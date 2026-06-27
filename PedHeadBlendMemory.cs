using System;
using System.Collections.Generic;
using GTA;
using GTA.Native;

namespace FreemodeIdentity {
	// Reads CPedHeadBlendData out of the live ped's memory to recover head data the game
	// exposes no getter native for: head-overlay opacity, the 20 face micro-morphs, the
	// overlay tint colour ids and the hair tint ids. The native capture path (PedAppearance)
	// handles everything the game DOES expose; this fills the rest.
	//
	// HOW the struct is found (GTA V ENHANCED, build 1013.34):
	// The Enhanced exe is packed/encrypted on disk, so the FiveM/Legacy byte patterns
	// (fwExtensionList::Get + the extension-id global) cannot be derived statically and do
	// not exist in the decrypted runtime layout either — Game.FindPattern fails for them.
	// Instead we locate the struct BY CONTENT: GET_PED_HEAD_BLEND_DATA already returns the
	// ped's three heritage MIX floats (shape/skin/third), whose exact bit patterns are a
	// rare, layout-agnostic fingerprint. We walk the ped's pointer graph (the struct hangs
	// off ped+16's extension list) and find the three mix floats stored consecutively;
	// that location IS inside the live CPedHeadBlendData. All other fields are then read at
	// offsets relative to that mix-start, derived empirically from two live samples + the
	// native getters as ground truth (see the project's struct-layout note).
	//
	// SAFETY: every memory access goes through MemScan, which VirtualQuery-gates each read
	// (an unmapped pointer would otherwise raise an uncatchable access violation that kills
	// the game). If the struct can't be found, or a read looks out of range, the whole path
	// disables itself / returns false and callers keep their native-captured defaults.
	static class PedHeadBlendMemory {
		// Field offsets RELATIVE TO THE MIX-START (the ShapeMix float = the first of the
		// three consecutive mix floats we locate). Enhanced 1013.34. All locked against
		// ground truth (native getters + a Menyoo outfit XML carrying the authored tint
		// values, used only to interpret the probe dump). The overlay highlight (secondary
		// tint) array sits directly after the primary array and is read the same way; in
		// every sample it was 0, the universal default, so a wrong stride there can only
		// drop a highlight, never miscolour a face.
		const int OffShapeMix = 0;          // f32 (the located anchor)
		const int OffOverlayAlpha = 0x10;   // f32[13] overlay opacities
		const int OffFaceFeature = 0x78;    // f32[20] micro-morphs
		const int OffOverlayColorId = 0xC8; // u8[13] overlay tint primary palette id
		const int OffOverlayHighlightId = 0xD5; // u8[13] overlay tint secondary palette id
		const int OffOverlayValue = 0xF5;   // u8[13] overlay drawable index (255 = none)
		const int OffEyeColour = 0x10E;     // u16 eye colour palette index
		const int OffHairColour = 0x110;    // u8 hair tint primary palette id
		const int OffHairHighlight = 0x111; // u8 hair tint secondary palette id

		// How far past the mix-start the struct extends (hair highlight at 0x111 + margin);
		// the snapshot we read must cover it. A small fixed window — the struct is contiguous.
		const int StructSpan = 0x120;

		// The mix triple must be findable somewhere in a reachable block; this bounds how
		// far into each candidate block we scan for it.
		const int BlockScanBytes = 0x400;

		// Pointer-graph walk bounds for locating the mix-start. Block-count caps (CPU-independent):
		// the struct is reached within a few hops. The block ORDINAL at which it's reached varies with
		// BFS ordering — observed 125-345 on a male but 1434 on a female (same struct address, later
		// in traversal). 1500 nearly clipped that, so the cap is 3000 for safe margin; the cheap
		// overlay pre-filter makes each block fast, so a higher cap costs little when the ped is found
		// early and only matters in the rare deep case.
		const int MaxHops = 4;
		const int MaxFindBlocks = 3000;

		static bool Initialized;
		static bool available;

		// The memory read is "available" once we know it can run at all. Unlike the old
		// pattern path there is nothing to resolve up front — availability is proven per
		// ped when we actually find the struct — so we report true on a platform where the
		// primitives work and let TryFill no-op safely if a given ped can't be resolved.
		public static bool Available {
			get {
				EnsureInit();
				return available;
			}
		}

