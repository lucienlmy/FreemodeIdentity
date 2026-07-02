using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace FreemodeIdentity {
	// Simulated skill progression for the freemode identity. A freemode ped NEVER progresses natively
	// (the game's skill-up scripts only run for a genuine protagonist, and STAT_SET_INT is reverted),
	// so "levelling up" here means WE accumulate our own XP from watched activity and promote whole
	// levels into the skill's VALUE (which lives in Skills, the number the user sees), then the shim's
	// per-frame pin holds that value. The pin doesn't care whether the value is fixed or rising — that's
	// the whole reason this is cheap.
	//
	// Each skill is independently HALTED or PROGRESSING:
	//   - Halted (locked)   → the value is held as-is, no climb. The original Skills behaviour, per-skill.
	//   - Progressing (free)→ climbs. Watched activity earns XP; each whole level crossed bumps the value
	//     in Skills by 1 (Earn returns the levels for the orchestrator to add). The number the user sees
	//     IS the progression — there's no separate "earned" readout.
	//
	// This owns only the lock flags, the SUB-LEVEL XP (progress toward the next level, below its cost) and
	// the speed curve; the value itself lives in Skills. One shared profile for the freemode identity
	// (like skills.dat), persisted to skillxp.dat.
	internal sealed class SkillProgress {
		public const string DefaultStoreFileName = "skillxp.dat";
		const int FormatVersion = 1;

		readonly int count;
		readonly bool[] locked;   // per-skill: true = halted (held), false = progressing (climbs)
		readonly float[] xp;      // per-skill sub-level XP: progress toward the next level, below its cost
		string lastSaved;         // last text written, to skip an unchanged re-write

		// The climb curve, dialled by a SPEED multiplier the user picks in the menu: 1.0 = the default
		// pace, 2.0 = twice as fast, 0.5 = half. Speed scales the whole curve (bigger = fewer XP per level
		// = faster). Base is picked so a full 0→100 on one skill is a solid chunk of active play at 1.0.
		const float BaseXpPerLevel = 300f;
		public const float DefaultSpeed = 1.0f;
		public const float MinSpeed = 0.1f, MaxSpeed = 32f; // clamp a hand-edited value sane (32x = the top preset)
		float speed = DefaultSpeed;

		// GTA's real skills are NOT linear — they slow down as they rise (the first half comes quickly, the
		// last stretch is a grind). So each level costs more than the last (see CostForLevel): at
		// CurveSteepness 4, level 99→100 costs 5x a level 0→1, front-loading the
		// climb like the native game. Speed scales the whole curve uniformly.
		const float CurveSteepness = 4f;
		float BaseCost => BaseXpPerLevel / speed;
		float CostForLevel(int level) => BaseCost * (1f + Math.Max(0, level) / 100f * CurveSteepness);

		string StorePath => ScriptPaths.For(DefaultStoreFileName);

		public SkillProgress(int skillCount) {
			count = skillCount;
			locked = new bool[count];
			xp = new float[count];
			// Default every skill HALTED: an install that flips Skills on expects the values it set to
			// hold, not to start drifting upward unasked. Progressing is an explicit per-skill opt-in.
			for (int i = 0; i < count; i++) locked[i] = true;
		}

		public bool IsLocked(int skill) => InRange(skill) && locked[skill];

		// Set the climb-speed multiplier (menu/ini-driven). Clamped so a bad value can't make every
		// progressing skill jump a level on the first XP tick, nor stall it dead at zero.
		public void SetSpeed(float value) {
			speed = Math.Max(MinSpeed, Math.Min(MaxSpeed, value));
		}
		public float Speed => speed;

		// Toggle a skill between halted and progressing. Freeing keeps the sub-level XP so a re-freed skill
		// resumes toward the next level where it left off; halting just stops the climb (the value already
		// earned stays in Skills). Only a deliberate value change (ResetSubLevelXp) drops the fraction.
		public void SetLocked(int skill, bool value) {
			if (!InRange(skill) || locked[skill] == value) return;
			locked[skill] = value;
			Save();
		}

		// The user set this skill's value directly, so the sub-level XP toward the NEXT level is meaningless
		// now — drop it, so a progressing skill climbs cleanly from the value just set rather than jumping
		// a level early on leftover XP. Idempotent-ish: only writes when something actually cleared.
		public void ResetSubLevelXp(int skill) {
			if (!InRange(skill) || xp[skill] == 0f) return;
			xp[skill] = 0f;
			Save();
		}

		// Award activity XP to a FREE skill and return how many WHOLE levels it just crossed (0 if none).
		// The value itself lives in Skills (the number the user sees) — the caller promotes those whole
		// levels into it — so xp here only ever holds the fraction toward the NEXT level. `currentLevel` is
		// that live value, needed because each level costs more than the last (CostForLevel). Private core
		// seam so a future external-mod bridge is a thin add; Earn is the wrapper.
		int AddXp(int skill, int currentLevel, float amount) {
			if (!InRange(skill) || locked[skill] || amount <= 0f) return 0;
			xp[skill] += amount;
			int levels = 0;
			// Drain whole levels, each costing more as the (rising) level climbs — so 90→100 takes far
			// longer than 0→10, matching the native game's diminishing returns.
			while (true) {
				float cost = CostForLevel(currentLevel + levels);
				if (xp[skill] < cost) break;
				xp[skill] -= cost;
				levels++;
			}
			return levels;
		}

		// The activity watcher's entry point: award XP to a free skill, returning whole levels earned for
		// the caller to add to the live value. Public wrapper over the private AddXp seam (kept private so
		// the bridge story stays a deliberate, separate decision).
		public int Earn(int skill, int currentLevel, float amount) => AddXp(skill, currentLevel, amount);

		bool InRange(int skill) => skill >= 0 && skill < count;

		// --- Persistence (never throws; missing = all-locked, zero-XP) ----------------------

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
					// Each skill line is `<NAME> <locked 0|1> <xp>` — keyed by the stable stat NAME token
					// (same key Skills uses), so a reordered Names array can't misalign the data.
					if (tok.Length != 3) {
						continue;
					}
					int skill = Array.IndexOf(Skills.Names, tok[0].ToUpperInvariant());
					if (skill < 0 || skill >= count) {
						continue;
					}
					if (int.TryParse(tok[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int lk)) {
						locked[skill] = lk != 0;
					}
					if (float.TryParse(tok[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) && x >= 0f) {
						xp[skill] = x;
					}
				}
			} catch (Exception ex) {
				Logger.LogError($"SkillProgress: load failed ({ex.GetType().Name}) — starting all-locked at zero XP.");
			}
			lastSaved = Serialize();
		}

		// Best-effort persist; skips the write when nothing changed. A failed write never crashes the mod —
		// the in-memory state still works for the session. Public so the throttled watcher can flush XP.
		public void Save() {
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
			for (int i = 0; i < count; i++) {
				sb.Append(Skills.Names[i]).Append(' ')
					.Append(locked[i] ? 1 : 0).Append(' ')
					.Append(xp[i].ToString("0.##", CultureInfo.InvariantCulture)).Append('\n');
			}
			return sb.ToString();
		}
	}
}
