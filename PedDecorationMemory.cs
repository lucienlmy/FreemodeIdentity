using System;
using System.Collections.Generic;
using GTA;

namespace FreemodeIdentity {
	// Reads the ped's applied decorations (tattoos / badges) from live memory.
	//
	// The game exposes ADD_PED_DECORATION_FROM_HASHES to apply a decoration but NO native to
	// enumerate the ones already on a ped — GET_PED_DECORATIONS_STATE returns only a whole-set
	// checksum, which is not reversible. So to preserve tattoos we read the list straight from
	// the engine's decoration store.
	//
	// Layout (FiveM's GET_PED_DECORATIONS, citizenfx/fivem#1467; confirmed on Enhanced with a
	// diagnostic probe, since removed — see git history): decorations are NOT inline in CPed.
	// A uint16 buffer-index in the CPed
	// indexes a global parallel array of fixed-size PedEntry records; each PedEntry holds an
	// OverlayEntry[87] plus a count.
	//   PedEntry  (size 0x7D8): OverlayEntry entries[87] @ +0xB8, uint32 count @ +0x784
	//   OverlayEntry (size 0x14): uint32 collectionHash @ +0, uint32 overlayHash @ +4
	//   CPed index: uint16 buffer-index @ ped + 0x2E8 (Enhanced; pinned across three probe runs).
	//
	// The array BASE is a page-aligned global allocation whose address CHANGES every launch, so
	// it can't be a constant and is too expensive to scan for inline (a full sweep froze the
	// game). DecorationBaseFinder discovers it ONCE per session in a tick-driven background pass
	// (anchored on a unique sentinel decoration) and hands it here via SetBase. TryFill is then a
	// pure, instant read — so Capture() stays synchronous. Until the base is set, TryFill returns
	// false (tattoos uncaptured; apply then won't clear them).
	static class PedDecorationMemory {
		public const int PedBufferIndexOffset = 0x2E8;
		public const int PedEntryStride = 0x7D8;
		public const int EntriesOffset = 0xB8;
		public const int CountOffset = 0x784;
		public const int OverlayStride = 0x14;
		public const int MaxOverlays = 87;

		// The session's decoration array base (address of PedEntry[0]), set once by
		// DecorationBaseFinder. IntPtr.Zero until discovered. PROVEN session-GLOBAL and stable: the
		// probe read the SAME base (2468E8A0000) for two different peds across a model switch (female
		// idx=5, male idx=2). It is ONE global PedEntry[] array; each ped owns a slot whose index lives
		// at ped+0x2E8 and varies per ped. So the base never goes stale — only the index does, and
		// TryFill reads that fresh every call. entry = base + index*stride.
		static IntPtr arrayBase = IntPtr.Zero;

		public static bool BaseKnown => arrayBase != IntPtr.Zero;

		public static IntPtr GetBase() {
			return arrayBase;
		}

		public static void SetBase(IntPtr baseAddr) {
			arrayBase = baseAddr;
		}

		// Reads the ped's (collection, overlay) decoration hash pairs into ad.Decorations.
		// Returns true only if the base is known AND the entry read; false leaves the list empty
		// and signals the caller not to clear tattoos on apply.
		public static bool TryFill(Ped ped, AppearanceData ad) {
			try {
				if (arrayBase == IntPtr.Zero || ped == null || !ped.Exists() || ped.MemoryAddress == IntPtr.Zero) {
					return false;
				}
				// The index is per-ped and read FRESH here (the global base is stable across peds, only
				// the slot index varies). entry = base + index*stride.
				ushort index = MemScan.ReadUInt16(ped.MemoryAddress + PedBufferIndexOffset);
				// Guard against an absurd index (a re-spawned/transitional ped can momentarily report
				// a wild buffer slot): base + index*stride could then point far outside the array.
				// long math avoids int overflow before the readability gate sees it.
				long entryAddr = arrayBase.ToInt64() + (long)index * PedEntryStride;
				IntPtr entry = (IntPtr)entryAddr;
				if (!MemScan.IsReadable(entry, PedEntryStride)) {
					return false;
				}
				uint count = MemScan.ReadUInt32(entry + CountOffset);
				if (count > MaxOverlays) {
					// Not a valid entry (stale base or bad index) — don't trust this read. Returning
					// false leaves Decorations empty + DecorationsFromMemory false, so apply won't
					// clear the ped's tattoos based on a bad read.
					return false;
				}
				ReadEntry(entry, count, ad.Decorations);
				return true; // count==0 is a valid "no tattoos" read, not a failure
			} catch (Exception e) {
				Logger.LogError("PedDecorationMemory.TryFill: " + e);
				return false;
			}
		}

		// Reads the `count` overlay slots at an entry into `into`, skipping empty (zero) slots.
		public static void ReadEntry(IntPtr entry, uint count, List<DecorationData> into) {
			for (int i = 0; i < count; i++) {
				IntPtr o = entry + EntriesOffset + i * OverlayStride;
				uint collection = MemScan.ReadUInt32(o + 0);
				uint overlay = MemScan.ReadUInt32(o + 4);
				if (collection == 0 || overlay == 0) {
					continue;
				}
				into.Add(new DecorationData(collection, overlay));
			}
		}
	}
}
