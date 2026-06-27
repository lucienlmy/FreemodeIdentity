using System;
using System.Collections.Generic;
using System.Diagnostics;
using GTA;

namespace FreemodeIdentity {
	// Locates the ped's active mood (facial idle-anim override) in live memory on GTA V Enhanced.
	// SET_FACIAL_IDLE_ANIM_OVERRIDE has no getter native, and the engine stores only the clip's
	// joaat HASH (the source name string is discarded), so the mood can't be read back through
	// the scripting API at all — exactly like the moving style.
	//
	// WHY A CONTENT SCAN (not a fixed offset chain like the moving style): the route to the
	// facial-mood field through the intelligence task tree is ARRAY-INDEXED — it shifts every
	// session, and even the field's offset-within-its-task varied between probe runs, so no
	// positional chain is replayable (confirmed across male/female peds + restarts). What IS
	// build-stable is the LOCAL byte layout: the mood hash sits 0x10 after a fixed clip-buffer
	// marker (0x65726F0000000001), identical at that relative offset on every probe run.
	//
	//     [field - 0x10] qword = 0x65726F0000000001   (fixed clip-buffer marker)
	//     [field + 0x00] dword = the LIVE mood hash    <-- what we read
	//
	// WHY TICK-DRIVEN + DEFERRED (not a synchronous read inside Capture): the facial task churns
	// — changing a mood reallocates its blocks — so scanning it synchronously during a snapshot
	// races that churn and, on some peds, faulted the game (an unmapped page between the
	// readability check and the copy is an UNCATCHABLE access violation). Running the scan as a
	// tick-driven, time-sliced state machine off the snapshot hot path (the same pattern as
	// DecorationBaseFinder for tattoos) keeps each tick well under SHVDN's 5s watchdog AND keeps
	// the scan from racing a synchronous capture. BeginSnapshot kicks this off and defers the
	// capture; the tick loop completes the snapshot once Result is ready.
	//
	// SAFETY: every read is MemScan VirtualQuery-gated and snapshotted into managed buffers; a
	// wrong/unmapped address can never fault the scan itself.
	static class MoodMemory {
		const int PedIntelligenceOffset = 0x10A0;

		const ulong ClipBufferMarker = 0x65726F0000000001; // fixed marker qword at field-0x10
		const int MarkerOff = -0x10;
		const uint NormalMoodHash = 0xBD789759;            // joaat("mood_normal_1") = model default

		// BFS bounds. The facial task is within ~3 hops of CPedIntelligence; the live field is
		// reached within the first ~2000 blocks when a mood is set. The per-tick budget slices the
		// work so a single tick never approaches the 5s watchdog; the block cap bounds total work.
		const int MaxStructOff = 0x800;
		const int MaxHops = 3;
		// The live field, when a mood IS set, sits DEEPER in the facial task tree than the
		// head-blend struct (which is found at ~130-350 blocks) — a 800-block cap MISSED a real
		// mood, while 1200 found it. So cap at 2000 for safe margin: it reliably covers where the
		// field appears when a mood is set, and the no-mood case bails at 2000 (~1s, cheap now that
		// per-block work is a single snapshot, not a syscall per qword). This is a BLOCK-count cap
		// (not wall-clock), so it finds-or-doesn't identically on any CPU — a slow machine just
		// spreads the same 2000 blocks over more frames; it never false-misses what a fast CPU finds.
		const int MaxBlocks = 2000;
		// Per-tick wall-clock slice. The pointer reads are cheap now (WalkPointerGraph/this BFS read
		// children from a single snapshot, not a syscall per qword), so a modest slice clears the
		// whole capped scan in ~1-2 ticks — a brief blip, not the long FPS-tax a tiny slice caused
		// by dribbling the work across hundreds of frames.
		const long TickBudgetMs = 20;

		enum Phase { Idle, Scan, Done }

		static Phase phase = Phase.Idle;
		static Ped ped;
		static List<IntPtr> frontier;
		static HashSet<long> seen;
		static int hop;
		static int blocks;

		// Session cache: the live mood field's address is stable while the mood (and facial task)
		// is unchanged, so we try the last-found address first and reuse it on a cheap re-validation
		// (the marker still at -0x10 + a known mood at +0) — skipping the whole BFS. The field does
		// move when the mood changes, so a miss just falls through to a fresh scan.
		static long cachedPed;
		static IntPtr cachedField;

		// The resolved mood NAME for the last completed run, or null (no override / not located /
		// model default). Valid once IsRunning is false after a Begin.
		public static string Result { get; private set; }

		public static bool IsRunning => phase == Phase.Scan;

		// Called when mood preservation is OFF: clear any stale Result so capture stores no mood
		// and no scan runs this snapshot.
		public static void Disable() {
			Result = null;
			phase = Phase.Done;
		}