		static void EnsureInit() {
			if (Initialized) {
				return;
			}
			Initialized = true;
			// No patterns to resolve any more. The content-based locator works as long as
			// we can read process memory, which MemScan always can on a live game. Kept as
			// a flag so callers and the startup log have a single yes/no to report.
			available = true;
			Logger.Log("PedHeadBlendMemory: content-based head-blend read armed (Enhanced layout).");
		}

		// ---- Tick-driven mix-start finder ------------------------------------------------
		// Locating the struct walks the ped's pointer graph scanning for the heritage mix triple.
		// That is exactly the kind of heavy content scan that must NOT run synchronously inside a
		// snapshot: when the ped's task graph is large/churning (e.g. right after a moving style is
		// applied in a trainer) the walk balloons and racing the churn can fault the game with an
		// uncatchable access violation. So the find runs tick-driven and time-sliced, off the
		// snapshot hot path, the same way mood and the tattoo base are found. BeginFind starts it;
		// FillFromMix consumes MixResult once FindRunning is false.

		// Per-tick wall-clock slice for the walk. With a DEFAULT heritage (0,0,0) the triple matches
		// constantly, so each block pays many candidate checks — at 20ms/tick that spread a ~1400-block
		// walk over ~24s of real time. A bigger slice (still far under SHVDN's 5s tick watchdog) clears
		// the same walk in a couple of ticks: a brief one-time blip behind the "Preparing snapshot"
		// ticker instead of a long grind. The snapshot is user-initiated and already shows a wait, so a
		// ~300ms hitch is acceptable; the result is cached for the session.
		const long FindBudgetMs = 300;

		static bool findRunning;
		static int findBlocks; // blocks walked this find — for the FOUND/NOT-found diagnostic log
		static IEnumerator<IntPtr> findWalker;
		static float findShape, findSkin, findThird;
		// The ped's real per-slot overlay drawable indices, read from the native getter
		// (GET_PED_HEAD_OVERLAY, 255 = none). Used as a STRONG, ped-specific fingerprint at find
		// time: the heritage triple alone is 0,0,0 on a default ped and matches everywhere, but the
		// overlay-value array at OffOverlayValue must equal exactly these natively-read values — a
		// 13-byte ped-specific signature that random memory will not reproduce.
		static readonly byte[] findOverlayValues = new byte[PedAppearance.OverlayCount];
		static long findPed;
		static IntPtr mixResult;

		// Session cache: the CPedHeadBlendData doesn't move while the ped/model is unchanged, so a
		// found mix-start is reused on later snapshots after a cheap re-validation (read the three
		// heritage floats at the cached address — still match → skip the whole pointer-graph walk).
		// Keyed by ped memory address; a model swap gives a new address so the cache misses safely.
		static long cachedPed;
		static IntPtr cachedMix;

		// The struct bytes captured AT FIND TIME. Critical: between locating the struct and the
		// deferred DoSnapshot/TryFill, the tattoo DecorationBaseFinder adds+removes a sentinel
		// decoration, which churns the ped and can RELOCATE the head-blend struct — so the found
		// address is stale by the time TryFill reads it (observed: "mix anchor mismatch" 5s later).
		// We snapshot the struct the instant we find it (the user isn't editing mid-save, so the
		// contents are final) and TryFill consumes THIS buffer, immune to the later relocation.
		static byte[] foundStruct;

		public static bool FindRunning => findRunning;
		public static IntPtr MixResult => mixResult;
		public static byte[] StructSnapshot => foundStruct;

		// How long to wait for a freshly-switched ped's head blend to become readable before giving up.
		// GET_PED_HEAD_BLEND_DATA returns garbage right after a model switch/spawn and usually settles
		// within a second, but a snapshot taken immediately after switching can need longer — 6s covers
		// the slow case so the face still gets captured. WALL-CLOCK (Game.GameTime ms), NOT a tick
		// count: a tick budget is frame-rate dependent (observed: 360 ticks is ~6s at 60fps but only
		// ~2.2s at 160fps, so high-FPS machines gave up early and lost the head blend — the lost
		// hair-tint/eye/morph-on-custom-ped bug). Still bounded so a genuinely blend-less ped (e.g. a
		// non-freemode model) can't hold the snapshot forever.
		const int SettleBudgetMs = 6000;

