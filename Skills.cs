using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace FreemodeIdentity {
	// A freemode character's skill profile (strength, stamina, shooting, etc.). A freemode ped's
	// skills never PROGRESS on their own — the game's skill-up scripts only run for a genuine
	// protagonist — so this isn't preserving earned progress, it's a user-SET profile that makes a
	// freemode char read (and play) with the skills you chose instead of the spoofed protagonist's.
	//
	// The skills live in the same SP{N} stat namespace as the wallet's cash, resolved by the spoofed
	// protagonist's char index, so they only apply while spoofed. Neither STAT_SET_INT nor the GET
	// redirect can hold them — the stat manager reverts the native write and the gameplay code reads
	// the stat object in memory directly — so the native shim writes the value into that object each
	// frame (see ShimBridge.PushSkills + the shim's stats.cpp). C# owns the chosen profile here and
	// persists it to a file in the mod's %APPDATA% dir, like the wallet/loadout.
	internal sealed class Skills {
		const string StoreFileName = "skills.dat";
		const int FormatVersion = 1;

		// The seven per-character ability stats, in menu order. Name is the SP{N} stat suffix (also
		// the readable token in the .dat); Hash is joaat("SP{N}_<Name>") per char index. Hashes are
		// verified against the known cash/bank hashes (same namespace + resolution path).
		public static readonly string[] Names =
			{ "STRENGTH", "STAMINA", "SHOOTING", "STEALTH", "FLYING", "DRIVING", "LUNG" };
		// Friendly labels for the menu (the stat suffix isn't always the in-game name).
		public static readonly string[] Labels =
			{ "Strength", "Stamina", "Shooting", "Stealth", "Flying", "Driving", "Lung Capacity" };
		// joaat("SP{charIdx}_<STAT>") — the stat-suffix differs from the label for a few (WHEELIE =
		// driving, LUNG_CAPACITY = lung). Indexed [charIdx][skill].
		static readonly int[][] Hashes = {
			// SP0 (Michael): STRENGTH STAMINA SHOOTING STEALTH FLYING WHEELIE LUNG_CAPACITY
			new[] { unchecked((int)0x906B2799), unchecked((int)0x22C8AAA2), unchecked((int)0xB4892709), unchecked((int)0x2268B791), unchecked((int)0x78ABE4E6), unchecked((int)0x11B47270), unchecked((int)0x73968EBD) },
			// SP1 (Franklin)
			new[] { unchecked((int)0xB82874E3), unchecked((int)0x255EFFB5), unchecked((int)0xCB261497), unchecked((int)0xE76D0C23), unchecked((int)0xE98BEE3D), unchecked((int)0x7DD80AC8), unchecked((int)0x6C3BBB1A) },
			// SP2 (Trevor)
			new[] { unchecked((int)0x4F19E159), unchecked((int)0x7D8246AE), unchecked((int)0x2A3A74EA), unchecked((int)0xD03B7EEB), unchecked((int)0x77CF9710), unchecked((int)0x6BEF592F), unchecked((int)0x7E9487B3) },
		};

		// The chosen profile (0..100 per skill). This is the single shared profile for the freemode
		// identity — not per-protagonist (mirrors the one shared wallet/loadout).
		readonly int[] values = new int[Names.Length];
		string lastSaved; // last text written, to skip an unchanged re-write

		public int Count => Names.Length;
		public int Get(int skill) => (skill >= 0 && skill < values.Length) ? values[skill] : 0;

		// A copy of the chosen values, for pushing the profile to the shim (which pins them against the
		// game's reversion). Length == Count, in the same order as Names/Hashes.
		public int[] Values() => (int[])values.Clone();

		// The active SP{charIdx} skill stat hashes, in Names order — the set the shim redirects. Empty
		// for an out-of-range char index (not spoofed to a protagonist), so nothing gets pinned.
		public int[] HashesFor(int charIdx) {
			if (charIdx < 0 || charIdx > 2) {
				return new int[Names.Length];
			}
			return (int[])Hashes[charIdx].Clone();
		}

		static string StorePath => ScriptPaths.For(StoreFileName);

		// --- Set --------------------------------------------------------------------------

		// Record a user-chosen value and persist. Applying it to the game is the orchestrator's job
		// (it pushes the profile to the shim, which does the per-frame memory write). Clamped 0..100.
		public void Set(int skill, int value) {
			if (skill < 0 || skill >= values.Length) {
				return;
			}
			values[skill] = Math.Max(0, Math.Min(100, value));
			Save();
		}

		// --- Persistence (never throws; missing = all-zero profile) -------------------------

		// A missing file is the valid empty state (all skills 0); a malformed line is skipped. The
		// hash isn't stored — the stat NAME token is the key (stable, unlike a localized label), so a
		// line is `<NAME> <value>`. Names that don't match a known skill are ignored.
		public void Load() {
			try {
				if (!File.Exists(StorePath)) {
					return;
				}
				foreach (string raw in File.ReadAllLines(StorePath)) {
					int comment = raw.IndexOf('#');
					string line = (comment >= 0 ? raw.Substring(0, comment) : raw).Trim();
					if (line.Length == 0) {
						continue;
					}
					string[] tok = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
					if (tok.Length == 2 && tok[0].Equals("version", StringComparison.OrdinalIgnoreCase)) {
						continue; // version line — shape is stable, nothing to act on yet
					}
					if (tok.Length != 2) {
						continue;
					}
					int skill = Array.IndexOf(Names, tok[0].ToUpperInvariant());
					if (skill >= 0 && int.TryParse(tok[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) {
						values[skill] = Math.Max(0, Math.Min(100, v));
					}
				}
			} catch (Exception ex) {
				Logger.LogError($"Skills: load failed ({ex.GetType().Name}) — starting at a zero profile.");
			}
			lastSaved = Serialize();
		}

		// Best-effort persist; skips the write when nothing changed (a failed write never crashes
		// the mod — the in-memory profile still works for the session).
		void Save() {
			try {
				string text = Serialize();
				if (text == lastSaved) {
					return;
				}
				File.WriteAllText(StorePath, text);
				lastSaved = text;
			} catch {
				// swallow — see Logger discipline
			}
		}

		string Serialize() {
			var sb = new StringBuilder();
			sb.Append("version ").Append(FormatVersion).Append('\n');
			for (int i = 0; i < values.Length; i++) {
				sb.Append(Names[i]).Append(' ').Append(values[i]).Append('\n');
			}
			return sb.ToString();
		}
	}
}