		// Start a tick-driven scan of the given ped's facial task tree for the active mood. If the
		// last-found field address still holds a valid mood fingerprint, reuse it instantly (no
		// BFS) — what keeps repeat/auto snapshots smooth.
		public static void Begin(Ped player) {
			Result = null;
			if (player == null || !player.Exists() || player.MemoryAddress == IntPtr.Zero) {
				phase = Phase.Done;
				return;
			}
			long pedKey = player.MemoryAddress.ToInt64();
			if (cachedPed == pedKey && cachedField != IntPtr.Zero) {
				uint h = ReadFieldIfValid(cachedField);
				if (h != 0) {
					Finish(h); // cache hit — reuse, no BFS
					return;
				}
			}
			IntPtr intel = MemScan.SafeReadPtr(player.MemoryAddress + PedIntelligenceOffset);
			if (intel == IntPtr.Zero) {
				phase = Phase.Done;
				return;
			}
			ped = player;
			cachedPed = pedKey;
			frontier = new List<IntPtr> { intel };
			seen = new HashSet<long> { intel.ToInt64() };
			hop = 0;
			blocks = 0;
			phase = Phase.Scan;
		}

		// Re-validate a cached field address: marker still at -0x10 and a recognised mood at +0 →
		// return that mood hash, else 0. One snapshot, no scan.
		static uint ReadFieldIfValid(IntPtr field) {
			byte[] b = MemScan.Snapshot((IntPtr)(field.ToInt64() + MarkerOff), 0x10 + 4);
			if (b.Length < 0x14 || BitConverter.ToUInt64(b, 0) != ClipBufferMarker) {
				return 0;
			}
			uint h = BitConverter.ToUInt32(b, 0x10);
			return FacialMoods.ResolveHash(h) != null ? h : 0;
		}

		// Advance the scan by one time-bounded slice. Sets Result + stops when the field is found,
		// the tree is exhausted, or the block cap is hit. Call once per tick while IsRunning.
		public static void Tick() {
			if (phase != Phase.Scan) {
				return;
			}
			try {
				var sw = Stopwatch.StartNew();
				while (frontier.Count > 0 && hop <= MaxHops && blocks < MaxBlocks) {
					var next = new List<IntPtr>();
					int idx = 0;
					while (idx < frontier.Count) {
						if (sw.ElapsedMilliseconds >= TickBudgetMs) {
							// Out of time this tick: keep the unprocessed remainder + queued children.
							var remainder = frontier.GetRange(idx, frontier.Count - idx);
							remainder.AddRange(next);
							frontier = remainder;
							return; // resume next tick
						}
						IntPtr block = frontier[idx++];
						if (blocks++ >= MaxBlocks) {
							break;
						}
						uint found = ScanBlock(block, next);
						if (found != 0) {
							Finish(found);
							return;
						}
					}
					frontier = next;
					hop++;
				}
				Finish(0); // exhausted / capped without a hit
			} catch (Exception e) {
				Logger.LogError("MoodMemory.Tick: " + e);
				Finish(0);
			}
		}

		// Snapshot one block, scan it for the mood fingerprint, and queue its child pointers.
		// Returns the live mood hash if the fingerprint is present, else 0.
		static uint ScanBlock(IntPtr block, List<IntPtr> next) {
			byte[] buf = MemScan.Snapshot(block, MaxStructOff);
			// The field is at some offset `off` where off-0x10 (the marker) is also in range, so
			// start scanning at 0x10 in.
			for (int off = 0x10; off + 4 <= buf.Length; off += 4) {
				if (BitConverter.ToUInt64(buf, off + MarkerOff) != ClipBufferMarker) {
					continue;
				}
				uint live = BitConverter.ToUInt32(buf, off);
				if (FacialMoods.ResolveHash(live) != null) {
					cachedField = (IntPtr)(block.ToInt64() + off); // remember for cheap reuse
					return live; // marker matched and the field holds a known mood
				}
			}
			for (int off = 0; off + 8 <= buf.Length; off += 8) {
				long raw = BitConverter.ToInt64(buf, off);
				// Cheap pre-filter BEFORE the VirtualQuery in LooksLikeHeapPtr: a real heap pointer
				// is in the user range and 8-aligned. This skips the syscall on the vast majority of
				// qwords (floats, small ints, flags). Dedup before the syscall too.
				if ((raw & 7) != 0 || raw <= 0x10000 || raw >= 0x7FFFFFFFFFFF) {
					continue;
				}
				if (!seen.Add(raw)) {
					continue;
				}
				IntPtr p = (IntPtr)raw;
				if (MemScan.LooksLikeHeapPtr(p)) {
					next.Add(p);
				}
			}
			return 0;
		}

		static void Finish(uint hash) {
			// 0 = not located; mood_normal_1 = the model default — both mean "no override", so leave
			// Result null and let apply use the default rather than persisting the baseline mood.
			Result = (hash == 0 || hash == NormalMoodHash) ? null : FacialMoods.ResolveHash(hash);
			frontier = null;
			seen = null;
			ped = null;
			phase = Phase.Done;
		}
	}
}