		static Ped findTargetPed;
		static int settleStartMs; // Game.GameTime at which the settle wait began; -1 = not started
		static string lastRejectedMix; // last mix triple IsValidMix rejected, for the give-up diagnostic

		// Start a tick-driven search for the ped's CPedHeadBlendData mix-start. The heritage triple
		// (the native already round-trips it) is the locator fingerprint. If a previous snapshot
		// already located it for this ped and the cached address still reads the same triple, reuse
		// it instantly (no walk, no per-frame slicing) — that's what makes consequent saves smooth.
		//
		// The heritage may not be readable yet if the ped was just switched/spawned (the blend data
		// settles a beat later), so this enters a tick-driven SETTLE wait first; the actual walk is
		// armed by ArmWalk() once the heritage reads valid. FindRunning stays true throughout, so the
		// deferred snapshot correctly waits for the whole thing.
		public static void BeginFind(Ped ped) {
			mixResult = IntPtr.Zero;
			foundStruct = null;
			findWalker = null;
			findRunning = false;
			findTargetPed = null;
			settleStartMs = -1;
			if (!Available || ped == null || !ped.Exists() || ped.MemoryAddress == IntPtr.Zero) {
				return;
			}
			findTargetPed = ped;
			findRunning = true; // hold the deferred snapshot until settle+walk resolve (or time out)
			if (!ArmWalk()) {
				// Heritage not readable yet — wait for the blend to settle (driven by TickFind).
				Logger.Log("PedHeadBlendMemory: head blend not ready — waiting for it to settle.");
			}
		}

		// Read the heritage + overlay fingerprint and, if the heritage is valid, either consume the
		// session cache or arm the pointer-graph walk. Returns true when the find is resolved or armed
		// (caller stops waiting), false when the heritage isn't readable yet (keep settling).
		static bool ArmWalk() {
			Ped ped = findTargetPed;
			if (ped == null || !ped.Exists() || ped.MemoryAddress == IntPtr.Zero) {
				findRunning = false;
				return true;
			}
			OutputArgument arg = OutputArgument.AllocForType<HeadBlendData>();
			Function.Call(Hash.GET_PED_HEAD_BLEND_DATA, ped, arg);
			HeadBlendData d = arg.GetResultAsBlittableStruct<HeadBlendData>();
			findShape = d.ShapeMix;
			findSkin = d.SkinMix;
			findThird = d.ThirdMix;

			// The heritage mix floats are blend WEIGHTS — always in [0,1] for a valid freemode head.
			// Right after a model switch/spawn GET_PED_HEAD_BLEND_DATA hands back garbage (observed
			// e.g. 4.6E+24, 8.4E-45) until the blend settles. Not-ready → caller keeps settling.
			if (!IsValidMix(findShape) || !IsValidMix(findSkin) || !IsValidMix(findThird)) {
				// Remember the last rejected triple so the give-up log can show WHY (distinguishes
				// real-but-out-of-range, e.g. a Menyoo overshoot, from just-switched garbage).
				lastRejectedMix = $"shape={findShape:G9} skin={findSkin:G9} third={findThird:G9}";
				return false;
			}

			// Read the real overlay drawable indices now (native getter) to fingerprint the struct
			// location precisely. GET_PED_HEAD_OVERLAY returns the slot's drawable index, 255 = none.
			for (int slot = 0; slot < PedAppearance.OverlayCount; slot++) {
				int idx = Function.Call<int>(Hash.GET_PED_HEAD_OVERLAY, ped, slot);
				findOverlayValues[slot] = (byte)(idx < 0 || idx > 255 ? 255 : idx);
			}

			findPed = ped.MemoryAddress.ToInt64();
			if (cachedPed == findPed && cachedMix != IntPtr.Zero && MixMatches(cachedMix)) {
				mixResult = cachedMix; // cache hit — reuse, no walk
				// Snapshot now, before any later ped churn (e.g. the tattoo sweep) can relocate it.
				foundStruct = MemScan.Snapshot(cachedMix, StructSpan);
				findRunning = false;
				Logger.Log($"PedHeadBlendMemory: mix cache HIT (mix={cachedMix.ToInt64():X}).");
				return true;
			}
			Logger.Log($"PedHeadBlendMemory: mix cache MISS (heritage={findShape},{findSkin},{findThird}) — walking graph.");
			findBlocks = 0;
			// Tight block cap: the CPedHeadBlendData mix-start is reached EARLY in the walk (found
			// at 128-330 blocks across runs), so 1500 has comfortable margin while bailing the
			// not-found case (e.g. a non-freemode ped slipping through) fast instead of grinding the
			// default 4000. Block-count, so it behaves identically on slow CPUs.
			findWalker = MemScan.WalkPointerGraph(ped.MemoryAddress, MaxHops, MaxFindBlocks).GetEnumerator();
			return true;
		}

