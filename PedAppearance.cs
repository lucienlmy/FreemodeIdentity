using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using GTA;
using GTA.Native;

namespace FreemodeIdentity {
	// GET_PED_HEAD_BLEND_DATA writes through an out-pointer. Fixed game layout: ints 8-aligned,
	// isParent at 75, total 80 bytes (verified against SHVDN's HeadBlendData).
	[StructLayout(LayoutKind.Explicit, Size = 80)]
	struct HeadBlendData {
		[FieldOffset(0)] public int ShapeFirst;
		[FieldOffset(8)] public int ShapeSecond;
		[FieldOffset(16)] public int ShapeThird;
		[FieldOffset(24)] public int SkinFirst;
		[FieldOffset(32)] public int SkinSecond;
		[FieldOffset(40)] public int SkinThird;
		[FieldOffset(48)] public float ShapeMix;
		[FieldOffset(56)] public float SkinMix;
		[FieldOffset(64)] public float ThirdMix;
		[FieldOffset(75)] public bool IsParent;
	}

	// The capture/apply layer for the freemode (MP) player appearance.
	//
	// SHVDN3 wraps clothing but NOT the head-creation natives (heritage blend, head overlays,
	// blend eye/hair colour), so those go through Function.Call — and since those getters return
	// through an out-pointer SHVDN3 can marshal, we can capture them, not just apply them.
	//
	// A few fields have no readback native at all and are recovered from live memory (a native
	// snapshot would default them): the 20 face micro-morphs (SET_PED_MICRO_MORPH has no getter),
	// overlay/hair tint, and the movement clipset. Apply order is load-bearing: model first, then
	// head-blend, then features/overlays/components.
	public static class PedAppearance {
		// The optional capture fields, gated because they're the costly reads: tattoos (a memory
		// sweep), mood and moving style (deferred/memory reads). Everything else is always captured.
		// A field left out is stored empty, and apply treats empty as "leave the model default"
		// (tattoos: leave the ped's as-is) — so a toggled-off feature never overwrites with blanks.
		public struct CaptureOptions {
			public bool Tattoos;
			public bool Mood;
			public bool MovingStyle;
			public static readonly CaptureOptions All = new CaptureOptions { Tattoos = true, Mood = true, MovingStyle = true };
		}

		// The two freemode models. Heritage/morph/overlay natives only behave on
		// these; a story-mode ped silently ignores most of them.
		public const string MaleModel = "mp_m_freemode_01";
		public const string FemaleModel = "mp_f_freemode_01";

		public const int FaceFeatureCount = 20;

		// ---- Model -------------------------------------------------------------

