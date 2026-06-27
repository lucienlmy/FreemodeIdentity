using System.Collections.Generic;

namespace FreemodeIdentity {
	// The set of facial mood (idle-anim override) clip names this mod can recognise.
	//
	// SET_FACIAL_IDLE_ANIM_OVERRIDE takes a clip NAME string but has no getter, and the engine
	// stores only the name's joaat HASH in the ped's facial task (the source string is discarded).
	// So a memory read of the active mood yields a hash with no built-in way back to a name. We
	// bridge that by hashing every name below ourselves (Joaat) and matching the read hash —
	// recovering the name the setter needs.
	//
	// This is MenyooSP's exact Mood picker set, so any mood a player selected in Menyoo
	// resolves. Note the quirks: "burning_1" and "dead_1" have NO mood_ prefix, and
	// mood_smug_1 / mood_sulk_1 are Menyoo-specific. Moods are a small closed set, so this
	// table is complete rather than a sample.
	static class FacialMoods {
		// The facial idle-anim clip names (case-insensitive in the engine; Joaat lowercases
		// before hashing). Matches MenyooSP's vFacialAnims list.
		public static readonly string[] Names = {
			"mood_normal_1", "mood_aiming_1", "mood_angry_1", "mood_happy_1",
			"mood_injured_1", "mood_stressed_1", "mood_smug_1", "mood_sulk_1",
			"mood_sleeping_1", "mood_drunk_1", "burning_1", "dead_1",
		};

		static readonly Dictionary<uint, string> byHash = BuildLookup();

		static Dictionary<uint, string> BuildLookup() {
			var d = new Dictionary<uint, string>(Names.Length);
			foreach (string n in Names) {
				d[Joaat.Hash(n)] = n;
			}
			return d;
		}

		// The mood name for a joaat hash, or null if it isn't a recognised mood.
		public static string ResolveHash(uint hash) {
			return byHash.TryGetValue(hash, out string name) ? name : null;
		}
	}
}