		// Cheap revalidation: does the cached mix address still look like a real CPedHeadBlendData?
		// The heritage triple alone is NOT enough — when a ped has default heritage the triple is
		// 0,0,0, which matches countless unrelated zero runs in memory. So require BOTH the triple
		// AND the morph-range fingerprint (LooksLikeStruct) here, same as at find time.
		static bool MixMatches(IntPtr mix) {
			return LooksLikeStruct(mix);
		}

		// Is `mix` the real CPedHeadBlendData anchor, or just a coincidental run of floats that
		// happen to equal the heritage triple? The heritage mix is the locator, but a DEFAULT
		// freemode ped has mix = 0,0,0, and three consecutive zero-floats occur all over process
		// memory — so the bare triple is a false-positive magnet. Two earlier weak filters (morphs
		// in [-1.5,1.5]; any empty overlay slot) both still admitted garbage: a block of denormal
		// floats (4.4E-42, etc.) is numerically in range yet is not a real morph array.
		//
		// The reliable discriminator is a PED-SPECIFIC content match: the overlay drawable-value
		// array at OffOverlayValue must equal, byte-for-byte, the 13 values the native getter
		// returned for THIS ped (findOverlayValues). That is a precise fingerprint a random region
		// won't reproduce. We additionally reject morph arrays that are denormal/garbage so a near-
		// zero region can't sneak through. The mix triple still gates first (cheap reject).
		static bool LooksLikeStruct(IntPtr mix) {
			byte[] s = MemScan.Snapshot(mix, StructSpan);
			if (s.Length < StructSpan) {
				return false;
			}
			if (!FloatEq(BitConverter.ToSingle(s, OffShapeMix), findShape) ||
				!FloatEq(BitConverter.ToSingle(s, OffShapeMix + 4), findSkin) ||
				!FloatEq(BitConverter.ToSingle(s, OffShapeMix + 8), findThird)) {
				return false;
			}
			// Strong, ped-specific signature: the live overlay drawable indices must match exactly.
			for (int i = 0; i < PedAppearance.OverlayCount; i++) {
				if (s[OffOverlayValue + i] != findOverlayValues[i]) {
					return false;
				}
			}
			// And the morph array must be PLAUSIBLE morph data, not denormal noise that merely falls
			// in range. A real morph is 0 or a normal float in [-1.5,1.5]; reject NaN, out-of-range,
			// and sub-normal tiny magnitudes (|v| < 1e-6 but nonzero) that signal reinterpreted bytes.
			for (int i = 0; i < PedAppearance.FaceFeatureCount; i++) {
				float v = BitConverter.ToSingle(s, OffFaceFeature + i * 4);
				if (float.IsNaN(v) || v < -1.5f || v > 1.5f) {
					return false;
				}
				if (v != 0f && Math.Abs(v) < 1e-6f) {
					return false; // denormal / garbage byte pattern, not an authored morph
				}
			}
			return true;
		}