		// force: skip the "already this model" short-circuit and ALWAYS do a real
		// SET_PLAYER_MODEL. The disable/return-to-protagonist path needs this because
		// Model.Hash reads the archetype hash — the very field a spoof (or a hash stranded by
		// a reload) overwrites. Without force, returning to e.g. player_one while a Franklin
		// hash is painted on a freemode ped would see "already player_one" and no-op, leaving
		// you really freemode (Menyoo's player checkmark exposes the mismatch) — a fake return.
		//
		// realModelHash: the ped's TRUE model when a spoof has painted a protagonist hash over the
		// archetype that Model.Hash reads (0 = read it off the ped). The short-circuit must compare
		// against the real model, not the disguise: under a Franklin spoof a re-apply of the freemode
		// look would otherwise see "Model.Hash != mp_f_freemode_01" and recreate the ped every time —
		// and a SET_PLAYER_MODEL recreate also wipes the just-painted appearance, so the next tick
		// re-applies and recreates again, looping (and leaving a default freemode ped on screen).
		// resetComponents: apply default clothing to the recreated ped. True for a freemode apply (the
		// look paints over a clean base afterward). False when returning to a story protagonist, whose own
		// outfit StoryLook restores right after — skipping the default set avoids a default-clothes flash
		// before that lands. (The swap itself brings back nothing but the baked face, hence StoryLook.)
		public static bool SwitchModel(string model, bool force = false, int realModelHash = 0, bool resetComponents = true) {
			var hash = new Model(model);
			if (!hash.IsValid || !hash.IsPed) {
				return false;
			}
			// Skip the swap entirely if the player is ALREADY this model. SET_PLAYER_MODEL destroys
			// and recreates the ped, which leaves the decoration system unable to accept tattoos
			// (observed: capture works on a fresh MP ped but not one we re-applied — ADD no-ops and
			// GET_PED_DECORATIONS_STATE reads stale-clean on the recreated ped). Re-applying the same
			// freemode model over itself is the common case (load saved female onto a freemode ped),
			// so avoiding the needless recreate keeps decorations working. The head-blend/component
			// edits that follow land fine on the existing ped. Skipped when force (see above).
			int liveHash = realModelHash != 0 ? realModelHash
				: (Game.Player.Character != null ? Game.Player.Character.Model.Hash : 0);
			if (!force && liveHash == hash.Hash) {
				return true;
			}
			// Request the model and block until it streams in (5s budget) so the
			// SET_PLAYER_MODEL swap and the component sets after it resolve against
			// real freemode drawables rather than a not-yet-loaded placeholder.
			if (!hash.Request(5000)) {
				return false;
			}
			// SET_PLAYER_MODEL recreates the player ped, which drops the wanted level (the cops lose their
			// target and disengage). Capture it now and re-assert it on the new ped so a mod enable/disable
			// mid-chase doesn't hand the player a free escape.
			int wanted = Function.Call<int>(Hash.GET_PLAYER_WANTED_LEVEL, Game.Player);
			Function.Call(Hash.SET_PLAYER_MODEL, Game.Player, hash.Hash);
			// SET_PLAYER_MODEL destroys the old player ped and creates a new one, so the previous
			// handle is now invalid. A single frame yield can return before the new ped exists or
			// reports the new model, leaving the edits below to land on a stale/half-built ped. Wait
			// until it actually exists AND reports the freemode model, plus a brief settle, first.
			Ped ped = null;
			for (int i = 0; i < 60; i++) { // ~1s budget at 60fps
				Script.Wait(0);
				ped = Game.Player.Character;
				if (ped != null && ped.Exists() && ped.Model.Hash == hash.Hash) {
					Script.Wait(100); // brief settle so the mesh finishes building before edits
					break;
				}
			}
			if (ped == null || !ped.Exists()) {
				hash.MarkAsNoLongerNeeded();
				return false;
			}
			if (resetComponents) {
				Function.Call(Hash.SET_PED_DEFAULT_COMPONENT_VARIATION, ped);
			}
			// Re-assert the pre-swap wanted level onto the recreated ped. _NOW makes the cops re-engage
			// immediately instead of waiting for the next search cycle. Skip when there was nothing to keep.
			if (wanted > 0) {
				Function.Call(Hash.SET_PLAYER_WANTED_LEVEL, Game.Player, wanted, false);
				Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, Game.Player, false);
			}
			hash.MarkAsNoLongerNeeded();
			return true;
		}

		// Hash-based check, so callers can test the ped's REAL model when a spoof has overwritten
		// the archetype hash that ped.Model.Hash reads (the disguise makes a freemode ped report a
		// protagonist hash). The ped's actual body/head-blend memory is still freemode underneath.
		public static bool IsFreemodeHash(int hash) =>
			hash == new Model(MaleModel).Hash || hash == new Model(FemaleModel).Hash;

		// True when the ped's BODY is a freemode ped, independent of the archetype hash a spoof may
		// have painted over it. Guards every memory write that could poison the shared model-info
		// (stranded restore / spoof self-heal), so a false positive on a genuine protagonist is the
		// "broken Franklin" bug: writing freemode onto a protagonist's model-info corrupts it for the
		// whole session. CONFIRMED in-game: a story protagonist returns a CLEAN all-zero blend, which is
		// in [0,1] — so an in-range test ALONE passes it (that was the bug). A real freemode ped always
		// carries real heritage (non-zero parents and/or a non-zero mix), so we require that. A warm
		// load's transient garbage (NaN, denormals, junk ints) fails the range/sanity checks. Returns
		// false on no ped / unreadable, so the safe default is "don't touch".
		public static bool HasFreemodeBody(Ped ped) {
			if (ped == null || !ped.Exists()) return false;
			try {
				OutputArgument arg = OutputArgument.AllocForType<HeadBlendData>();
				Function.Call(Hash.GET_PED_HEAD_BLEND_DATA, ped, arg);
				HeadBlendData d = arg.GetResultAsBlittableStruct<HeadBlendData>();
				if (!InMixRange(d.ShapeMix) || !InMixRange(d.SkinMix) || !InMixRange(d.ThirdMix)) return false;
				// Real freemode parents are small indices (< 64); a junk read or a protagonist's zeros
				// don't qualify. A non-zero mix is the other tell. Either proves a built freemode head.
				bool saneParent(int v) => v > 0 && v < 64;
				return d.ShapeMix != 0f || d.SkinMix != 0f
					|| saneParent(d.ShapeFirst) || saneParent(d.ShapeSecond)
					|| saneParent(d.SkinFirst) || saneParent(d.SkinSecond);
			} catch (Exception e) {
				Logger.LogError("HasFreemodeBody: " + e);
				return false;
			}
		}

