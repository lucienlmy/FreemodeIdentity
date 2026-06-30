using System;
using System.Runtime.InteropServices;

namespace FreemodeIdentity {
	// Bridge to the native spend-shim (FreemodeIdentity.asi). SHVDN can't hook native
	// handlers, so the shim does the STAT spend-redirect; this side drives it. The shim
	// exports FreemodeIdentity_GetState() returning a pointer to a shared block:
	//
	//   struct { int version; int redirectEnabled; int activeStat; int activeBankStat; int balance;
	//            int pendingDelta; int logLevel; int reserved;
	//            int skillsPinned; int skillHashes[7]; int skillValues[7]; int reserved2;
	//            ulong decorationBase; ulong waypointInfoArray[4]; }
	//
	// C# is the authority: we write redirectEnabled / activeStat / balance, and the skill profile
	// (skillsPinned + the active SP{N} hashes + values). Money events come back as `pendingDelta` — a
	// signed change the shim ACCUMULATES (shop debit < 0, script payout > 0) which we apply + zero each
	// tick. If the shim isn't loaded (user hasn't installed the .asi), Available is false and the
	// wallet still earns — only in-shop spending / redirected payouts / skill pinning are unavailable.
	internal sealed class ShimBridge {
		const string AsiName = "FreemodeIdentity.asi";
		const int ExpectedVersion = 5;
		const int SkillCount = 7; // must match SHIM_SKILL_COUNT on the native side

		// Field byte offsets in the shared struct. The int32 region (an even count so the pointers stay
		// 8-aligned): eight wallet/log ints, then the skill block (pinned + 7 hashes + 7 values + a pad),
		// then the 8-byte pointers: decorationBase, then the four waypointInfoArray entries contiguously.
		const int OffVersion = 0;
		const int OffRedirect = 4;
		const int OffActiveStat = 8;
		const int OffActiveBankStat = 12;
		const int OffBalance = 16;
		const int OffPendingDelta = 20;
		const int OffLogLevel = 24;
		// 28 = reserved pad
		const int OffSkillsPinned = 32;
		const int OffSkillHashes = 36;  // 7 ints at 36..60
		const int OffSkillValues = 64;  // 7 ints at 64..88
		// 92 = reserved2 pad
		const int OffDecorationBase = 96;
		const int OffWaypointArray = 104; // four 8-byte addresses at 104/112/120/128
		const int StateSize = 136;

