using System.Collections.Generic;

namespace FreemodeIdentity {
	// One head-overlay slot (blemishes, beard, eyebrows, makeup, ...); 13 slots exist.
	// Tintable slots also carry primary/secondary colour ids. Index 255 = none/clear.
	public class HeadOverlayData {
		public HeadOverlayData() { }
		public HeadOverlayData(int slot, int index, float opacity, int colorType, int firstColor, int secondColor) {
			Slot = slot;
			Index = index;
			Opacity = opacity;
			ColorType = colorType;
			FirstColor = firstColor;
			SecondColor = secondColor;
		}
		public int Slot { get; set; }
		public int Index { get; set; }
		public float Opacity { get; set; }
		// 0 = none, 1 = makeup palette, 2 = hair (eyebrow/beard) palette. Drives
		// which native colours the overlay; SET_PED_HEAD_OVERLAY_TINT needs it.
		public int ColorType { get; set; }
		public int FirstColor { get; set; }
		public int SecondColor { get; set; }
	}

	// One prop slot (hats=0, glasses=1, ears=2, watches=6, bracelets=7). A separate
	// system from components; drawable -1 means "nothing worn".
	public class PropData {
		public PropData() { }
		public PropData(int slot, int drawable, int texture) {
			Slot = slot;
			Drawable = drawable;
			Texture = texture;
		}
		public int Slot { get; set; }
		public int Drawable { get; set; }
		public int Texture { get; set; }
	}

	// One applied decoration (tattoo / badge). The game keys decorations by a pair of
	// joaat hashes — the overlay's collection (e.g. "mpbeach_overlays") and the overlay
	// name within it — which is exactly what ADD_PED_DECORATION_FROM_HASHES re-applies.
	// There is no native that enumerates a ped's applied decorations (only a whole-set
	// checksum), so capture reads the pair list from the ped's decoration memory.
	public class DecorationData {
		public DecorationData() { }
		public DecorationData(uint collectionHash, uint overlayHash) {
			CollectionHash = collectionHash;
			OverlayHash = overlayHash;
		}
		public uint CollectionHash { get; set; }
		public uint OverlayHash { get; set; }
	}

	// One apparel component slot (Torso, Legs, Shoes, ...). Drawable + texture
	// together identify a variation; palette is the optional alternate colourway
	// (the 4th arg to SET_PED_COMPONENT_VARIATION) some clothing items carry.
	public class ComponentData {
		public ComponentData() { }
		public ComponentData(int type, int drawable, int texture, int palette) {
			Type = type;
			Drawable = drawable;
			Texture = texture;
			Palette = palette;
		}
		// GTA.PedComponentType value (Face, Head, Hair, Torso, Legs, ...).
		public int Type { get; set; }
		public int Drawable { get; set; }
		public int Texture { get; set; }
		// Palette variation (0 for most items). Default 0 keeps old snapshots that
		// predate this field deserializing to the neutral palette.
		public int Palette { get; set; }
	}

	// The preserved player appearance: one snapshot of the live freemode ped. Fields the
	// game has no getter for (micro-morphs, overlay/hair tint) are filled from memory
	// (PedHeadBlendMemory); capture and apply must stay symmetric. A public XmlSerializer
	// DTO — needs the parameterless ctor and public settable members it has.
	public class AppearanceData {
		// User-chosen slot label, also the per-slot XML filename stem (sanitized). The
		// store keys slots by this. Old single-file snapshots that predate multi-slot
		// have no Name element and deserialize to null; the store names them on import.
		public string Name { get; set; }

		// The freemode model to apply: "mp_m_freemode_01" or "mp_f_freemode_01".
		public string Model { get; set; }

		// Heritage (GET/SET_PED_HEAD_BLEND_DATA). Shape and skin tone take SEPARATE parent
		// pairs, so both are stored. Parent ids are head ids 0..45; *Mix are 0..1 blends
		// (1.0 = full father). The *Third slot is the special-character heritage.
		public int ShapeFirst { get; set; }
		public int ShapeSecond { get; set; }
		public int ShapeThird { get; set; }
		public int SkinFirst { get; set; }
		public int SkinSecond { get; set; }
		public int SkinThird { get; set; }
		public float ShapeMix { get; set; }
		public float SkinMix { get; set; }
		public float ThirdMix { get; set; }