		// ---- Heritage (head blend) --------------------------------------------

		public static void ApplyHeritage(Ped ped, AppearanceData ad) {
			// isParent = false: these are the head shape/skin parents, not a parent-of-a-child blend.
			Function.Call(Hash.SET_PED_HEAD_BLEND_DATA, ped,
				ad.ShapeFirst, ad.ShapeSecond, ad.ShapeThird,
				ad.SkinFirst, ad.SkinSecond, ad.SkinThird,
				ad.ShapeMix, ad.SkinMix, ad.ThirdMix, false);
		}

		// True when a freemode capture came back with no usable face data — an externally-authored
		// face (Menyoo's randomizer, some trainer peds) that lives outside the head-blend system, so
		// the values the natives returned describe no real face. Two independent tells, EITHER of
		// which condemns the face (both seen in the wild):
		//   - garbage heritage mix: a weight outside [0,1], OR a denormal "dirty zero" like 1.4E-45
		//     that a real blend never produces (a settled default reads a clean 0.0).
		//   - the morph memory read failed AND every morph is 0: a real head-blend face round-trips
		//     its 20 micro-morphs from memory, so headBlendMem=false with all-zero morphs means the
		//     face wasn't in the readable system (the randomizer case whose mix lands numerically
		//     in-range and so slips past the mix check alone).
		// Used to warn at save time and to skip applying the face. An in-creator / heritage-slider
		// face has a clean in-range mix, distinctive parents, or non-zero morphs, so this is false.
		public static bool HasNoUsableFace(AppearanceData ad) {
			bool mixGarbage = !InMixRange(ad.ShapeMix) || !InMixRange(ad.SkinMix) || !InMixRange(ad.ThirdMix);
			bool allMorphsZero = true;
			foreach (float v in ad.FaceFeatures) {
				if (v != 0f) { allMorphsZero = false; break; }
			}
			// A distinctive heritage (non-default parents or a real blend weight) IS a real, restorable
			// face on its own — keep it even if the morph memory read failed, rather than dropping a
			// genuine face to a transient walk hiccup. Only condemn when there's truly nothing to save:
			// garbage/absent heritage AND no morphs. Default freemode parents are 0/0 shape, 1/1 skin.
			bool distinctiveHeritage = ad.ShapeFirst != 0 || ad.ShapeSecond != 0
				|| ad.SkinFirst != 1 || ad.SkinSecond != 1
				|| ad.ShapeMix != 0f || ad.SkinMix != 0f || ad.ThirdMix != 0f;
			if (distinctiveHeritage && !mixGarbage) {
				return false;
			}
			return (mixGarbage || !ad.HeadDataFromMemory) && allMorphsZero;
		}

		// A real heritage mix weight is a clean 0.0 or a NORMAL float in [0,1]. Rejects NaN,
		// out-of-range, and IEEE subnormals (like 1.4E-45): those are the just-switched settling
		// artefact, not a value a genuine blend writes. The line is normal-vs-subnormal, NOT a
		// magnitude floor — a real near-zero mix (7.45E-08, seen on a Menyoo-edited default-heritage
		// ped) is a small normal float and must pass, or an otherwise-fine face is condemned. Kept in
		// lockstep with PedHeadBlendMemory.IsValidMix, which gates the same distinction earlier.
		static bool InMixRange(float v) {
			if (float.IsNaN(v) || v < 0f || v > 1f) {
				return false;
			}
			return v == 0f || Math.Abs(v) >= 1.17549435E-38f;
		}

