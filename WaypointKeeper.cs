using System;
using GTA;

namespace FreemodeIdentity {
	// Keeps the map waypoint visible across an identity spoof. The game stores waypoints in a small
	// WaypointInfoArray keyed BY PED MODEL HASH: each entry is { int modelHash; int blipHandle; ... }
	// and GET_WAYPOINT_BLIP returns the entry whose modelHash matches the player ped's current model.
	// Our spoof overwrites that model hash (freemode -> protagonist), so the lookup misses while held
	// and the waypoint vanishes from the minimap (the blip data still sits under the freemode hash).
	//
	// The fix re-keys the existing entry instead of creating a new one (SET_NEW_WAYPOINT spawns a
	// duplicate): when the spoof engages, rewrite the freemode-keyed entry's modelHash to the
	// protagonist hash so the lookup hits; on release, rewrite it back. Same single blip throughout.
	//
	// Layout (from SHVDN NativeMemory): each entry has modelHash at +0x00, blipHandle at +0x04.
	// Legacy keeps a contiguous range (stride 0x18), resolved here by Game.FindPattern (plaintext
	// .text). Enhanced unrolls it into four separate entry globals whose addresses the native shim
	// resolves from the decrypted .text and hands over (Game.FindPattern can't read it).
	static class WaypointKeeper {
		const int EntryStride = 0x18;
		const int ModelHashOffset = 0x00;

		// Legacy WaypointInfoArray bounds patterns (SHVDN NativeMemory.cs). Start and end are each a
		// rip-relative lea; resolve target = *(int*)(hit+3) + hit + 7.
		const string LegacyStartPattern = "4C 8D 05 ?? ?? ?? ?? 74 07 B8 ?? ?? ?? ?? EB 2D 33 C0";
		const string LegacyEndPattern = "48 8D 15 ?? ?? ?? ?? 48 83 C1 ?? FF C0 48 3B CA 7C EA 32 C0";

		static bool attempted;
		static IntPtr legacyStart;
		static IntPtr legacyEnd;
		static IntPtr[] enhancedEntries; // the 4 entry addresses the shim published (Enhanced only)

		// Resolve the array once. Legacy scans its own (plaintext .text); Enhanced takes the four entry
		// addresses the shim resolved (passed in — 0 in any slot means the shim missed or isn't loaded).
		static bool Resolve(IntPtr[] shimEntries) {
			if (legacyStart != IntPtr.Zero || enhancedEntries != null) return true;
			if (attempted) return false;

			if (GameBuild.IsEnhanced) {
				if (shimEntries == null || shimEntries[0] == IntPtr.Zero) {
					return false; // shim not connected / scan missed yet — retry next flip, don't latch
				}
				attempted = true;
				enhancedEntries = shimEntries;
				Logger.LogDebug($"WaypointKeeper: WaypointInfoArray entries (shim) @ {shimEntries[0].ToInt64():X} {shimEntries[1].ToInt64():X} {shimEntries[2].ToInt64():X} {shimEntries[3].ToInt64():X}.");
				return true;
			}

			attempted = true;
			IntPtr startHit = Game.FindPattern(LegacyStartPattern);
			IntPtr endHit = Game.FindPattern(LegacyEndPattern);
			if (startHit == IntPtr.Zero || endHit == IntPtr.Zero) {
				Logger.Log("WaypointKeeper: WaypointInfoArray pattern not found — waypoint won't follow the spoof.");
				return false;
			}
			legacyStart = (IntPtr)(startHit.ToInt64() + 7 + MemScan.ReadInt32(startHit + 3));
			legacyEnd = (IntPtr)(endHit.ToInt64() + 7 + MemScan.ReadInt32(endHit + 3));
			Logger.LogDebug($"WaypointKeeper: WaypointInfoArray @ {legacyStart.ToInt64():X}..{legacyEnd.ToInt64():X}.");
			return true;
		}

		// Rewrite an entry whose modelHash == fromHash to toHash. Returns true if one was rewritten.
		static bool ReKeyEntry(IntPtr entry, uint fromHash, uint toHash) {
			if (MemScan.ReadUInt32(entry + ModelHashOffset) == fromHash) {
				return MemScan.WriteUInt32(entry + ModelHashOffset, toHash);
			}
			return false;
		}

		// Re-key the entry currently keyed to `fromHash` so it reads as `toHash`. No-op if the array
		// isn't resolved or no entry matches (no waypoint set). Walks the contiguous range on Legacy,
		// the four shim-published entries on Enhanced.
		static bool ReKey(uint fromHash, uint toHash, IntPtr[] shimEntries) {
			if (fromHash == 0 || toHash == 0 || fromHash == toHash) return false;
			if (!Resolve(shimEntries)) return false;
			if (enhancedEntries != null) {
				foreach (IntPtr e in enhancedEntries) {
					if (ReKeyEntry(e, fromHash, toHash)) return true;
				}
				return false;
			}
			for (IntPtr e = legacyStart; e.ToInt64() < legacyEnd.ToInt64(); e += EntryStride) {
				if (ReKeyEntry(e, fromHash, toHash)) return true;
			}
			return false;
		}

		// Call on each spoof state flip. When the spoof engages, the entry the user set as freemode is
		// re-keyed to the protagonist hash so the lookup finds it while spoofed; on release it's keyed
		// back. freemodeHash is the player's real freemode model, spoofHash the held protagonist hash.
		// shimEntries are the Enhanced array addresses from the shim (ignored on Legacy).
		public static void OnSpoofFlip(bool held, uint freemodeHash, uint spoofHash, IntPtr[] shimEntries) {
			bool rekeyed = held ? ReKey(freemodeHash, spoofHash, shimEntries) : ReKey(spoofHash, freemodeHash, shimEntries);
			if (rekeyed) {
				Logger.LogDebug($"WaypointKeeper: re-keyed waypoint {(held ? $"{freemodeHash:X8}->{spoofHash:X8}" : $"{spoofHash:X8}->{freemodeHash:X8}")}.");
			}
		}
	}
}
