using System;
using System.Runtime.InteropServices;

namespace FreemodeIdentity {
	// Bridge to the native spend-shim (FreemodeIdentity.asi). SHVDN can't hook native
	// handlers, so the shim does the STAT spend-redirect; this side drives it. The shim
	// exports FreemodeIdentity_GetState() returning a pointer to a shared block:
	//
	//   struct { int version; int redirectEnabled; int activeStat; int balance; int pendingDelta;
	//            int logLevel; ulong decorationBase; ulong waypointInfoArray[4]; }
	//
	// C# is the authority: we write redirectEnabled / activeStat / balance. Money events come
	// back as `pendingDelta` — a signed change the shim ACCUMULATES (shop debit < 0, script
	// payout > 0) which we apply + zero each tick. If the shim isn't loaded (user hasn't
	// installed the .asi), Available is false and the wallet still earns — only in-shop
	// spending / redirected payouts are unavailable.
	internal sealed class ShimBridge {
		const string AsiName = "FreemodeIdentity.asi";
		const int ExpectedVersion = 3;

		// Field byte offsets in the shared struct. Six 4-byte ints, then 8-byte pointers on their
		// natural 8-byte boundary (even int count before the first ⇒ no padding): decorationBase, then
		// the four waypointInfoArray entry addresses contiguously.
		const int OffVersion = 0;
		const int OffRedirect = 4;
		const int OffActiveStat = 8;
		const int OffBalance = 12;
		const int OffPendingDelta = 16;
		const int OffLogLevel = 20;
		const int OffDecorationBase = 24;
		const int OffWaypointArray = 32; // four 8-byte addresses at 32/40/48/56
		const int StateSize = 64;

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
		// protagonist whose stat is activeStat. logLevel drives the shim's trace (0=Info,1=Debug).
		public void Push(bool redirect, int activeStat, int balance, int logLevel) {
			if (state == IntPtr.Zero) return;
			Marshal.WriteInt32(state, OffRedirect, redirect ? 1 : 0);
			Marshal.WriteInt32(state, OffActiveStat, activeStat);
			Marshal.WriteInt32(state, OffBalance, balance);
			Marshal.WriteInt32(state, OffLogLevel, logLevel);
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