		public static void CaptureHeritage(Ped ped, AppearanceData ad) {
			OutputArgument arg = OutputArgument.AllocForType<HeadBlendData>();
			Function.Call(Hash.GET_PED_HEAD_BLEND_DATA, ped, arg);
			HeadBlendData d = arg.GetResultAsBlittableStruct<HeadBlendData>();
			ad.ShapeFirst = d.ShapeFirst;
			ad.ShapeSecond = d.ShapeSecond;
			ad.ShapeThird = d.ShapeThird;
			ad.SkinFirst = d.SkinFirst;
			ad.SkinSecond = d.SkinSecond;
			ad.SkinThird = d.SkinThird;
			ad.ShapeMix = d.ShapeMix;
			ad.SkinMix = d.SkinMix;
			ad.ThirdMix = d.ThirdMix;
		}

		// ---- Face features (micro-morphs) -------------------------------------

		public static void ApplyFaceFeatures(Ped ped, AppearanceData ad) {
			for (int i = 0; i < FaceFeatureCount; i++) {
				float v = i < ad.FaceFeatures.Count ? ad.FaceFeatures[i] : 0f;
				Function.Call(Hash.SET_PED_MICRO_MORPH, ped, i, v);
			}
		}

		public static void EnsureFaceFeatureSlots(AppearanceData ad) {
			while (ad.FaceFeatures.Count < FaceFeatureCount) {
				ad.FaceFeatures.Add(0f);
			}
		}

		// ---- Head overlays ----------------------------------------------------

		// The 13 head-overlay slots (blemishes, facial hair, eyebrows, ageing,
		// makeup, blush, complexion, damage, beard/moles, body blemishes, ...).
		public const int OverlayCount = 13;

		// The tint colour-type a slot requires (per SET_PED_HEAD_OVERLAY_COLOR docs):
		// 1 for eyebrows/facial-hair/makeup/chest-hair, 2 for blush/lipstick, 0 (no
		// tint) for the rest. A tintable overlay applied WITHOUT its tint renders at
		// palette index 0 — which for the makeup palette is a vivid green — so apply
		// must always tint the tintable slots. Indexed by overlay slot 0..12.
		static readonly int[] OverlayColorType = {
			0, // 0 blemishes
			1, // 1 facial hair
			1, // 2 eyebrows
			0, // 3 ageing
			1, // 4 makeup
			2, // 5 blush
			0, // 6 complexion
			0, // 7 sun damage
			2, // 8 lipstick
			0, // 9 moles/freckles
			1, // 10 chest hair
			0, // 11 body blemishes
			0, // 12 add body blemishes
		};

		public static void ApplyOverlays(Ped ped, AppearanceData ad) {
			// Clear ALL overlay slots to "none" first, so an overlay the PREVIOUS apply set (e.g. pink
			// lipstick on a prior look) doesn't carry over onto a look that doesn't define that slot.
			// Capture/ApplyOverlays only list overlays that are present (idx != 255), so without this
			// reset the unlisted slots keep whatever the last-applied ped left there. Index 255 = none.
			for (int slot = 0; slot < OverlayCount; slot++) {
				Function.Call(Hash.SET_PED_HEAD_OVERLAY, ped, slot, 255, 0f);
			}
			foreach (HeadOverlayData o in ad.Overlays) {
				int colorType = o.Slot >= 0 && o.Slot < OverlayColorType.Length ? OverlayColorType[o.Slot] : 0;

				// Without real captured colours, applying a tintable overlay forces it
				// to palette index 0 — a vivid green/red mask. So when the tint colours
				// were NOT captured, skip the tinted slots entirely (better a missing
				// brow tint than a red face) and only apply untinted overlays. Gated on
				// OverlayTintFromMemory specifically: opacity/morphs may have come from
				// memory while the tint ids did not.
				if (!ad.OverlayTintFromMemory && colorType > 0) {
					continue;
				}

				Function.Call(Hash.SET_PED_HEAD_OVERLAY, ped, o.Slot, o.Index, o.Opacity);
				if (colorType > 0) {
					// Real captured colours from memory; colorType is the slot's fixed
					// palette type, the colour ids are what we read off the ped.
					Function.Call(Hash.SET_PED_HEAD_OVERLAY_TINT, ped, o.Slot, colorType, o.FirstColor, o.SecondColor);
				}
			}
		}

