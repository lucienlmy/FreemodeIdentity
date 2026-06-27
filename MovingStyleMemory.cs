using System;
using GTA;

namespace FreemodeIdentity {
	// Reads the ped's active moving style (movement clipset) out of live memory on GTA V
	// Enhanced. SET_PED_MOVEMENT_CLIPSET has no getter native, and the engine stores only the
	// clipset's joaat HASH (the source name string is discarded), so the moving style can't be
	// read back through the scripting API at all.
	//
	// The hash sits at a fixed structural offset chain rooted at the ped, derived and verified
	// with a diagnostic probe (since removed; see git history) across male+female peds, multiple styles,
	// and a game RESTART — the offsets held identical every time:
	//
	//     styleHash = *(*(*(ped + 0x10A0) + 0x388) + 0x20) + 0xE0
	//
	//   ped + 0x10A0 = CPedIntelligence pointer (Enhanced; matches the SHVDN source's
	//                  PedIntelligenceOffset). +0x388 -> +0x20 reaches the on-foot motion task;
	//                  +0xE0 is the active movement-clipset id (a joaat hash).
	//
	// The hash is then resolved back to a clipset NAME via MovingStyles (we hash a table of
	// known names and match) — the only way back from a stored hash, since the engine keeps
	// no reverse table. An unrecognised hash yields null and the moving style is not captured.
	//
	// SAFETY: every dereference is MemScan VirtualQuery-gated. We read WITHOUT modifying the
	// style (no SET call), so the motion task is not reallocated and the chain stays valid. If
	// any hop is unreadable the read aborts and returns null — a bad read never corrupts a
	// snapshot.
	static class MovingStyleMemory {
		const int PedIntelligenceOffset = 0x10A0;
		// Pointer hops from the intelligence object to the motion-task block, then the field
		// offset of the clipset hash within it. Verified cross-restart (see class header).
		static readonly int[] Chain = { 0x388, 0x20 };
		const int ClipsetHashOffset = 0xE0;

		// Returns the active moving-style NAME for the ped, or null if there is no override /
		// it can't be read / the hash isn't a known style. Never throws.
		public static string TryGetMovingStyle(Ped ped) {
			try {
				if (ped == null || !ped.Exists() || ped.MemoryAddress == IntPtr.Zero) {
					return null;
				}
				IntPtr cur = MemScan.SafeReadPtr(ped.MemoryAddress + PedIntelligenceOffset);
				foreach (int off in Chain) {
					if (cur == IntPtr.Zero) {
						return null;
					}
					cur = MemScan.SafeReadPtr(cur + off);
				}
				if (cur == IntPtr.Zero) {
					return null;
				}
				uint hash = MemScan.ReadUInt32(cur + ClipsetHashOffset);
				if (hash == 0) {
					return null; // no override / model default
				}
				return MovingStyles.ResolveHash(hash);
			} catch (Exception e) {
				Logger.LogError("MovingStyleMemory.TryGetMovingStyle: " + e);
				return null;
			}
		}
	}
}