		[DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
		static extern IntPtr GetModuleHandle(string name);

		[DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
		static extern IntPtr GetProcAddress(IntPtr module, string name);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		delegate IntPtr GetStateFn();

		IntPtr state = IntPtr.Zero;
		string lastFailure; // last logged TryConnect failure — dedupes the per-tick retry

		public bool Available => state != IntPtr.Zero;

		// Log a connect failure ONCE per distinct reason (TryConnect runs every tick, so the
		// shim being absent must not spam the log). A new reason supersedes the old.
		bool Fail(string reason) {
			if (reason != lastFailure) {
				Logger.LogError("ShimBridge: " + reason);
				lastFailure = reason;
			}
			return false;
		}

		// Resolve the shared-state pointer from the loaded .asi. The shim is loaded by
		// ScriptHookV at game start, so by the time the script ticks it's present (if the
		// user installed it). Safe to retry: call until Available.
		public bool TryConnect() {
			if (state != IntPtr.Zero) {
				return true;
			}
			IntPtr module = GetModuleHandle(AsiName);
			if (module == IntPtr.Zero) {
				return Fail("FreemodeIdentity.asi not loaded — install the .asi in the game root to enable spending.");
			}
			IntPtr proc = GetProcAddress(module, "FreemodeIdentity_GetState");
			if (proc == IntPtr.Zero) {
				return Fail("FreemodeIdentity_GetState export missing — .asi version mismatch with this DLL.");
			}
			var fn = Marshal.GetDelegateForFunctionPointer<GetStateFn>(proc);
			IntPtr p = fn();
			if (p == IntPtr.Zero || !MemScan.IsReadable(p, StateSize)) {
				return Fail("shared-state pointer null/unreadable — shim failed to initialise.");
			}
			int version = Marshal.ReadInt32(p, OffVersion);
			if (version != ExpectedVersion) {
				return Fail($"state version mismatch (.asi={version}, expected {ExpectedVersion}) — rebuild/redeploy both halves.");
			}
			state = p;
			lastFailure = null;
			Logger.Log("ShimBridge: connected to native spend-shim.");
			return true;
		}

		// Push the current feature state + balance to the shim before each potential shop
		// interaction. redirect=true only when the wallet is enabled AND spoofed to the
		// protagonist whose stats are activeStat (cash) + activeBankStat (bank — some mods charge
		// the bank instead). logLevel drives the shim's trace (0=Info,1=Debug).
		public void Push(bool redirect, int activeStat, int activeBankStat, int balance, int logLevel) {
			if (state == IntPtr.Zero) return;
			Marshal.WriteInt32(state, OffRedirect, redirect ? 1 : 0);
			Marshal.WriteInt32(state, OffActiveStat, activeStat);
			Marshal.WriteInt32(state, OffActiveBankStat, activeBankStat);
			Marshal.WriteInt32(state, OffBalance, balance);
			Marshal.WriteInt32(state, OffLogLevel, logLevel);
		}

		// Push the skill profile the shim pins. `pin` is true only when Skills is enabled AND spoofed
		// (so a genuine protagonist is never masked). `hashes`/`values` are the active SP{N} skill
		// hashes for the spoof target and the chosen 0..100 values, both length SkillCount. When pin is
		// false the shim ignores the rest, but we still write a cleared set so a stale profile can't
		// linger. No-op if the shim isn't connected (skills then can't be pinned — they won't hold).
		public void PushSkills(bool pin, int[] hashes, int[] values) {
			if (state == IntPtr.Zero) return;
			Marshal.WriteInt32(state, OffSkillsPinned, pin ? 1 : 0);
			for (int i = 0; i < SkillCount; i++) {
				Marshal.WriteInt32(state, OffSkillHashes + i * 4, hashes != null && i < hashes.Length ? hashes[i] : 0);
				Marshal.WriteInt32(state, OffSkillValues + i * 4, values != null && i < values.Length ? values[i] : 0);
			}
		}

		// The ped-decoration array base the shim resolved from the live .text (0 if it couldn't, or
		// the shim isn't connected). C# uses this on Enhanced — where it can't pattern-scan the
		// encrypted .text itself. Returns IntPtr.Zero when unavailable, in which case tattoos are
		// simply skipped this snapshot (no fallback that could touch the ped).
		public IntPtr DecorationBase {
			get {
				if (state == IntPtr.Zero) return IntPtr.Zero;
				return (IntPtr)Marshal.ReadInt64(state, OffDecorationBase);
			}
		}

		// The four Enhanced WaypointInfoArray entry addresses the shim resolved (all IntPtr.Zero if it
		// couldn't, or the shim isn't connected). C# re-keys the matching entry to follow the spoof —
		// Enhanced only, since it can't pattern-scan the encrypted .text itself. See WaypointKeeper.
		public IntPtr[] WaypointEntries {
			get {
				var entries = new IntPtr[4];
				if (state == IntPtr.Zero) return entries;
				for (int i = 0; i < 4; i++) {
					entries[i] = (IntPtr)Marshal.ReadInt64(state, OffWaypointArray + i * 8);
				}
				return entries;
			}
		}

		// Read the shim's accumulated signed change (debit < 0 / payout > 0) and zero it. The
		// read-then-clear isn't atomic: a shim write landing in the ns-wide gap between the
		// read and the clear is lost. The shim only writes inside a STAT native on the game
		// thread, so that window is vanishingly rare — accepted over the cost of an interlocked
		// exchange across the native boundary. Returns 0 when not connected.
		public int ReadAndClearPendingDelta() {
			if (state == IntPtr.Zero) return 0;
			int delta = Marshal.ReadInt32(state, OffPendingDelta);
			if (delta != 0) {
				Marshal.WriteInt32(state, OffPendingDelta, 0);
			}
			return delta;
		}
	}
}