		public static void CaptureOverlays(Ped ped, AppearanceData ad) {
			// The only overlay GETTER the game exposes is _GET_PED_HEAD_OVERLAY_VALUE
			// (SHVDN's Hash.GET_PED_HEAD_OVERLAY, 0xA60EF3B6461A4D43): it RETURNS the
			// drawable index and takes NO out-pointers — there is no native to read an
			// overlay's tint colours or opacity back. So a native capture gets the
			// overlay shape (eyebrows, ageing, complexion, beard, ...) but its tint
			// and opacity default. (The colours live only in CPedHeadBlendData memory;
			// reading them needs the memory path, kept separate.) Index 255 = "none".
			ad.Overlays = new List<HeadOverlayData>();
			for (int slot = 0; slot < OverlayCount; slot++) {
				int idx = Function.Call<int>(Hash.GET_PED_HEAD_OVERLAY, ped, slot);
				if (idx == 255) {
					continue;
				}
				// Opacity 1.0 and no tint (ColorType 0) are the neutral defaults; the
				// overlay still renders at full strength with its natural colour.
				ad.Overlays.Add(new HeadOverlayData(slot, idx, 1.0f, 0, 0, 0));
			}
		}

		// ---- Hair & eyes ------------------------------------------------------

		public static void ApplyHairAndEyes(Ped ped, AppearanceData ad) {
			Function.Call(Hash.SET_PED_COMPONENT_VARIATION, ped,
				(int)PedComponentType.Hair, ad.HairDrawable, ad.HairTexture, 0);
			Function.Call(Hash.SET_PED_HAIR_TINT, ped, ad.HairColor, ad.HairHighlightColor);
			if (ad.EyeColor >= 0) {
				Function.Call(Hash.SET_HEAD_BLEND_EYE_COLOR, ped, ad.EyeColor);
			}
		}

		public static void CaptureHairAndEyes(Ped ped, AppearanceData ad) {
			ad.HairDrawable = Function.Call<int>(Hash.GET_PED_DRAWABLE_VARIATION, ped, (int)PedComponentType.Hair);
			ad.HairTexture = Function.Call<int>(Hash.GET_PED_TEXTURE_VARIATION, ped, (int)PedComponentType.Hair);
			ad.EyeColor = Function.Call<int>(Hash.GET_HEAD_BLEND_EYE_COLOR, ped);
		}

		// ---- Apparel -----------------------------------------------------------

		// Component slots a snapshot captures. Face/Head/Hair are head-creation
		// slots handled elsewhere; clothing is Torso, Legs, Hands, Shoes, the two
		// specials and the second torso layer.
		static readonly PedComponentType[] ApparelSlots = {
			PedComponentType.Torso, PedComponentType.Legs, PedComponentType.Hands,
			PedComponentType.Shoes, PedComponentType.Special1, PedComponentType.Special2,
			PedComponentType.Special3, PedComponentType.Torso2,
		};

		public static void ApplyComponents(Ped ped, AppearanceData ad) {
			foreach (ComponentData c in ad.Components) {
				Function.Call(Hash.SET_PED_COMPONENT_VARIATION, ped, c.Type, c.Drawable, c.Texture, c.Palette);
			}
		}

		public static void CaptureComponents(Ped ped, AppearanceData ad) {
			ad.Components = new List<ComponentData>();
			foreach (PedComponentType slot in ApparelSlots) {
				int drawable = Function.Call<int>(Hash.GET_PED_DRAWABLE_VARIATION, ped, (int)slot);
				int texture = Function.Call<int>(Hash.GET_PED_TEXTURE_VARIATION, ped, (int)slot);
				int palette = Function.Call<int>(Hash.GET_PED_PALETTE_VARIATION, ped, (int)slot);
				ad.Components.Add(new ComponentData((int)slot, drawable, texture, palette));
			}
		}

		// ---- Props (earrings, glasses, hats, watches, bracelets) ---------------

		// Prop slots a snapshot captures. Props are a separate system from components:
		// 0 hats/helmets, 1 glasses, 2 ears (earrings), 6 watches, 7 bracelets. (Slots
		// 3-5 are unused on ped.) GET_PED_PROP_INDEX returns -1 for an empty slot.
		static readonly int[] PropSlots = { 0, 1, 2, 6, 7 };

