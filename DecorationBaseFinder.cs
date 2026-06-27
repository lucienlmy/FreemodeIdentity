using System;
using System.Collections.Generic;
using System.Diagnostics;
using GTA;
using GTA.Native;

namespace FreemodeIdentity {
	// One-time, tick-driven discovery of the per-session decoration-array base for
	// PedDecorationMemory. The base is a page-aligned global allocation whose address changes
	// every launch, so it can't be a constant. Runs in time-bounded chunks across ticks (SHVDN
	// aborts any tick > 5s; Script.Wait does NOT reset that watchdog on API ≥3.7) and caches the
	// result.
	//
	// METHOD: we already know the player's buffer INDEX (ped+0x2E8) and that the base is
	// page-aligned, so we test only PAGE STARTS — not every 4 bytes — making this sub-second (an
	// earlier full byte-sweep of all writable memory took ~20s and was the "very slow" symptom).
	// APPEND a unique sentinel decoration to the player (without clearing their real ones), then for
	// each page-aligned candidate base check whether base + index*stride is a live PedEntry that
	// CONTAINS the sentinel. The sentinel-containment check makes the match unambiguous (no
	// network/staging false hits). Then remove the sentinel, re-applying the player's real
	// decorations read back via the now-known base, so the ped is left exactly as before. Every
	// dereference is VirtualQuery-gated, so a wrong candidate can never fault.
	static class DecorationBaseFinder {
		// TWO unique sentinel overlays the player is unlikely to wear, appended (not cleared) so the
		// real tattoo is preserved. Two is deliberate: the locator matches them as a CONSECUTIVE
		// OverlayEntry PAIR exactly OverlayStride (0x14) apart — that signature uniquely identifies the
		// real global PedEntry array and rejects a decoy structure (a ped-creation DTO that also holds
		// the sentinel pair but at a 0x68 stride). Hashes via joaat (match the DurtyFree dump). Overlays
		// are gender-specific — a male overlay no-ops on a female ped — so pick by the ped's model.
		const string SentinelCollection = "mpbeach_overlays";
		static readonly string[] MaleSentinelOverlays = { "MP_Bea_M_Back_000", "MP_Bea_M_Chest_000" };
		static readonly string[] FemaleSentinelOverlays = { "MP_Bea_F_Back_000", "MP_Bea_F_Chest_000" };

		// Per-tick wall-clock slice. Discovery is a deliberate user action (a snapshot they're waiting
		// on, menu open, game unusable), so spend a big slice per tick to finish fast — a brief heavy
		// hitch rather than a long grind. 1000ms is well under SHVDN's 5s tick watchdog (the loop does
		// no Script.Wait, so only its own work counts).
		const long TickBudgetMs = 1000;

		// Hard ceiling on TOTAL bytes scanned across the whole sweep (across ticks). When the sentinel
		// ADD doesn't form a findable pair (e.g. a Menyoo ped: state 2->2, only lone hits), the base is
		// never solvable, so without a cap the sweep grinds EVERY region — minutes of frozen menu. A
		// good ped's array is found well within this budget (it lives near the ped's own arena), so this
		// bounds the failure case without cutting a real find short. 512MB ≈ a ~20s worst-case fail.
		const long MaxScanBytes = 512L << 20; // 512MB

		enum Phase { Idle, Apply, Scan, Done }

		static Phase phase = Phase.Idle;
		static Ped ped;
		static IntPtr pedAddr;
		static uint collHash;
		static uint overlay0;   // first sentinel overlay hash
		static uint overlay1;   // second sentinel overlay hash
		static List<MemScan.Region> regions;
		static int regionIdx;
		static int loneHits; // diagnostic: single-sentinel (collHash,o0) hits seen without the consecutive o1

		public static bool IsRunning => phase != Phase.Idle && phase != Phase.Done;


