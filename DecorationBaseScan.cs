using System;
using GTA;

namespace FreemodeIdentity {
	// Resolves the decoration-array base by PATTERN — instant, and the only decoration base-finder.
	// FiveM resolves a global pointer to the PedEntry array (EntityExtraNatives.cpp, PR #1467) and
	// derefs it once. Two ways in, tried in order:
	//   1. The native shim — it scans the LIVE (decrypted) .text, the only way on Enhanced, whose
	//      .text is encrypted on disk so Game.FindPattern can't read it. The shim publishes the
	//      resolved base in the shared block; we just read it.
	//   2. Game.FindPattern — works where .text is plaintext (Legacy), so it still resolves there
	//      even if the .asi isn't installed.
	// If neither yields a base, tattoos are skipped this snapshot (the old content sweep that added
	// a probe tattoo and could wipe real ones is gone — a miss now never touches the ped).
	static class DecorationBaseScan {
		// FiveM global-pointer pattern. The rip-relative operand is at match+3 (instr len 7);
		// dereferencing the resolved address once yields the PedEntry array base.
		const string GlobalPtrPattern = "4C 03 05 ?? ?? ?? ?? EB 03 4D 8B C3";

		static bool attempted;

		// Try to resolve and arm PedDecorationMemory's base. shimBase is the base the native shim
		// resolved (IntPtr.Zero if the shim isn't connected or its scan missed). Returns true if
		// armed (or already known); false means unresolved, so the caller skips tattoos this snapshot.
		// The Game.FindPattern path is attempted once per session — but a shim base arriving later (the
		// .asi connects a few ticks in) re-opens the attempt.
		public static bool TryArm(IntPtr shimBase) {
			if (PedDecorationMemory.BaseKnown) return true;

			// Shim path first — the only one that works on Enhanced. Not gated by `attempted`: the
			// shim may connect after our first try, and reading the block is cheap.
			if (shimBase != IntPtr.Zero) {
				PedDecorationMemory.SetBase(shimBase);
				Logger.Log($"DecorationBaseScan: decoration array base @ {shimBase.ToInt64():X} (from native shim).");
				return true;
			}

			if (attempted) return false;
			attempted = true;

			IntPtr hit = Game.FindPattern(GlobalPtrPattern);
			if (hit == IntPtr.Zero) {
				Logger.LogDebug("DecorationBaseScan: global-ptr pattern not found (encrypted .text?) and no shim base — skipping tattoos this snapshot.");
				return false;
			}
			int disp = MemScan.ReadInt32(hit + 3);
			IntPtr globalPtrAddr = (IntPtr)(hit.ToInt64() + 7 + disp);
			IntPtr arrayBase = MemScan.SafeReadPtr(globalPtrAddr); // one deref → PedEntry array base
			if (arrayBase == IntPtr.Zero) {
				Logger.LogDebug("DecorationBaseScan: array base null — skipping tattoos this snapshot.");
				return false;
			}
			PedDecorationMemory.SetBase(arrayBase);
			Logger.Log($"DecorationBaseScan: decoration array base @ {arrayBase.ToInt64():X} (pattern @ {hit.ToInt64():X}).");
			return true;
		}
	}
}