		// Advance the search by one time-bounded slice. Sets MixResult + stops when found or the
		// pointer graph is exhausted. Call once per tick while FindRunning.
		public static void TickFind() {
			if (!findRunning) {
				return;
			}
			// SETTLE phase: the walk isn't armed yet because the heritage wasn't readable (ped just
			// switched/spawned). Retry reading it each tick until it's valid, then ArmWalk() either
			// resolves from cache or sets findWalker for the walk below. Bounded so a blend-less ped
			// gives up instead of holding the snapshot forever.
			if (findWalker == null && mixResult == IntPtr.Zero) {
				if (ArmWalk()) {
					// Resolved (cache hit) or armed the walk. If it cleared findRunning (cache hit /
					// dead ped), we're done; otherwise fall through next tick into the walk.
					return;
				}
				// Wall-clock settle budget (frame-rate independent — see SettleBudgetMs). Stamp the
				// start on the first not-ready tick.
				if (settleStartMs < 0) {
					settleStartMs = Game.GameTime;
				}
				if (Game.GameTime - settleStartMs >= SettleBudgetMs) {
					Logger.LogError($"PedHeadBlendMemory: head blend never became readable (last rejected mix: {lastRejectedMix}); skipping memory read (keeping defaults).");
					findRunning = false;
				}
				return;
			}
			try {
				var sw = System.Diagnostics.Stopwatch.StartNew();
				while (sw.ElapsedMilliseconds < FindBudgetMs && findWalker.MoveNext()) {
					findBlocks++;
					byte[] buf = MemScan.Snapshot(findWalker.Current, BlockScanBytes);
					for (int off = 0; off + 12 <= buf.Length; off += 4) {
						if (FloatEq(BitConverter.ToSingle(buf, off), findShape) &&
							FloatEq(BitConverter.ToSingle(buf, off + 4), findSkin) &&
							FloatEq(BitConverter.ToSingle(buf, off + 8), findThird)) {
							// Triple matched — but with default heritage (0,0,0) that's a false-positive
							// magnet hitting constantly. Before paying for the full LooksLikeStruct (a
							// fresh 0x120-byte VirtualQuery-gated snapshot + 13 overlay + 20 morph checks),
							// do a CHEAP reject straight from the block buffer we already have: the overlay
							// drawable-value bytes at OffOverlayValue must match the ped fingerprint. This
							// kills the vast majority of zero-run false matches without a syscall, which is
							// what made the default-heritage walk grind. (Only when the bytes lie within the
							// scanned buffer; otherwise fall through to the authoritative check.)
							int ovEnd = off + OffOverlayValue + PedAppearance.OverlayCount;
							if (ovEnd <= buf.Length && !OverlayBytesMatch(buf, off + OffOverlayValue)) {
								continue;
							}
							IntPtr cand = findWalker.Current + off;
							if (!LooksLikeStruct(cand)) {
								continue;
							}
							mixResult = cand;
							cachedPed = findPed;   // cache for cheap reuse on later snapshots
							cachedMix = mixResult;
							// Snapshot the struct NOW. The deferred decoration sweep that runs after this
							// find churns the ped and can relocate the struct, so reading it later (in
							// TryFill) would hit a stale address. Capture the final bytes here instead.
							foundStruct = MemScan.Snapshot(cand, StructSpan);
							findRunning = false;
							findWalker = null;
							Logger.Log($"PedHeadBlendMemory: mix FOUND after {findBlocks} blocks (mix={cand.ToInt64():X}).");
							return;
						}
					}
					if (sw.ElapsedMilliseconds >= FindBudgetMs) {
						return; // resume next tick from the same enumerator
					}
				}
				// MoveNext returned false within budget: graph exhausted, not found.
				if (sw.ElapsedMilliseconds < FindBudgetMs) {
					findRunning = false;
					findWalker = null;
					Logger.LogError($"PedHeadBlendMemory: mix NOT found after {findBlocks} blocks (graph exhausted).");
				}
			} catch (Exception e) {
				Logger.LogError("PedHeadBlendMemory.TickFind: " + e);
				findRunning = false;
				findWalker = null;
			}
		}

