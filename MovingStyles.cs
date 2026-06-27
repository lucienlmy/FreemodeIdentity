using System.Collections.Generic;

namespace FreemodeIdentity {
	// The set of moving-style (movement clipset) dictionary names this mod can recognise.
	//
	// SET_PED_MOVEMENT_CLIPSET takes a NAME string but the engine stores only its joaat
	// HASH (rage::fwClipSetManager keys fwClipSet by hash; the source string is discarded
	// at runtime). So a memory read of the active style yields a hash with no built-in way
	// back to a name. We bridge that by hashing every name below ourselves (Joaat) and
	// matching the read hash against the table — recovering the name the setter needs.
	//
	// This bounds capture to styles on the list. That is acceptable: a style a player can
	// realistically have applied comes from a finite pool of base-game movement clipsets, and
	// one we don't recognise simply falls back to the model default rather than being captured
	// wrong. Add names here to widen coverage.
	static class MovingStyles {
		// Base-game movement clipset dictionaries. Names are case-insensitive in the engine
		// (we lowercase before hashing). Sourced from the DurtyFree gta-v-data-dumps
		// (movementClipsetsWalking, the move_m@/move_f@/move_characters@/move_p_m walk set),
		// unioned with MenyooSP's Movement Styles list (its exact 109-entry picker) and
		// probe-verified variants — so every style a player could pick in Menyoo resolves.
		// This is a recognition allowlist: an entry only ever matches if the engine actually
		// has that clipset active, so a broad list is strictly better. Add more to widen.
		public static readonly string[] Names = {
			// Male personality / mood walks
			"move_m@bail_bond", "move_m@brave", "move_m@brave@a", "move_m@brave@b",
			"move_m@business@a", "move_m@business@b", "move_m@business@c", "move_m@buzzed",
			"move_m@casual@a", "move_m@casual@b", "move_m@casual@c", "move_m@casual@d",
			"move_m@casual@e", "move_m@casual@f", "move_m@caution", "move_m@chubby@a",
			"move_m@clipboard", "move_m@confident", "move_m@coward", "move_m@crazy",
			"move_m@depressed@a", "move_m@depressed@b", "move_m@depressed@c", "move_m@depressed@d",
			"move_m@drunk@a", "move_m@drunk@moderatedrunk", "move_m@drunk@slightlydrunk", "move_m@drunk@verydrunk",
			"move_m@fat@a", "move_m@fat@bulky", "move_m@favor_right_foot", "move_m@femme@",
			"move_m@fire", "move_m@flee@a", "move_m@flee@b", "move_m@flee@c",
			"move_m@flee@generic", "move_m@gangster@a", "move_m@gangster@generic", "move_m@gangster@ng",
			"move_m@gangster@var_e", "move_m@gangster@var_f", "move_m@gangster@var_g", "move_m@gangster@var_i",
			"move_m@generic", "move_m@golfer@", "move_m@hiking", "move_m@hipster@a",
			"move_m@hobo@a", "move_m@hobo@b", "move_m@hurry@a", "move_m@hurry@b",
			"move_m@hurry@c", "move_m@hurry_butch@a", "move_m@hurry_butch@b", "move_m@hurry_butch@c",
			"move_m@injured", "move_m@janitor", "move_m@jog@", "move_m@jogger",
			"move_m@joy@a", "move_m@leaf_blower", "move_m@melee", "move_m@money",
			"move_m@multiplayer", "move_m@muscle@a", "move_m@non_chalant", "move_m@plodding",
			"move_m@posh@", "move_m@power", "move_m@powerwalk", "move_m@prison_gaurd",
			"move_m@quick", "move_m@sad@a", "move_m@sad@b", "move_m@sad@c",
			"move_m@sassy", "move_m@scared@a", "move_m@shadyped@a", "move_m@shocked@a",
			"move_m@shy@a", "move_m@shy@b", "move_m@shy@c", "move_m@shy@d",
			"move_m@shy@e", "move_m@strung_out@", "move_m@swagger", "move_m@swagger@b",
			"move_m@tired", "move_m@tool_belt@a", "move_m@tough_guy@", "move_m@wading",
			// Menyoo-only male additions
			"move_m@alien", "move_m@brave@fallback", "move_m@brave@idle_a", "move_m@brave@idle_b",
			"move_m@drunk@moderatedrunk_head_up", "move_m@drunk@verydrunk_idles@",
			"move_m@intimidation@1h", "move_m@intimidation@cop@unarmed", "move_m@intimidation@unarmed",
			// Female personality / mood walks
			"move_f@arrogant@a", "move_f@arrogant@b", "move_f@arrogant@c", "move_f@business@a",
			"move_f@chichi", "move_f@chubby@a", "move_f@depressed@a", "move_f@depressed@b",
			"move_f@depressed@c", "move_f@drunk@a", "move_f@exhausted", "move_f@fat@a",
			"move_f@femme@", "move_f@film_reel", "move_f@film_reel_arms", "move_f@flee@a",
			"move_f@flee@b", "move_f@flee@c", "move_f@flee@generic", "move_f@gangster@ng",
			"move_f@generic", "move_f@handbag", "move_f@heels@c", "move_f@heels@d",
			"move_f@hiking", "move_f@hurry@a", "move_f@hurry@b", "move_f@injured",
			"move_f@jogger", "move_f@maneater", "move_f@multiplayer", "move_f@posh@",
			"move_f@runner", "move_f@sad@a", "move_f@sad@b", "move_f@sassy",
			"move_f@scared@a", "move_f@sexy", "move_f@sexy@a", "move_f@shy@a",
			"move_f@shy@b", "move_f@shy@c", "move_f@shy@d", "move_f@tool_belt@a",
			"move_f@tough_guy@",
			// Story-character movement sets
			"move_characters@amanda@bag", "move_characters@casey@nervous", "move_characters@dave_n@core@",
			"move_characters@floyd@core@", "move_characters@franklin@fire", "move_characters@jimmy@core@",
			"move_characters@jimmy@nervous@", "move_characters@jimmy@slow@", "move_characters@lamar@core",
			"move_characters@lester@std", "move_characters@lester@std_caneup", "move_characters@michael@fire",
			"move_characters@michael@gay", "move_characters@orleans@core@", "move_characters@patricia@core@",
			"move_characters@peter@core", "move_characters@ron@core@", "move_characters@tracey@core@",
			"move_characters@trevor@cough_run", "move_characters@trevor@gay",
			// Player, cop, and special movement sets
			"move_cop@action", "move_crawl", "move_lester_caneup", "anim_group_move_ballistic",
			"move_heist_lester", "move_injured_generic", "move_p_m_one", "move_p_m_one_briefcase",
			"move_p_m_one_fire", "move_p_m_two", "move_p_m_zero", "move_p_m_zero@first_person",
			"move_p_m_zero_fire", "move_p_m_zero_janitor", "move_p_m_zero_rucksack", "move_p_m_zero_slow",
			"move_ped_crouched",
		};

		// Lazily-built hash → name map for resolving a read clipset hash back to its name.
		// Built once; the table is small and static.
		static Dictionary<uint, string> hashToName;

		// Returns the clipset name whose joaat hash equals `hash`, or null if none in the
		// table matches (an unknown/custom style — caller falls back to the model default).
		public static string ResolveHash(uint hash) {
			if (hashToName == null) {
				var map = new Dictionary<uint, string>();
				foreach (string candidate in Names) {
					map[Joaat.Hash(candidate)] = candidate;
				}
				hashToName = map;
			}
			return hashToName.TryGetValue(hash, out string name) ? name : null;
		}
	}
}