		public static void Begin(Ped player) {
			if (IsRunning) {
				return; // a sweep is already in progress
			}
			if (PedDecorationMemory.BaseKnown) {
				return; // base already found this session; the per-ped index is read fresh at capture
			}
			if (player == null || !player.Exists() || player.MemoryAddress == IntPtr.Zero) {
				Logger.Log("DecorationBaseFinder: no live ped — cannot enable tattoo capture.");
				return;
			}
			if (!PedAppearance.IsFreemode(player)) {
				Logger.Log("DecorationBaseFinder: not a freemode ped — tattoo capture only applies to the MP character.");
				return;
			}
			ped = player;
			pedAddr = player.MemoryAddress;
			// NO clean-state skip. GET_PED_DECORATIONS_STATE is UNRELIABLE on this Enhanced build — it
			// reads the "clean" baseline (2) on a ped with a visible tattoo (proven in-game), so it
			// can't tell us whether to run. We ALWAYS attempt discovery when the base isn't known; a
			// genuinely clean ped just pays one sentinel-anchored sweep that finds nothing. We do NOT
			// latch a permanent "failed" flag: discovery can flake intermittently (the same ped has
			// failed once then succeeded), so a failed sweep just means "no tattoos this snapshot" and
			// the next snapshot retries. The sweep itself is content-based and reliable.
			bool female = player.Model.Hash == new Model(PedAppearance.FemaleModel).Hash;
			string[] overlays = female ? FemaleSentinelOverlays : MaleSentinelOverlays;
			collHash = Joaat.Hash(SentinelCollection);
			overlay0 = Joaat.Hash(overlays[0]);
			overlay1 = Joaat.Hash(overlays[1]);
			phase = Phase.Apply;
			Logger.Log("DecorationBaseFinder: starting decoration-array base discovery.");
		}

		public static void Tick() {
			try {
				switch (phase) {
					case Phase.Apply:
						// Append TWO sentinels WITHOUT clearing the player's real decorations (so a real
						// tattoo is preserved — we match the two as a consecutive 0x14-apart OverlayEntry
						// pair, which is what uniquely identifies the real array).
						uint stateBefore = Function.Call<uint>(Hash.GET_PED_DECORATIONS_STATE, ped);
						Function.Call(Hash.ADD_PED_DECORATION_FROM_HASHES, ped, collHash, overlay0);
						Function.Call(Hash.ADD_PED_DECORATION_FROM_HASHES, ped, collHash, overlay1);
						Script.Wait(250); // let the engine commit them into the store (the probe used 250ms reliably)
						uint stateAfter = Function.Call<uint>(Hash.GET_PED_DECORATIONS_STATE, ped);
						// We don't GATE on GET_PED_DECORATIONS_STATE (it is unreliable on Enhanced), but logging
						// before/after tells us whether ADD committed at all — key on Menyoo peds where ADD may
						// no-op (the sentinels then never land and the sweep finds nothing).
						ushort dbgIndex = MemScan.ReadUInt16(pedAddr + PedDecorationMemory.PedBufferIndexOffset);
						Logger.Log($"DecorationBaseFinder: sentinels ADD (coll={collHash:X8} o0={overlay0:X8} o1={overlay1:X8}); bufferIndex={dbgIndex}, state {stateBefore:X8}->{stateAfter:X8}. Sweeping for the consecutive pair.");
						// privateOnly: the global decoration array is a private heap allocation, never a mapped
						// file/image — filtering to MEM_PRIVATE skips a large slab of writable image data
						// sections. Then scan the LARGEST regions first: the array is a big dedicated pool
						// allocation, so it lives in a large region; small regions almost never hold it. We
						// enumerate with true (unchunked) region sizes to sort, then chunk during the scan so
						// the per-tick budget and buffers stay bounded. Stop at first match → order is pure
						// speed, never correctness.
						regions = new List<MemScan.Region>(MemScan.EnumerateRegions(long.MaxValue, true, true));
						regions.Sort((a, b) => b.Size.CompareTo(a.Size));
						regionIdx = 0;
						scanOff = 0;
						loneHits = 0;
						bytesScanned = 0;
						phase = Phase.Scan;
						break;
					case Phase.Scan:
						if (StepScan()) {
							phase = Phase.Done;
						}
						break;
				}
			} catch (Exception e) {
				Logger.LogError("DecorationBaseFinder tick failed: " + e);
				CleanupSentinel();
				phase = Phase.Done;
			}
		}

		// Discovery sweep. Scans writable memory BY CONTENT for our TWO sentinels as a CONSECUTIVE
		// OverlayEntry pair: (collHash, overlay0) at some offset and (collHash, overlay1) exactly
		// OverlayStride (0x14) later. That 0x14-apart signature is what the real array has and a decoy
		// (different stride) does not. The sentinels were APPENDED (not cleared) so they sit at slots
		// [realCount, realCount+1] — NOT slot 0 — so we don't assume the slot: we solve for the PedEntry
		// + base by trying each slot k and requiring the derived base be 16KB-aligned (only the real
		// global array is). Tick-sliced (resume from regionIdx).
		// Largest region may be many MB/GB (we enumerate unchunked to sort by size), so scan it in
		// bounded slices: cap the per-read buffer, carry an overlap so a sentinel pair straddling a
		// slice boundary isn't missed, and persist the in-region cursor (scanOff) so the tick budget
		// can yield mid-region and resume. resume from (regionIdx, scanOff).
		const int ScanSliceBytes = 0x100000; // 1MB working buffer cap