		public static void ApplyProps(Ped ped, AppearanceData ad) {
			foreach (PropData p in ad.Props) {
				if (p.Drawable < 0) {
					// -1 = nothing worn in this slot; clear it so a previously-applied
					// prop on the fresh ped doesn't linger.
					Function.Call(Hash.CLEAR_PED_PROP, ped, p.Slot);
					continue;
				}
				Function.Call(Hash.SET_PED_PROP_INDEX, ped, p.Slot, p.Drawable, p.Texture, true);
			}
		}

		public static void CaptureProps(Ped ped, AppearanceData ad) {
			ad.Props = new List<PropData>();
			foreach (int slot in PropSlots) {
				int drawable = Function.Call<int>(Hash.GET_PED_PROP_INDEX, ped, slot);
				int texture = Function.Call<int>(Hash.GET_PED_PROP_TEXTURE_INDEX, ped, slot);
				ad.Props.Add(new PropData(slot, drawable, texture));
			}
		}

		// ---- Voice & movement --------------------------------------------------

		public static void ApplyVoiceAndMovement(Ped ped, AppearanceData ad) {
			if (ad.VoiceHash != 0) {
				Function.Call(Hash.SET_AMBIENT_VOICE_NAME_HASH, ped, ad.VoiceHash);
			}
			if (!string.IsNullOrEmpty(ad.MovingStyle)) {
				// The clipset dictionary must be streamed in before SET_PED_MOVEMENT
				// _CLIPSET takes effect; request it and wait (bounded) for the load,
				// or the set silently no-ops. 1.0 = full blend-in duration.
				Function.Call(Hash.REQUEST_CLIP_SET, ad.MovingStyle);
				for (int i = 0; i < 100 && !Function.Call<bool>(Hash.HAS_CLIP_SET_LOADED, ad.MovingStyle); i++) {
					Script.Wait(10);
				}
				Function.Call(Hash.SET_PED_MOVEMENT_CLIPSET, ped, ad.MovingStyle, 1.0f);
			} else {
				Function.Call(Hash.RESET_PED_MOVEMENT_CLIPSET, ped, 0.0f);
			}
			if (!string.IsNullOrEmpty(ad.Mood)) {
				// dict arg 0 — the standard facials dictionary is already streamed for a
				// freemode ped, so the override applies without a separate REQUEST.
				Function.Call(Hash.SET_FACIAL_IDLE_ANIM_OVERRIDE, ped, ad.Mood, 0);
			} else {
				Function.Call(Hash.CLEAR_FACIAL_IDLE_ANIM_OVERRIDE, ped);
			}
		}

		public static void CaptureVoiceAndMovement(Ped ped, AppearanceData ad, CaptureOptions opts) {
			// Voice DOES read back, via GET_AMBIENT_VOICE_NAME_HASH (the hash of the
			// active voice). Ped.Voice the SHVDN property is write-only; this native
			// is the getter. 0 means no override / model default.
			ad.VoiceHash = Function.Call<int>(Hash.GET_AMBIENT_VOICE_NAME_HASH, ped);
			// Moving style has no getter native, but the active clipset hash is read from
			// the ped's motion-task memory and resolved to a name via a known-style table
			// (MovingStyleMemory). Null = no override, an unknown/custom style, or unreadable —
			// leave it empty so apply uses the model default rather than a wrong style. Skipped
			// (left empty) when the caller opts out — apply then leaves the model's default walk.
			ad.MovingStyle = opts.MovingStyle ? (MovingStyleMemory.TryGetMovingStyle(ped) ?? "") : "";
			// Mood (facial idle override) likewise has no getter. Unlike the moving style it can't
			// be read synchronously here — the facial task churns and scanning it inline races that
			// and can fault the game — so it's resolved by a tick-driven deferred scan
			// (MoodCaptureFinder) BEFORE this runs; we just read its result. Empty if not located /
			// model default, or if the caller opted out of mood. See MoodMemory and the deferred-
			// snapshot flow in AppearanceKeeper.
			ad.Mood = opts.Mood ? (MoodMemory.Result ?? "") : "";
		}

		// ---- Tattoos / decals --------------------------------------------------

