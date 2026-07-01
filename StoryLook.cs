using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using GTA;

namespace FreemodeIdentity {
	// The story protagonist's own OUTFIT (apparel components, props and hair), snapshotted at enable and
	// replayed on disable. Returning to a protagonist does a SET_PLAYER_MODEL that recreates their ped
	// with DEFAULT clothing and hair — the game restores their baked FACE but not what they were wearing,
	// so a custom outfit (e.g. one set via Menyoo) is lost on the swap unless we put it back.
	//
	// The face/heritage stay baked into the player_zero/one/two model and come back with the swap, so
	// only the reset parts are stored. A lightweight sibling to loadout.orig.dat / skills.orig.dat, not a
	// freemode appearance slot — it reuses PedAppearance's component/prop capture-apply but persists to
	// its own plain-text file. Char-bound like the others so a snapshot can't be replayed onto a
	// different protagonist.
	internal sealed class StoryLook {
		public const string DefaultStoreFileName = "look.orig.dat";
		const int FormatVersion = 1;

		readonly string storeFileName;
		public StoryLook(string storeFileName = DefaultStoreFileName) {
			this.storeFileName = storeFileName;
		}

		// The captured outfit lives in an AppearanceData so we can hand it straight to PedAppearance's
		// existing ApplyComponents/ApplyProps — only the Components and Props lists are ever populated.
		readonly AppearanceData look = new AppearanceData();
		string lastSaved;

		// Which protagonist char index (0/1/2) this outfit was captured from, so a restore refuses to
		// dress a DIFFERENT character in it (base swapped mid-spoof, or a snapshot left from another
		// save). -1 = nothing captured. Persisted. Mirrors Loadout/Skills' guard.
		int capturedChar = -1;
		public bool Captured => capturedChar >= 0 && look.Components.Count > 0;
		public bool MatchesChar(int charIdx) => capturedChar == charIdx;

		string StorePath => ScriptPaths.For(storeFileName);

		// Drop the snapshot so a faulted capture can't leave a prior session's outfit restorable.
		public void Clear() {
			look.Components = new List<ComponentData>();
			look.Props = new List<PropData>();
			capturedChar = -1;
		}

		// Snapshot the live protagonist's worn outfit, bound to their char index. Called at enable while
		// the player is still the genuine protagonist, before spoofing takes over. Returns false if the
		// ped is gone or the read faults (the caller logs and leaves nothing to restore).
		public bool CaptureFrom(Ped ped, int charIdx) {
			if (ped == null || !ped.Exists() || charIdx < 0 || charIdx > 2) {
				return false;
			}
			try {
				PedAppearance.CaptureComponents(ped, look);
				PedAppearance.CaptureProps(ped, look);
				// Hair too. PedAppearance's ApparelSlots deliberately omits the Hair slot (freemode hair is
				// head-blend-authored and restored via the appearance system), but a story protagonist's
				// hair is a plain component the recreate resets, so capture and replay it as one — drawable
				// + texture is the hairstyle. Stored in the same component list under the Hair slot type.
				CaptureHair(ped);
				capturedChar = charIdx;
			} catch (Exception e) {
				Logger.LogError("StoryLook.CaptureFrom: " + e);
				return false;
			}
			return Save();
		}

		// The Hair component slot (GTA.PedComponentType.Hair == 2). Read/written with the same drawable +
		// texture natives as any component, so appending it to look.Components lets ApplyComponents replay
		// it with the clothing — no separate apply path. Palette is unused for hair (0).
		const int HairSlot = 2;
		void CaptureHair(Ped ped) {
			int drawable = GTA.Native.Function.Call<int>(GTA.Native.Hash.GET_PED_DRAWABLE_VARIATION, ped, HairSlot);
			int texture = GTA.Native.Function.Call<int>(GTA.Native.Hash.GET_PED_TEXTURE_VARIATION, ped, HairSlot);
			look.Components.Add(new ComponentData(HairSlot, drawable, texture, 0));
		}

		// Replay the captured outfit onto the returned protagonist — ONLY onto the same character we
		// captured it from. Called after the return swap, where the recreated ped is in default clothing.
		public void RestoreTo(Ped ped, int charIdx) {
			if (!Captured || charIdx != capturedChar || ped == null || !ped.Exists()) {
				return;
			}
			try {
				PedAppearance.ApplyComponents(ped, look);
				PedAppearance.ApplyProps(ped, look);
			} catch (Exception e) {
				Logger.LogError("StoryLook.RestoreTo: " + e);
			}
		}

		// --- Persistence (never throws; missing = nothing captured) -------------------------

		// Line-oriented text, sibling to loadout.dat/skills.dat: a version line, a char line, one
		// "component <type> <drawable> <texture> <palette>" per apparel/hair slot and one
		// "prop <slot> <drawable> <texture>" per prop slot. A missing file is the valid empty state, and a
		// malformed line is skipped rather than fatal.
		public void Load() {
			try {
				if (!File.Exists(StorePath)) {
					return;
				}
				var comps = new List<ComponentData>();
				var props = new List<PropData>();
				foreach (string raw in File.ReadAllLines(StorePath)) {
					int comment = raw.IndexOf('#');
					string line = (comment >= 0 ? raw.Substring(0, comment) : raw).Trim();
					if (line.Length == 0) {
						continue;
					}
					string[] f = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
					switch (f[0]) {
						case "version":
							break; // shape is stable, nothing to act on yet
						case "char":
							if (!(f.Length == 2 && int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out capturedChar)
									&& capturedChar >= 0 && capturedChar <= 2)) {
								capturedChar = -1;
							}
							break;
						case "component":
							if (f.Length == 5
									&& int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ct)
									&& int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cd)
									&& int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ctx)
									&& int.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cp)) {
								comps.Add(new ComponentData(ct, cd, ctx, cp));
							}
							break;
						case "prop":
							if (f.Length == 4
									&& int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ps)
									&& int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pd)
									&& int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ptx)) {
								props.Add(new PropData(ps, pd, ptx));
							}
							break;
					}
				}
				look.Components = comps;
				look.Props = props;
			} catch (Exception ex) {
				Logger.LogError($"StoryLook: load failed ({ex.GetType().Name}) — no stored outfit.");
				Clear();
			}
			lastSaved = Serialize();
		}

		bool Save() {
			string text = Serialize();
			if (text == lastSaved) {
				return false;
			}
			try {
				File.WriteAllText(StorePath, text);
				lastSaved = text;
				return true;
			} catch {
				return false; // a failed write never crashes the mod — the in-memory outfit still restores
			}
		}

		string Serialize() {
			var sb = new StringBuilder();
			sb.Append("version ").Append(FormatVersion).Append('\n');
			if (capturedChar >= 0) {
				sb.Append("char ").Append(capturedChar).Append('\n');
			}
			foreach (ComponentData c in look.Components) {
				sb.Append("component ").Append(c.Type).Append(' ').Append(c.Drawable)
					.Append(' ').Append(c.Texture).Append(' ').Append(c.Palette).Append('\n');
			}
			foreach (PropData p in look.Props) {
				sb.Append("prop ").Append(p.Slot).Append(' ').Append(p.Drawable)
					.Append(' ').Append(p.Texture).Append('\n');
			}
			return sb.ToString();
		}
	}
}