		static long scanOff; // byte offset already scanned within regions[regionIdx]
		static long bytesScanned; // total bytes scanned this sweep, across ticks; for the MaxScanBytes cap

		static bool StepScan() {
			ushort index = MemScan.ReadUInt16(pedAddr + PedDecorationMemory.PedBufferIndexOffset);
			int stride = PedDecorationMemory.OverlayStride;
			int overlap = 8 + stride; // a pair spans collHash(4)+o0(4)+collHash(4)+o1(4) with stride gap
			var sw = Stopwatch.StartNew();
			while (regionIdx < regions.Count) {
				if (bytesScanned >= MaxScanBytes) {
					Logger.Log($"DecorationBaseFinder: gave up after {bytesScanned >> 20}MB ({loneHits} lone-sentinel hit(s)) — sentinels likely didn't land; no tattoos this snapshot, next snapshot retries.");
					CleanupSentinel();
					return true;
				}
				MemScan.Region r = regions[regionIdx];
				long remaining = r.Size - scanOff;
				if (remaining <= 0) {
					regionIdx++;
					scanOff = 0;
					continue;
				}
				if (sw.ElapsedMilliseconds >= TickBudgetMs) {
					return false; // resume next tick from (regionIdx, scanOff)
				}
				int sliceLen = (int)Math.Min(ScanSliceBytes, remaining);
				IntPtr sliceBase = (IntPtr)(r.Base.ToInt64() + scanOff);
				bytesScanned += sliceLen;
				if (ScanSlice(sliceBase, sliceLen, index)) {
					return true; // base found + armed, sentinels cleaned up
				}
				// Advance, but step back by `overlap` so a pair split across this slice's tail and the
				// next slice's head is still seen. Never advance past the region end.
				long advance = sliceLen - overlap;
				if (advance < 1) {
					advance = sliceLen; // tiny final slice — just finish it
				}
				scanOff += advance;
			}
			Logger.Log($"DecorationBaseFinder: array base not found ({regions.Count} regions swept, {loneHits} lone-sentinel hit(s)); no tattoos captured this snapshot — the next snapshot will retry.");
			CleanupSentinel();
			return true;
		}

		// Scan one bounded slice for the consecutive sentinel pair, then solve for the PedEntry +
		// 16KB-aligned base. Returns true once the real base is found and armed.
		static bool ScanSlice(IntPtr sliceBase, int len, ushort index) {
			byte[] buf = MemScan.Snapshot(sliceBase, len);
			int stride = PedDecorationMemory.OverlayStride;
			for (int off = 0; off + 8 + stride <= buf.Length; off += 4) {
				// First sentinel OverlayEntry: (collHash, overlay0).
				if (BitConverter.ToUInt32(buf, off) != collHash ||
					BitConverter.ToUInt32(buf, off + 4) != overlay0) {
					continue;
				}
				// Second sentinel must follow exactly one OverlayStride later: (collHash, overlay1).
				if (BitConverter.ToUInt32(buf, off + stride) != collHash ||
					BitConverter.ToUInt32(buf, off + stride + 4) != overlay1) {
					// A lone first-sentinel hit (no consecutive o1). Counted so a failed run can tell us
					// whether the sentinels landed at all (lone hits > 0 = ADD committed but not adjacent;
					// 0 = ADD likely no-op'd, e.g. on a custom/Menyoo ped).
					loneHits++;
					continue;
				}
				IntPtr firstEntry = (IntPtr)(sliceBase.ToInt64() + off); // OverlayEntry of the first sentinel
				if (TrySolveBase(firstEntry, index)) {
					return true;
				}
			}
			return false;
		}