		// Face micro-morphs (SET_PED_MICRO_MORPH), index 0..19, each -1.0..1.0. Fixed-length
		// list so the 0.0 defaults round-trip. No getter native — read from memory.
		public List<float> FaceFeatures { get; set; } = new List<float>();

		// Head overlays: beard, eyebrows, makeup, blemishes, etc.
		public List<HeadOverlayData> Overlays { get; set; } = new List<HeadOverlayData>();

		// Hair drawable + its two tint palette ids. Stored here, not as an overlay, because
		// freemode hair is a component. The tint ids have no getter — read from memory.
		public int HairDrawable { get; set; }
		public int HairTexture { get; set; }
		public int HairColor { get; set; }
		public int HairHighlightColor { get; set; }

		// Eye colour palette index (SET_HEAD_BLEND_EYE_COLOR). -1 = leave default.
		public int EyeColor { get; set; }

		// True if the memory-only head data (overlay opacity, the 20 micro-morphs) was
		// captured this snapshot. False → those fields are defaults.
		public bool HeadDataFromMemory { get; set; }

		// True only if the overlay TINT colours (FirstColor/SecondColor per slot) were
		// genuinely captured. Apply must gate tinting on THIS, not HeadDataFromMemory:
		// applying a tintable overlay with uncaptured (0) colours forces palette index 0,
		// which renders as a vivid green/red mask. On Enhanced the tint ids aren't yet
		// located, so this stays false and apply skips tinting (better a missing brow
		// tint than a red face). Separate from HeadDataFromMemory, which can be true
		// (morphs/opacity captured) while tints are not.
		public bool OverlayTintFromMemory { get; set; }

		// Apparel components (torso, legs, shoes, ...).
		public List<ComponentData> Components { get; set; } = new List<ComponentData>();

		// Props (earrings, glasses, hats, watches, bracelets) — a separate system from components.
		public List<PropData> Props { get; set; } = new List<PropData>();

		// Ambient voice as the voice-name hash (Ped.Voice is write-only). 0 = model default.
		public int VoiceHash { get; set; }
		// Movement clipset (e.g. a tough-guy walk). Empty = no override (model default).
		public string MovingStyle { get; set; }

		// Back-compat: snapshots written before the rename stored this element as
		// "MovementClipset". XmlSerializer maps by element name, so this aliased
		// property lets an old file deserialize into MovingStyle. ShouldSerialize
		// returns false so we never WRITE the legacy element on new snapshots.
		[System.Xml.Serialization.XmlElement("MovementClipset")]
		public string MovementClipsetLegacy {
			get { return null; }
			set { if (!string.IsNullOrEmpty(value)) MovingStyle = value; }
		}
		public bool ShouldSerializeMovementClipsetLegacy() { return false; }

		// Facial idle-anim override (SET_FACIAL_IDLE_ANIM_OVERRIDE), e.g. happy/angry.
		// Empty = no override (model default).
		public string Mood { get; set; }

		// Tattoos / decals as (collection, overlay) hash pairs, read from decoration memory
		// (no native enumerates them). Empty may mean none OR unread — see DecorationsFromMemory.
		public List<DecorationData> Decorations { get; set; } = new List<DecorationData>();

		// True only if the decoration list was genuinely read from memory for this
		// snapshot. When false, Decorations is empty because we could not locate the
		// decoration array (not because the ped had none) — so apply must NOT clear the
		// ped's existing tattoos in that case, to avoid wiping tattoos we simply failed
		// to capture. Mirrors the HeadDataFromMemory / OverlayTintFromMemory guards.
		public bool DecorationsFromMemory { get; set; }

		// False when the captured heritage/morphs are garbage — an externally-authored face
		// (e.g. Menyoo's randomizer) that lives outside the head-blend system, so the values
		// the natives returned describe no real face. Apply must then leave the ped's current
		// face alone rather than writing the garbage blend, which produced a wrong generic
		// face. Defaults true so existing good slots (no element) still apply their face.
		public bool FaceUsable { get; set; } = true;
	}
}