		public static void ApplyDecorations(Ped ped, AppearanceData ad) {
			// Builds without decoration support (Legacy, until the offset is probed) must NOT touch
			// decorations at all. A slot saved on Enhanced carries decoration hashes that don't
			// resolve here; clearing + re-adding them corrupts the rest of the look. Skipping leaves
			// the captured face/hair/clothing intact and just omits tattoos.
			if (!GameBuild.DecorationsSupported) {
				return;
			}
			// Only touch decorations if we actually captured the set. A snapshot taken
			// before the decoration memory read was available carries an empty list with
			// DecorationsFromMemory == false; clearing then would wipe the player's
			// tattoos and re-apply nothing. So a non-memory snapshot leaves tattoos alone.
			if (!ad.DecorationsFromMemory) {
				return;
			}
			// CLEAR first so a stale tattoo on the (re-spawned) ped doesn't survive, then
			// re-apply each captured pair. Scars are decorations too, but the freemode ped
			// we apply to is freshly spawned and carries none, so a plain clear is fine.
			Function.Call(Hash.CLEAR_PED_DECORATIONS, ped);
			foreach (DecorationData d in ad.Decorations) {
				Function.Call(Hash.ADD_PED_DECORATION_FROM_HASHES, ped, d.CollectionHash, d.OverlayHash);
			}
		}

		public static void CaptureDecorations(Ped ped, AppearanceData ad, bool capture) {
			// No native enumerates a ped's applied decorations (GET_PED_DECORATIONS_STATE is only a
			// whole-set checksum), so tattoos are read from the decoration array in memory
			// (PedDecorationMemory). That read needs the array base, which is resolved once per
			// session by DecorationBaseScan (a pattern resolve). Until the base is
			// known, TryFill returns false → Decorations empty + DecorationsFromMemory false, so
			// apply leaves the ped's tattoos untouched. The read is fully VirtualQuery-gated and the
			// base is re-validated each call, so it can't fault on a stale/wrong base.
			//
			// When the caller opts out (e.g. autosave with tattoo-save off), leave Decorations empty
			// and DecorationsFromMemory false — apply then never clears the ped's tattoos, so opting
			// out preserves whatever is on the ped rather than wiping it.
			ad.Decorations = new List<DecorationData>();
			if (!capture) {
				return;
			}
			// Read the live decorations via base + the ped's OWN slot index (ped+0x2E8), read fresh.
			// The base is session-global and stable; the index is per-ped, so this is correct for every
			// snapshot — including a second ped this session — with no stale cached list to go wrong.
			ad.DecorationsFromMemory = PedDecorationMemory.TryFill(ped, ad);
		}

		// ---- Full appearance ---------------------------------------------------

		// Applies an entire snapshot to the current player. Swaps the model first
		// so every head-blend/component native lands on the freemode ped. force: pass through to
		// SwitchModel for the auto re-apply, where a spoof/stranded hash can make Model.Hash already
		// read as the freemode model so a non-forced swap no-ops and the real body never changes.
		// realModelHash: the ped's TRUE model under a spoof (0 = read off the ped); passed to
		// SwitchModel so a re-apply while spoofed re-paints the look WITHOUT recreating the ped.
		public static bool Apply(AppearanceData ad, bool force = false, int realModelHash = 0) {
			if (!SwitchModel(ad.Model, force, realModelHash)) {
				return false;
			}
			Ped ped = Game.Player.Character;
			// Skip the face when it didn't round-trip: writing the garbage heritage/morphs of an
			// externally-authored face (Menyoo randomizer) produced a wrong generic face. Leave the
			// fresh ped's default face instead — the rest of the look still applies. (Overlays/hair
			// are independent and still useful, so they're not gated.)
			if (ad.FaceUsable) {
				ApplyHeritage(ped, ad);
				ApplyFaceFeatures(ped, ad);
			}
			ApplyOverlays(ped, ad);
			ApplyHairAndEyes(ped, ad);
			ApplyComponents(ped, ad);
			ApplyProps(ped, ad);
			ApplyDecorations(ped, ad);
			ApplyVoiceAndMovement(ped, ad);
			// Finalize the head blend. Leaving it un-finalized keeps the head in an
			// editable/locked state that blocks a later re-blend — so another mod
			// (e.g. Menyoo) applying its own ped afterwards would be silently
			// overridden by ours. We are preserve-only, not an editor, so we bake
			// the blend and release the lock, letting other tools take over after.
			Function.Call(Hash.FINALIZE_HEAD_BLEND, ped);
			return true;
		}