		// Given the address of the first sentinel's OverlayEntry, solve for the PedEntry and the global
		// array base. The sentinel sits at some slot k (we appended onto existing decorations, so k is
		// the prior count, unknown). For each plausible k: pedEntry = firstEntry - EntriesOffset -
		// k*stride; base = pedEntry - index*stride. Accept the k whose base is 16KB-aligned AND whose
		// count field is plausible — that's the real global array (the decoy never yields an aligned
		// base). Arms PedDecorationMemory and cleans up on success.
		static bool TrySolveBase(IntPtr firstEntry, ushort index) {
			for (int k = 0; k < PedDecorationMemory.MaxOverlays; k++) {
				long pedEntryL = firstEntry.ToInt64() - PedDecorationMemory.EntriesOffset - (long)k * PedDecorationMemory.OverlayStride;
				IntPtr pedEntry = (IntPtr)pedEntryL;
				long baseL = pedEntryL - (long)index * PedDecorationMemory.PedEntryStride;
				if ((baseL & 0x3FFF) != 0) {
					continue; // not the page-aligned global array for this slot guess
				}
				if (!MemScan.IsReadable(pedEntry, PedDecorationMemory.PedEntryStride)) {
					continue;
				}
				uint count = MemScan.ReadUInt32(pedEntry + PedDecorationMemory.CountOffset);
				// The sentinel sits at slot k, so the count must be at least k+2 (k priors + 2 sentinels)
				// and within bounds. This pins the right k.
				if (count < (uint)k + 2 || count > PedDecorationMemory.MaxOverlays) {
					continue;
				}
				IntPtr baseAddr = (IntPtr)baseL;
				PedDecorationMemory.SetBase(baseAddr); // session-global base (stable across peds; only the index varies)
				Logger.Log($"DecorationBaseFinder: decoration array base @ {baseAddr.ToInt64():X} " +
					$"(pedEntry @ {pedEntry.ToInt64():X}, index {index}, slot {k}, count {count}). Armed.");
				// Remove our sentinels, restoring the player's real decorations (read back via the now-known
				// base). Capture from here on uses PedDecorationMemory.TryFill (base + the ped's own fresh
				// 0x2E8 index), correct for this and every later ped this session.
				CleanupSentinel();
				return true;
			}
			return false;
		}

		// Remove the sentinel we appended while PRESERVING the player's real decorations. With the base
		// known we surgically drop just the sentinel pair (read the entry, clear, re-apply the reals).
		// With the base UNKNOWN (discovery failed) we can't read the reals back, so behaviour splits on
		// whether our sentinel actually LANDED — loneHits, incremented during the sweep each time the
		// first sentinel's OverlayEntry was seen in memory:
		//   - loneHits == 0 → the ADD no-op'd (e.g. a Menyoo ped); nothing of ours is on the ped, so
		//     there is nothing to remove — leave the ped untouched (a blind CLEAR here would wipe real
		//     tattoos for no reason).
		//   - loneHits  > 0 → our sentinel landed (a visible stray beach tattoo) but we couldn't find the
		//     base; CLEAR_PED_DECORATIONS to remove it. CLEAR is all-or-nothing (no per-decoration
		//     native), so on the rare ped that ALSO has real tattoos this clears those from the LIVE ped
		//     too — but those reals weren't captured this snapshot anyway (base unknown ⇒
		//     DecorationsFromMemory false), and a stray tattoo the user didn't ask for is the worse
		//     everyday outcome than re-applying a saved look that re-adds the reals.
		static void CleanupSentinel() {
			try {
				if (!PedDecorationMemory.BaseKnown) {
					if (loneHits == 0) {
						Logger.Log("DecorationBaseFinder: base unknown and sentinel never landed (ADD no-op) — nothing to clean.");
						return;
					}
					Logger.Log($"DecorationBaseFinder: base unknown but sentinel landed ({loneHits} hit(s)) — clearing decorations to remove the stray sentinel.");
					Function.Call(Hash.CLEAR_PED_DECORATIONS, ped);
					return;
				}
				var all = new List<DecorationData>();
				ushort index = MemScan.ReadUInt16(pedAddr + PedDecorationMemory.PedBufferIndexOffset);
				IntPtr entry = PedDecorationMemory.GetBase() + index * PedDecorationMemory.PedEntryStride;
				uint count = MemScan.ReadUInt32(entry + PedDecorationMemory.CountOffset);
				if (count <= PedDecorationMemory.MaxOverlays) {
					PedDecorationMemory.ReadEntry(entry, count, all);
				}
				Function.Call(Hash.CLEAR_PED_DECORATIONS, ped);
				foreach (DecorationData d in all) {
					if (d.CollectionHash == collHash && (d.OverlayHash == overlay0 || d.OverlayHash == overlay1)) {
						continue; // drop our two sentinels
					}
					Function.Call(Hash.ADD_PED_DECORATION_FROM_HASHES, ped, d.CollectionHash, d.OverlayHash);
				}
			} catch (Exception e) {
				Logger.LogError("DecorationBaseFinder.CleanupSentinel: " + e);
			}
		}
	}
}