		// Fills the fields no native exposes — overlay opacity, the 20 micro-morphs — and
		// refines overlay drawable indices and eye colour from the live struct. Returns
		// false (touching nothing) if the struct can't be located so the caller keeps its
		// native-captured / default values. Anything unexpected is treated as unavailable —
		// a bad read must never wreck a face.
		public static bool TryFill(Ped ped, AppearanceData ad) {
			if (!Available || ped == null || !ped.Exists()) {
				return false;
			}

			// Consume the struct bytes snapshotted AT FIND TIME (TickFind/BeginFind), not the live
			// address: the deferred tattoo sweep that runs between find and here churns the ped and
			// can relocate the struct, so re-reading MixResult now would hit stale memory (that was
			// the "mix anchor mismatch" 5s later). The find-time bytes are the ped's final state for
			// this save — the user isn't editing mid-snapshot.
			byte[] s = StructSnapshot;
			if (s == null || s.Length < StructSpan) {
				Logger.LogError("PedHeadBlendMemory: could not locate CPedHeadBlendData for this ped; keeping defaults.");
				return false;
			}

			// Micro-morphs: 20 floats. The find-time fingerprint already validated these, so this is
			// a belt-and-suspenders sanity gate — a failure here means the layout shifted under a
			// game patch; abort rather than write garbage.
			for (int i = 0; i < PedAppearance.FaceFeatureCount; i++) {
				float v = BitConverter.ToSingle(s, OffFaceFeature + i * 4);
				if (v < -1.5f || v > 1.5f || float.IsNaN(v)) {
					Logger.LogError($"PedHeadBlendMemory: faceFeature[{i}]={v} out of range; aborting memory fill.");
					return false;
				}
				ad.FaceFeatures[i] = v;
			}

			// Eye colour as the live palette index (the native getter agrees with this, but
			// reading it here keeps everything from one consistent snapshot).
			ad.EyeColor = BitConverter.ToUInt16(s, OffEyeColour);

			// Hair tint palette ids — no native getter exposes these (only RGB, which the
			// setter can't take), so memory is the only source. They sit immediately after
			// eye colour.
			ad.HairColor = s[OffHairColour];
			ad.HairHighlightColor = s[OffHairHighlight];

			// Overlay opacity + tint: enrich the overlays the native pass already found. The
			// tint colorId/highlightId arrays are per-slot bytes; reading them lets apply
			// restore eyebrow/eye-shadow/blush/lipstick colours instead of skipping them.
			foreach (HeadOverlayData o in ad.Overlays) {
				if (o.Slot < 0 || o.Slot >= PedAppearance.OverlayCount) {
					continue;
				}
				float opacity = BitConverter.ToSingle(s, OffOverlayAlpha + o.Slot * 4);
				if (opacity >= 0f && opacity <= 1f) {
					o.Opacity = opacity;
				}
				// Refine the drawable index from the struct (255 = none) as a cross-check;
				// the native pass already set it, so only overwrite a sane value.
				byte value = s[OffOverlayValue + o.Slot];
				if (value != 255) {
					o.Index = value;
				}
				o.FirstColor = s[OffOverlayColorId + o.Slot];
				o.SecondColor = s[OffOverlayHighlightId + o.Slot];
			}

			// The overlay tint colours and hair tint were genuinely read this pass, so apply
			// may now tint the tintable slots (eyebrows/makeup/blush/lipstick) instead of
			// skipping them. See AppearanceData.OverlayTintFromMemory.
			ad.OverlayTintFromMemory = true;
			return true;
		}

		static bool FloatEq(float a, float b) {
			return Math.Abs(a - b) < 0.0005f;
		}

		// Cheap pre-filter: do the 13 overlay drawable-value bytes at `start` in the already-read
		// block buffer match this ped's native overlay fingerprint? Used to reject the flood of
		// coincidental heritage-triple matches before the expensive full struct snapshot.
		static bool OverlayBytesMatch(byte[] buf, int start) {
			for (int i = 0; i < PedAppearance.OverlayCount; i++) {
				if (buf[start + i] != findOverlayValues[i]) {
					return false;
				}
			}
			return true;
		}

		// A heritage mix weight is sane only in [0,1]. A tiny epsilon of slack absorbs float noise.
		// Rejects NaN, wild magnitudes, AND nonzero denormals (|v| < 1e-6): a settled default heritage
		// reads a clean 0.0, so a tiny-but-nonzero value like 8.4E-45 is a sign the blend hasn't
		// settled yet (just-switched ped) — treat it as not-ready so the finder waits rather than
		// searching for a near-zero fingerprint that won't match the real (settled) struct.
		static bool IsValidMix(float v) {
			// A heritage mix weight is nominally [0,1], but Menyoo-loaded peds write values a hair
			// outside it (observed skin mix = 1.0087 on a custom female — real, stable data, NOT the
			// just-switched garbage). Tolerate ±0.1 so those round-trip; true garbage (4.6E+24, NaN,
			// denormals) is orders of magnitude out and still rejected.
			if (float.IsNaN(v) || v < -0.1f || v > 1.1f) {
				return false;
			}
			return v == 0f || Math.Abs(v) >= 1e-6f;
		}
	}
}