		// Captures the current freemode ped into a fresh AppearanceData. Reads back
		// everything the game exposes a getter for: model, heritage (face shape +
		// skin tone), head overlays, hair drawable/texture, eye colour and apparel.
		//
		// The 20 face micro-morphs and the overlay/hair tint palette ids have no readback
		// native (only RGB is exposed, which the setters can't take); they are recovered
		// from CPedHeadBlendData memory instead (PedHeadBlendMemory.TryFill). Voice reads
		// back via its hash native; the moving style and mood are read from memory
		// (MovingStyleMemory / MoodMemory). Returns null if the player is not a freemode ped
		// (the only model these natives behave on).
		public static AppearanceData Capture(Ped ped) {
			return Capture(ped, CaptureOptions.All);
		}

		// realModelHash: the ped's true freemode model when a spoof has overwritten the archetype
		// hash (0 = read it off the ped, the normal unspoofed case). The capture natives act on the
		// ped instance, whose body/head-blend memory stays freemode under the disguise.
		public static AppearanceData Capture(Ped ped, CaptureOptions opts, int realModelHash = 0) {
			int modelHash = realModelHash != 0 ? realModelHash : (ped?.Model.Hash ?? 0);
			if (!IsFreemodeHash(modelHash)) {
				return null;
			}
			var ad = new AppearanceData {
				Model = modelHash == new Model(FemaleModel).Hash ? FemaleModel : MaleModel,
				EyeColor = -1,
				MovingStyle = "",
				Mood = "",
			};
			// Heritage/overlay capture marshals struct out-pointers via
			// OutputArgument. Guard each so a marshalling hiccup logs and leaves that
			// group at its default rather than corrupting the whole snapshot — the
			// readable parts (apparel, eyes, voice) still get saved.
			try { CaptureHeritage(ped, ad); } catch (Exception e) { Logger.LogError("CaptureHeritage: " + e); }
			try { CaptureOverlays(ped, ad); } catch (Exception e) { Logger.LogError("CaptureOverlays: " + e); }
			CaptureHairAndEyes(ped, ad);
			CaptureComponents(ped, ad);
			CaptureProps(ped, ad);
			CaptureDecorations(ped, ad, opts.Tattoos);
			CaptureVoiceAndMovement(ped, ad, opts);
			EnsureFaceFeatureSlots(ad);
			// Refine from CPedHeadBlendData memory the fields natives don't expose: the 20
			// micro-morphs and overlay opacity, plus a cross-check of overlay drawable and
			// eye colour. Runs AFTER the native captures so it enriches them. If the struct
			// can't be located this is a no-op and the native-captured/default values
			// stand. Overlay TINT colours are not yet located on Enhanced, so they stay
			// default and OverlayTintFromMemory stays false (apply then skips tinting to
			// avoid the green/red-face bug).
			ad.HeadDataFromMemory = PedHeadBlendMemory.TryFill(ped, ad);
			// Flag a face that didn't round-trip (garbage heritage + zero morphs) so apply leaves
			// the ped's face untouched instead of writing the broken blend.
			ad.FaceUsable = !HasNoUsableFace(ad);
			// One-line summary of what this capture actually got — the quickest way to diagnose a
			// "field X didn't save" report from a shared log: it shows the model and counts plus which
			// memory reads succeeded (head-blend gates morphs/opacity; decorations gate tattoos).
			Logger.Log($"Capture: model={ad.Model} overlays={ad.Overlays.Count} components={ad.Components.Count} " +
				$"props={ad.Props.Count} decorations={ad.Decorations.Count} " +
				$"headBlendMem={ad.HeadDataFromMemory} faceUsable={ad.FaceUsable} tattooMem={ad.DecorationsFromMemory} " +
				$"movingStyle={(string.IsNullOrEmpty(ad.MovingStyle) ? "-" : ad.MovingStyle)} " +
				$"mood={(string.IsNullOrEmpty(ad.Mood) ? "-" : ad.Mood)}");
			return ad;
		}
	}
}
