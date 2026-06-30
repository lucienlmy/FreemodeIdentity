using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using GTA;
using LemonUI;
using LemonUI.Menus;

namespace FreemodeIdentity {
	// FreemodeIdentity — the merged AppearanceKeeper + FreemodeWallet mod. Both only ever
	// operate on a freemode (MP) player character in story mode:
	//   - APPEARANCE: snapshot a freemode ped's look and re-apply it (auto-apply on load,
	//     multiple slots, autosave) via a real SET_PLAYER_MODEL swap.
	//   - WALLET: give that freemode character a real spendable wallet — earn from cash
	//     pickups; a held identity SPOOF disguises the freemode ped as a story protagonist
	//     so shops open + charge a protagonist stat, which the native FreemodeIdentity.asi
	//     redirects to the wallet.
	//
	// The two used to be separate Script processes that coordinated the shared player
	// model/identity through a file (FreemodeWallet's ini), which bred every sync bug:
	// refuse-to-disable deadlock, stranded-hash-on-reload, auto-apply reapply loop. Merging
	// into ONE Script deletes that seam: the appearance Enabled/SourceModel/AutoApplyDone and
	// the wallet's spoof.Held are now the SAME process's in-memory fields. Apply-vs-spoof is
	// a deterministic ordering inside one tick (apply first, spoof last) instead of a race.
	public sealed class FreemodeIdentity : Script {
		// Derived from the assembly version (Properties\AssemblyInfo.cs), which the release
		// workflow stamps from the git tag — so the tag is the single source of truth, with
		// nothing to hand-bump. ToString(3) trims the 4-part .NET version to semver.
		static readonly string MenuVersion = "v" + Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

		readonly ScriptSettings Config;
		Keys menuKey; // assigned once in LoadConfig (called from the ctor)

		readonly ObjectPool Pool = new ObjectPool();

		// --- Wallet / spoof engine (from FreemodeWallet) ----------------------------------
		readonly Wallet wallet = new Wallet();
		readonly Spoof spoof = new Spoof();
		readonly Earning earning;
		readonly ShimBridge shim = new ShimBridge();

		// --- Loadout (weapons/armor/health a freemode ped loses) --------------------------
		readonly Loadout loadout = new Loadout();

		// === Menu ==========================================================================
		// The tree: MainMenu holds the two master checkboxes + the Appearance/Wallet anchors;
		// every other control nests under one of those two. *MenuItem fields are the anchors,
		// held so the tick loop can grey them out.
		NativeMenu MainMenu;
		NativeCheckboxItem EnabledItem;       // appearance master
		NativeCheckboxItem WalletEnabledItem; // wallet master

		NativeMenu AppearanceMenu;
		NativeItem AppearanceMenuItem;
		NativeMenu SlotsMenu;
		NativeItem SlotsMenuItem;             // greyed mid-snapshot, so a re-press can't Apply the stale file
		NativeItem SnapshotItem;
		NativeItem OverwriteActiveItem;
		NativeItem ApplyActiveItem;

		NativeMenu WalletMenu;
		NativeItem WalletMenuItem;
		NativeCheckboxItem EarningItem;

		NativeMenu SpoofMenu;                 // top-level Spoofing submenu
		NativeItem SpoofMenuItem;
		NativeCheckboxItem SpoofItem;
		NativeListItem<string> TargetItem;

		NativeMenu LoadoutMenu;              // Loadout ▸ — weapons/armor/health a freemode ped loses
		NativeItem LoadoutMenuItem;
		NativeCheckboxItem LoadoutEnabledItem;
		NativeCheckboxItem LoadoutWeaponsItem;
		NativeCheckboxItem LoadoutArmorItem;
		NativeCheckboxItem LoadoutHealthItem;
		NativeListItem<string> LoadoutPeriodItem;

		NativeMenu DebugMenu;                 // Debug ▸ — log level + live identity read-outs
		NativeListItem<string> LogLevelItem;
		NativeItem DbgSeenAsItem;             // disabled info rows, refreshed live while open
		NativeItem DbgBaseModelItem;
		NativeItem DbgSpoofItem;
		NativeItem DbgSourceItem;
		NativeItem DbgShimItem;

		// The per-slot actions offered on each slot's NativeListItem, in scroll order. Backup copies
		// the slot's current data aside; Apply from Backup restores that copy (a safety net around
		// Overwrite).
		static readonly string[] SlotActions = { "Apply", "Set Active", "Overwrite", "Backup", "Apply from Backup", "Rename", "Delete" };

		// Cosmetic prefix marking the active slot's list row: a coloured '>' (plain ASCII — the menu
		// font lacks ●/★ glyphs, which render as a missing-glyph box). GREEN when the active slot has a
		// backup, YELLOW when not — a glance shows whether the active look is recoverable. KeepNameCasing
		// keeps the lowercase colour codes; both share the trailing "> " so StripActiveMarker recovers
		// the slot name.
		const string ActiveMarkerGreen = "~g~>~s~ ";   // active + has backup
		const string ActiveMarkerYellow = "~y~>~s~ ";  // active, no backup

		// Strip whichever active marker prefixes the title, to recover the slot-name key.
		static string StripActiveMarker(string title) {
			if (title.StartsWith(ActiveMarkerGreen, StringComparison.Ordinal)) return title.Substring(ActiveMarkerGreen.Length);
			if (title.StartsWith(ActiveMarkerYellow, StringComparison.Ordinal)) return title.Substring(ActiveMarkerYellow.Length);
			return title;
		}

		// Notification helpers, so colour signals one consistent thing across the whole mod:
		// plain = a routine confirmation, ~y~ = a warning / blocked action the user should heed,
		// ~r~ = something failed. Warn/Fail colour only a short leading TAG then ~s~ back to plain,
		// so the emphasis lands on the headline, not the whole line. The single "busy" message is
		// shared so the several "snapshot still saving" call sites can't drift apart.
		static void Notify(string message) => GTA.UI.Notification.PostTicker(message, false);
		static void Warn(string tag, string detail) => GTA.UI.Notification.PostTicker($"~y~{tag}~s~ {detail}", false);
		static void Fail(string tag, string detail) => GTA.UI.Notification.PostTicker($"~r~{tag}~s~ {detail}", false);
		static void NotifyBusy() => Notify("Snapshot still saving - try again in a moment.");

		// The slot auto-applied on load while appearance is enabled. "" means none chosen.
		string ActiveSlot;

		// The look currently meant to be worn — what the clobber-defense re-asserts after a
		// death/respawn/model-swap. Set on EVERY deliberate apply (a slot OR an Apply from Backup),
		// so applying a backup of a different model isn't mistaken for a clobber of the active slot
		// and reverted. In-memory only and never persisted: a reload re-establishes it from the
		// active slot. Null falls back to the active slot's saved data.
		AppearanceData WornLook;

		// --- Edit Mode --------------------------------------------------------------------
		// While on, the mod stops defending the look: auto-apply re-assert is suspended and the
		// shop spoof is dropped, so external tools (Menyoo, etc.) can freely change the ped. The
		// spoof INTENT is kept and re-engages when Edit is turned off. Not persisted — always
		// starts off so a reload can never leave the mod silently passive.
		bool EditMode;
		NativeCheckboxItem EditModeItem;
		// Edit Mode is silent (the mod goes passive) and not persisted, so it's easy to leave on after
		// closing the menu. Re-warn periodically while it's on AND no menu is open, but only a few times
		// then go quiet — the point is to catch a forgotten toggle, not to nag a long editing session.
		// Both counters reset whenever a menu is open or Edit Mode flips, so re-entering Edit Mode gives
		// a fresh set. editModeReminderMs: game-time ms of the last reminder (-1 = none yet).
		int editModeReminderMs = -1;
		int editModeReminderCount;
		const int EditModeReminderIntervalMs = 60000;
		const int EditModeReminderMax = 3; // a few nudges, then assume it's deliberate

		// A snapshot queued into a slot. The capture is deferred across ticks (mood/head-blend/
		// tattoo scans), so the slot name + options are captured up front.
		string PendingSlotName;
		PedAppearance.CaptureOptions PendingOptions;
		bool PendingMakeActive;

		// --- Manual Save ------------------------------------------------------------------
		bool ManualTattoos;
		bool ManualMood;
		bool ManualMovingStyle;

		NativeMenu ManualMenu;
		NativeItem ManualMenuItem;
		NativeCheckboxItem ManualTattoosItem;
		NativeCheckboxItem ManualMoodItem;
		NativeCheckboxItem ManualMovingStyleItem;

		// Appearance master. On applies the active freemode look (auto-apply / Enable) and
		// remembers the story protagonist it replaced; off swaps back to that protagonist and
		// goes passive. Distinct from the wallet master (walletEnabled) — they are separate
		// features the user can run independently.
		bool Enabled;
		// The real story-protagonist model captured at load (before auto-apply/spoof), so
		// Disable can return there. "" = none captured.
		string SourceModel;
		// Fallback return target when no source protagonist was captured. One of
		// player_zero/one/two; defaults to Michael.
		string ReturnProtagonist;

		// Auto-apply settle + clobber-reapply (NOT a one-shot — re-arms on death/mission-fail
		// force-swap; see OnTick). SpoofSettleTarget below is deliberately > SettleTarget so
		// appearance applies first and the spoof engages last, both in this one tick loop.
		bool AutoApplyDone;
		int SettleTicks;
		const int SettleTarget = 30; // ~0.5s stable after the world is live before applying
		// Game-time ms of the last auto-apply, so the clobber re-arm holds off for a grace
		// window (an apply takes a few frames to read back as the new model). -1 = never applied.
		int LastAutoApplyMs = -1;
		const int ReapplyCooldownMs = 4000;
		// Handle of the player ped we last auto-applied onto. Used as ONE clobber signal: some
		// model swaps recreate the player ped (new handle), which wipes the look. 0 = none yet.
		int LastAppliedPedHandle;
		// Player death state last tick. A wasted→hospital revive does NOT recreate the ped or
		// change the model — it revives the SAME ped in place and RESETS its appearance (bald
		// default face/clothes), so neither the handle nor the model-hash clobber check sees it.
		// The dead→alive EDGE is the only reliable signal, so we watch it and re-arm on revive.
		bool WasDead;
		// Same shape as death but invisible to every other signal: a bust doesn't kill the ped,
		// swap the model or recreate the handle, yet the station walk-out resets the look like a
		// hospital revive — and it's not a death, so WasDead never catches it. Hence its own edge.
		bool WasArrested;
		// True while we're waiting out the post-revive sequence before re-applying. The dead→alive
		// edge fires, but the game then runs a walk-out cutscene that MOVES the player with control
		// taken away; our re-apply does a real SET_PLAYER_MODEL (recreates the ped), and doing that
		// mid-cutscene dropped the ped through the ground. So we hold until player control is back
		// (IS_PLAYER_CONTROL_ON via CanControlCharacter) — the reliable "cutscene over" signal — no
		// matter how long it runs. A wall-clock timeout backstops it so the re-apply can't be lost.
		bool ReviveApplyPending;
		int ReviveTimeoutMs = -1;
		const int ReviveTimeoutGraceMs = 15000; // backstop if control never reads on
		// When the safety gate (SwapBlockedReason) first started blocking a pending re-apply, and
		// whether we've already logged the stuck wait once. The wait is indefinite by design (the
		// look must stay defended), so a gate that never clears would otherwise be silent — we log
		// it ONCE after the same grace window as the revive backstop so a misreporting modlist is
		// diagnosable, not invisible.
		int SwapBlockedSinceMs = -1;
		bool SwapBlockedLogged;

		bool SnapshotPending;

		// True while a user snapshot is still being written (deferred capture). Apply must
		// wait or it would restore the PREVIOUS save.
		bool SnapshotInProgress =>
			SnapshotPending || MoodMemory.IsRunning || PedHeadBlendMemory.FindRunning;

		// --- Wallet / spoof config --------------------------------------------------------
		bool walletEnabled;
		bool earningEnabled;
		bool spoofEnabled; // persisted INTENT to spoof; the live hold re-engages lazily in OnTick
		string spoofTarget;
		// The ped's REAL model-info hash captured when the spoof last engaged, persisted so a
		// reload-while-spoofed can undo the stranded hash it can no longer read off the
		// (spoofed) ped. 0 = none recorded. Stored hex in [State] SpoofSourceHash.
		uint spoofSourceHash;
		bool strandedRecoveryDone; // one-shot guard for the startup stranded-hash recovery
		LogLevel logLevel;
		string buildOverride; // [General] Build: Auto (detect) | Enhanced | Legacy

		// Spoof settle gate (see AutoSpoofReady). > SettleTarget so appearance lands first.
		const int SpoofSettleTarget = 45;
		// Last spoof hash pair seen while held, kept so a release flip can re-key the waypoint back to
		// freemode even after Tick() zeroed the live hashes. See the WaypointKeeper flip in OnTick.
		uint lastFreemodeHash;
		uint lastSpoofHash;
		bool spoofHeldPrev; // previous tick's spoof.Held, for edge-detecting the flip across Start/Stop
		int spoofSettleTicks;
		int spoofSettlePed;
		int spoofSettleModel;
		// Backoff after a failed auto-spoof engage so a persistently-unspoofable ped (e.g. an
		// unhealable stranded poison) can't retry every tick — that busy-loop froze the game.
		int autoSpoofRetryMs = -1;
		const int AutoSpoofRetryCooldownMs = 3000;

		bool redirectLogged; // last-logged redirect state, to edge-trigger the transition log

		// --- Loadout config ---------------------------------------------------------------
		// Master + per-item toggles for the weapons/armor/health a freemode ped loses (the game
		// doesn't save them, and our model-swap recreates the ped bare). Master off = no sampling,
		// no restore. loadoutSavePeriodMs is the shared sampling interval; the menu offers presets.
		bool loadoutEnabled;
		bool loadoutWeapons;
		bool loadoutArmor;
		bool loadoutHealth;
		int loadoutSavePeriodMs;
		// Game-time ms of the last loadout sample, so OnTick samples on the period rather than every
		// frame. -1 = never sampled.
		int lastLoadoutSampleMs = -1;
		// The selectable sampling periods (ms), shown as the labels below. One shared timer covers
		// every loadout group.
		static readonly int[] LoadoutPeriodsMs = { 1000, 2000, 5000, 10000, 30000, 60000 };
		static readonly string[] LoadoutPeriodLabels = { "1s", "2s", "5s", "10s", "30s", "60s" };

		public FreemodeIdentity() {
			// Records the DLL folder for diagnostics; runtime writes go to %APPDATA%.
			ScriptPaths.Init(BaseDirectory);

			Logger.ClearLog();
			// Banner lines so the build + data dir are in the log even at Error level (triage).
			Logger.LogBanner($"FreemodeIdentity {MenuVersion} started. Files dir: {ScriptPaths.DataDirectory}");
			Logger.LogBanner("Head-blend memory capture available: " + PedHeadBlendMemory.Available);

			Config = ScriptSettings.Load(ScriptPaths.For("FreemodeIdentity.ini"));
			LoadConfig();

			earning = new Earning(wallet);
			wallet.Load();
			loadout.Load();
			Logger.LogBanner($"Config: edition={GameBuild.Current} enabled={Enabled} wallet={walletEnabled} earning={earningEnabled} spoof={spoofEnabled} target={spoofTarget} menuKey={menuKey}.");

			XmlAppearanceStorage.Initialize(ScriptPaths.DataDirectory);
			MenuInit();

			Tick += OnTick;
			KeyDown += OnKeyDown;
			Aborted += (s, a) => spoof.Stop("script abort"); // never leave a held spoof on teardown
		}

		// Read every setting once. The ini is grouped by feature, with all runtime state (not
		// user-editable) corralled in [State]:
		//   [General]    MenuKey, LogLevel, Build
		//   [Appearance] Enabled, ReturnProtagonist
		//   [ManualSave] MovingStyle, Tattoos, Mood
		//   [Wallet]     Enabled, Earning
		//   [Loadout]    Enabled, Weapons, Armor, Health, SavePeriodSeconds
		//   [Spoof]      Enabled, Target
		//   [State]      ActiveSlot, SourceModel, SpoofSourceHash
		void LoadConfig() {
			menuKey = Config.GetValue("General", "MenuKey", Keys.Shift | Keys.X);
			// Default to Debug for the 0.x pre-releases so every reported issue arrives with full
			// triage detail without asking the user to re-run. No path logs per tick, so the file
			// stays small. Flip this back to Info at 1.0. The menu description still says Info — the
			// user-facing "normal" level — on purpose.
			logLevel = ParseLogLevel(Config.GetValue("General", "LogLevel", nameof(LogLevel.Debug)));
			Logger.Threshold = logLevel;

			// Edition select: "Auto" (default) auto-detects Enhanced vs Legacy by host module
			// name; "Enhanced"/"Legacy" forces it. Resolved before any version-pinned constant
			// is read, so the override always wins.
			buildOverride = Config.GetValue("General", "Build", "Auto");
			GameBuild.Configure(buildOverride);

			// Read order matches the menu + the ini layout written below: Appearance (+ its ManualSave
			// sub-options), Wallet, Loadout, Spoof, State.
			Enabled = Config.GetValue("Appearance", "Enabled", true);
			ReturnProtagonist = Config.GetValue("Appearance", "ReturnProtagonist", "player_zero");

			// Read light-first (MovingStyle, Tattoos, Mood) to match the menu + ini order. Tattoos and
			// Moving Style default ON (both a quick read); Mood defaults off (a brief memory scan).
			ManualMovingStyle = Config.GetValue("ManualSave", "MovingStyle", true);
			ManualTattoos = Config.GetValue("ManualSave", "Tattoos", true);
			ManualMood = Config.GetValue("ManualSave", "Mood", false);

			walletEnabled = Config.GetValue("Wallet", "Enabled", true);
			earningEnabled = Config.GetValue("Wallet", "Earning", true);

			loadoutEnabled = Config.GetValue("Loadout", "Enabled", true);
			loadoutWeapons = Config.GetValue("Loadout", "Weapons", true);
			loadoutArmor = Config.GetValue("Loadout", "Armor", true);
			loadoutHealth = Config.GetValue("Loadout", "Health", true);
			// Stored as the period in seconds; snap an out-of-range value to the nearest preset so a
			// hand-edited ini can't yield an interval that isn't selectable in the menu.
			int periodSec = Config.GetValue("Loadout", "SavePeriodSeconds", 2);
			loadoutSavePeriodMs = NearestLoadoutPeriodMs(periodSec * 1000);

			spoofEnabled = Config.GetValue("Spoof", "Enabled", false);
			spoofTarget = Config.GetValue("Spoof", "Target", Identity.Franklin);
			if (Array.IndexOf(Identity.All, spoofTarget) < 0) {
				spoofTarget = Identity.Franklin;
			}

			ActiveSlot = Config.GetValue("State", "ActiveSlot", string.Empty);
			SourceModel = Config.GetValue("State", "SourceModel", string.Empty);
			spoofSourceHash = ParseHashHex(Config.GetValue("State", "SpoofSourceHash", "0"));

			// Write every key back so the ini always reflects the full, current layout (seeds a
			// fresh install, and completes a just-migrated one).
			Config.SetValue("General", "MenuKey", menuKey);
			Config.SetValue("General", "LogLevel", logLevel.ToString());
			Config.SetValue("General", "Build", buildOverride);
			Config.SetValue("Appearance", "Enabled", Enabled);
			Config.SetValue("Appearance", "ReturnProtagonist", ReturnProtagonist);
			Config.SetValue("ManualSave", "MovingStyle", ManualMovingStyle);
			Config.SetValue("ManualSave", "Tattoos", ManualTattoos);
			Config.SetValue("ManualSave", "Mood", ManualMood);
			Config.SetValue("Wallet", "Enabled", walletEnabled);
			Config.SetValue("Wallet", "Earning", earningEnabled);
			Config.SetValue("Loadout", "Enabled", loadoutEnabled);
			Config.SetValue("Loadout", "Weapons", loadoutWeapons);
			Config.SetValue("Loadout", "Armor", loadoutArmor);
			Config.SetValue("Loadout", "Health", loadoutHealth);
			Config.SetValue("Loadout", "SavePeriodSeconds", loadoutSavePeriodMs / 1000);
			Config.SetValue("Spoof", "Enabled", spoofEnabled);
			Config.SetValue("Spoof", "Target", spoofTarget);
			Config.SetValue("State", "ActiveSlot", ActiveSlot);
			Config.SetValue("State", "SourceModel", SourceModel);
			Config.SetValue("State", "SpoofSourceHash", spoofSourceHash.ToString("X8"));
			Config.Save();
		}

		static LogLevel ParseLogLevel(string value) =>
			Enum.TryParse(value, true, out LogLevel level) ? level : LogLevel.Info;

		static uint ParseHashHex(string value) =>
			uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out uint h) ? h : 0u;

		// Snap an arbitrary interval (ms) to the closest selectable preset, so a hand-edited ini value
		// always maps to a period the menu can display and round-trip.
		static int NearestLoadoutPeriodMs(int ms) {
			int best = LoadoutPeriodsMs[0];
			foreach (int p in LoadoutPeriodsMs) {
				if (Math.Abs(p - ms) < Math.Abs(best - ms)) {
					best = p;
				}
			}
			return best;
		}

		// Record (or clear) the ped's real model hash for stranded-hash recovery after a
		// reload. Called right after a successful engage with spoof.OriginalHash, and on
		// release with 0.
		void PersistSpoofSource(uint realHash) {
			if (spoofSourceHash == realHash) return;
			spoofSourceHash = realHash;
			Config.SetValue("State", "SpoofSourceHash", realHash.ToString("X8"));
			Config.Save();
		}

		// === Tick ==========================================================================
		// The unified per-frame sequence (handoff §5). The whole reason to merge was to make
		// appearance-apply and spoof-engage one deterministic order in ONE tick instead of two
		// racing scripts: capture source → auto-apply/clobber-reapply → spoof reengage →
		// spoof.Tick → earning → shim. Every old SpoofMarker FILE read is now spoof.Held in
		// memory, so "is the protagonist reading ours or genuine" is answered exactly.
		void OnTick(object sender, EventArgs e) {
			try {
				Pool.Process();

				// A disabled feature's whole submenu is greyed so its now-inert controls aren't
				// reachable (the master checkbox is the only way back on). Within Appearance, the
				// slot controls are additionally greyed mid-snapshot so a re-press can't Apply the
				// stale pre-capture file or restart the deferred finders.
				// Close the appearance-switch window only once the swap has FULLY settled — the look
				// applied and the spoof reached its intended state — so the master toggle can't be
				// re-pressed until the model swap (and any spoof re-engage) is truly done. A hard
				// timeout backstop releases it regardless, so a swap that never settles can't lock the
				// switch off forever.
				if (appearanceSwitching) {
					appearanceSwitchTicks++;
					if (SwitchFullySettled(Game.Player?.Character) || appearanceSwitchTicks >= AppearanceSwitchTimeoutTicks) {
						appearanceSwitching = false;
					}
				}

				bool busy = SnapshotInProgress;
				// Grey the Appearance master toggle while a switch is in flight so it can't be
				// re-pressed mid-swap (rapid re-press thrashed SET_PLAYER_MODEL and froze the game).
				if (EnabledItem != null) EnabledItem.Enabled = !appearanceSwitching;
				if (AppearanceMenuItem != null) AppearanceMenuItem.Enabled = Enabled;
				if (WalletMenuItem != null) WalletMenuItem.Enabled = walletEnabled;
				// The loadout children are inert while the master is off; grey them so it reads as a unit.
				if (LoadoutWeaponsItem != null) LoadoutWeaponsItem.Enabled = loadoutEnabled;
				if (LoadoutArmorItem != null) LoadoutArmorItem.Enabled = loadoutEnabled;
				if (LoadoutHealthItem != null) LoadoutHealthItem.Enabled = loadoutEnabled;
				if (LoadoutPeriodItem != null) LoadoutPeriodItem.Enabled = loadoutEnabled;
				if (SnapshotItem != null) SnapshotItem.Enabled = !busy;
				bool hasActiveSlot = !string.IsNullOrEmpty(ActiveSlot) && XmlAppearanceStorage.Exists(ActiveSlot);
				if (OverwriteActiveItem != null) OverwriteActiveItem.Enabled = !busy && hasActiveSlot;
				if (ApplyActiveItem != null) ApplyActiveItem.Enabled = !busy && hasActiveSlot;
				if (SlotsMenuItem != null) SlotsMenuItem.Enabled = !busy;
				if (ManualMenuItem != null) ManualMenuItem.Enabled = !busy;
				RefreshSubtitle();

				// Deferred snapshot work, tick-driven so neither trips the >5s watchdog. Give the
				// whole tick to whichever finder is still running; complete the snapshot only once
				// all are done.
				if (MoodMemory.IsRunning) { MoodMemory.Tick(); return; }
				if (PedHeadBlendMemory.FindRunning) { PedHeadBlendMemory.TickFind(); return; }
				if (SnapshotPending) {
					Ped pp = Game.Player?.Character;
					if (pp != null) {
						DoSnapshot(pp, PendingSlotName, PendingOptions, PendingMakeActive);
					}
					SnapshotPending = false;
					PendingSlotName = null;
					PendingMakeActive = false;
				}

				Ped player = Game.Player?.Character;

				// One-time stranded-hash recovery: if we reloaded WHILE spoofed, the previous
				// instance's hash write is still on the ped but the hold is gone, so the ped reads
				// as the target protagonist with nothing holding it. Using the real model hash we
				// persisted at engage, undo the strand so the world reads freemode again; the
				// normal reengage then takes over. (A single Script can still be reloaded mid-spoof.)
				if (!strandedRecoveryDone && player != null && player.Exists()) {
					strandedRecoveryDone = true;
					if (spoofEnabled && !spoof.Held && spoofSourceHash != 0) {
						spoof.RecoverStranded(spoofSourceHash);
					}
				}

				// Capture the REAL story protagonist BEFORE auto-apply swaps it (and before the
				// spoof engages — it settles later on purpose). ONLY while enabled: when disabled
				// we've just returned the player TO a protagonist, and capturing that would poison
				// SourceModel with the restored character — so a later protagonist switch (e.g. via
				// Menyoo) while disabled could never be recorded, and Disable would keep returning
				// the stale one. While disabled, the source is re-read fresh by the apply path on
				// the next enable. Reads spoof.Held in-memory, so a spoofed identity is never taken.
				if (Enabled) {
					RememberSourceIfProtagonist();
				}

				// Death edge — computed every tick, outside the auto-apply gate, so a revive while
				// the gate is momentarily closed is never missed. (revived consumed below.)
				bool isDead = player != null && player.IsDead;
				bool revived = WasDead && !isDead;
				// Drop the spoof the instant death is detected, BEFORE the hospital respawn runs.
				// Dying while spoofed leaves a protagonist hash painted on the shared freemode
				// model-info; the respawn machinery then streams the new ped against that poisoned
				// info and the load never completes — an infinite black screen. Releasing here lets
				// the respawn run on a clean freemode body. (The clobber path below still drops it as
				// a no-op fallback; the re-engage waits out the respawn via AutoSpoofReady's settle.)
				if (isDead && !WasDead && spoof.Held) {
					spoof.Stop("death edge");
				}
				WasDead = isDead;

				// Arrest edge — arg2 false asks for the busted state (held through the police-station
				// screen), so the falling edge is the walk-out. The recovery is identical to a revive
				// (look reset + control-off cutscene), so it rides the same revive handling below.
				// Only polled when there's a look to defend — no point invoking the native (the one
				// unconditional per-frame call here) for a recovery we'd never act on. Tracking is
				// reset while off so re-enabling can't read a stale falling edge.
				bool defendActive = Enabled && XmlAppearanceStorage.Exists(ActiveSlot);
				bool isArrested = defendActive && player != null
					&& GTA.Native.Function.Call<bool>(GTA.Native.Hash.IS_PLAYER_BEING_ARRESTED, Game.Player, false);
				bool released = false;
				if (defendActive) {
					released = WasArrested && !isArrested;
					// Same poison release as the death edge: the busted walk-out is a respawn-style
					// sequence too, so drop the spoof before it runs against the model-info.
					if (isArrested && !WasArrested && spoof.Held) {
						spoof.Stop("arrest edge");
					}
					WasArrested = isArrested;
				} else {
					WasArrested = false;
				}
				revived = revived || released;

				// Auto-apply is NOT a one-shot: the game can wipe the look long after load, and we
				// re-arm to restore it. The cooldown skips a just-applied swap that's still settling.
				// Suspended in Edit Mode so external tools can change the ped without it snapping back.
				if (Enabled && !EditMode && XmlAppearanceStorage.Exists(ActiveSlot)) {
					bool cooling = LastAutoApplyMs >= 0 && Game.GameTime - LastAutoApplyMs < ReapplyCooldownMs;
					if (AutoApplyDone && player != null && !SnapshotInProgress && !cooling) {
						// Defend the look actually worn (a slot OR an applied backup), falling back to the
						// active slot's saved data when nothing's been applied yet this session.
						AppearanceData active = WornLook ?? XmlAppearanceStorage.Get(ActiveSlot);
						// Three clobber signals, each for a different way the look gets lost:
						//  - revived (dead→alive): a wasted→hospital revive resets the look on the SAME
						//    ped/model — handle + hash both unchanged, so it's the ONLY signal for it.
						//    Fires regardless of spoof (death wipes the look either way).
						//  - pedRecreated (handle changed): a model swap rebuilt the player ped — a recreate
						//    spawns a DEFAULT (or protagonist) body, wiping the look, so it must re-apply.
						//    The spoof churning the ped on the shared model-info also trips this.
						//  - modelClobbered (hash mismatch): a force-swap to a protagonist. Gated on
						//    !Held — while spoofed the spoof PAINTS a protagonist hash, so the mismatch
						//    is our own disguise, not a clobber.
						bool pedRecreated = player.Handle != LastAppliedPedHandle;
						bool modelClobbered = !spoof.Held && active != null
							&& player.Model.Hash != new Model(active.Model).Hash;
						if (active != null && (revived || modelClobbered || pedRecreated)) {
							AutoApplyDone = false;
							SettleTicks = 0;
							// Drop any held spoof before re-applying. While held, the spoof paints a
							// protagonist hash and its OriginalHash snapshot can lie about the real model
							// (the game may have swapped the body to the genuine protagonist under the hold,
							// e.g. the savegame's story-character restore a few seconds after load). Reading
							// the model honestly here lets the re-apply force a real swap when the body truly
							// became a protagonist, instead of painting our clothes onto it. The spoof
							// re-engages on its own once we're back on a settled freemode ped.
							if (spoof.Held) {
								spoof.Stop("clobber re-apply");
							}
							// A revive needs to wait out the walk-out cutscene (player moved with control
							// off); a SET_PLAYER_MODEL during it drops the ped through the ground. The
							// other clobbers happen after the player is in control, so they don't wait.
							if (revived) {
								ReviveApplyPending = true;
								ReviveTimeoutMs = Game.GameTime + ReviveTimeoutGraceMs;
							}
							Logger.Log(released ? "Active look clobbered (released from arrest); re-applying once control returns."
								: revived ? "Active look clobbered (revived from death); re-applying once control returns."
								: modelClobbered ? $"Active look clobbered (model now {player.Model.Hash:X8}, expected {active.Model}); re-applying."
								: "Active look clobbered (player ped recreated — respawn); re-applying.");
						}
					}
					if (!AutoApplyDone) {
						// After a revive, hold the re-apply until the player is back IN CONTROL — the
						// reliable end-of-cutscene signal. The backstop timeout only stops us waiting on
						// CONTROL (a foreign respawn manager can hold control off indefinitely); it does
						// NOT force the swap. The swap itself stays gated on SafeToSwapModel() via `stable`
						// below, so the timeout can never fire a blind SET_PLAYER_MODEL into an unready
						// world — the failure mode behind the reported void-fall / stuck black screen.
						bool reviveHeld = false;
						if (ReviveApplyPending) {
							bool controlBack = player != null && Game.Player.CanControlCharacter;
							bool timedOut = ReviveTimeoutMs >= 0 && Game.GameTime >= ReviveTimeoutMs;
							if (controlBack || timedOut) {
								ReviveApplyPending = false;
								Logger.LogDebug($"Revive hold released ({(controlBack ? "control back" : "timeout")}).");
							} else {
								reviveHeld = true;
							}
						}
						string swapBlocked = SwapBlockedReason();
						bool stable = player != null && GTA.UI.Screen.IsFadedIn && !reviveHeld && swapBlocked == null;
						// Surface a safety gate that stays shut for too long — the wait never gives up,
						// so without this a modlist whose native misreports would just never re-apply.
						if (swapBlocked != null) {
							if (SwapBlockedSinceMs < 0) { SwapBlockedSinceMs = Game.GameTime; }
							else if (!SwapBlockedLogged && Game.GameTime - SwapBlockedSinceMs >= ReviveTimeoutGraceMs) {
								SwapBlockedLogged = true;
								Logger.LogDebug($"Re-apply still waiting after {ReviveTimeoutGraceMs / 1000}s — {swapBlocked}.");
							}
						} else {
							SwapBlockedSinceMs = -1;
							SwapBlockedLogged = false;
						}
						if (!stable) {
							SettleTicks = 0;
						} else if (SettleTicks < SettleTarget) {
							SettleTicks++;
						} else {
							AutoApplyDone = true;
							LastAutoApplyMs = Game.GameTime;
							ReapplyWornLook();
						}
					}
				}

				// Spoof reengage AFTER apply (SpoofSettleTarget > SettleTarget): a persisted/
				// intended spoof re-engages once it can take hold AND the world has settled. The
				// settle gate is reset by any model change, so we only engage once the ped (incl.
				// our own just-applied freemode look) has stopped changing — i.e. last. Independent
				// of the wallet: spoofing is a standalone disguise (shops then charge the
				// protagonist's own cash); the wallet redirect is an additive layer in SyncShim.
				// Edit Mode blocks it: the spoof paints the model hash every tick and would fight
				// external ped edits (intent is kept and re-engages when Edit is turned off).
				bool retryCooling = autoSpoofRetryMs >= 0 && Game.GameTime - autoSpoofRetryMs < AutoSpoofRetryCooldownMs;
				if (spoofEnabled && !EditMode && !spoof.Held && !retryCooling && AutoSpoofReady()) {
					Logger.LogDebug($"Auto-spoof gate ready (current={Identity.Current() ?? "freemode"}) — engaging {spoofTarget}.");
					// Pass the live ped's real freemode hash so Start can self-heal a stranded poison
					// (a protagonist hash left on the shared freemode model-info) instead of refusing
					// every tick — the busy-loop that froze the game on enable.
					if (spoof.Start(spoofTarget, RealFreemodeHash(player))) {
						PersistSpoofSource(spoof.OriginalHash);
						autoSpoofRetryMs = -1;
					} else {
						// Engage failed (not ready / unhealable strand). Back off so we don't spin.
						autoSpoofRetryMs = Game.GameTime;
					}
				}
				spoof.Tick();

				// On a spoof flip, re-key the map waypoint so it follows the identity (the game stores
				// waypoints keyed by ped model hash, which the spoof changes). The flip happens via
				// Start()/Stop() earlier in this tick, so we compare against a PERSISTENT field — a
				// local captured mid-tick would already reflect the new state and miss the edge. While
				// held the hashes read live; on a release flip they're already 0, so we use the pair we
				// remembered on the matching engage.
				if (spoof.Held != spoofHeldPrev) {
					spoofHeldPrev = spoof.Held;
					if (spoof.Held) {
						lastFreemodeHash = spoof.OriginalHash;
						lastSpoofHash = spoof.SpoofHash;
						WaypointKeeper.OnSpoofFlip(true, lastFreemodeHash, lastSpoofHash, shim.WaypointEntries);
					} else {
						WaypointKeeper.OnSpoofFlip(false, lastFreemodeHash, lastSpoofHash, shim.WaypointEntries);
					}
				}

				// Earning credits only while the wallet is on AND earning is on; it still tracks
				// pickups otherwise so the baseline stays correct.
				earning.Tick(walletEnabled && earningEnabled);

				SyncShim();

				// Sample the carryables a freemode ped loses (weapons/armor/health) on the shared
				// period so the persisted snapshot tracks what the player is actually carrying.
				SampleLoadout();

				// Periodic reminder that Edit Mode is still on while no menu is open (it's silent and
				// not persisted, easy to forget). Hold off while any menu is open — they're editing —
				// and reset then so the first reminder lands a full interval after they leave. Fires at
				// most EditModeReminderMax times, then goes quiet: a longer session is taken as deliberate.
				if (EditMode && !AnyMenuVisible()) {
					if (editModeReminderMs < 0) {
						editModeReminderMs = Game.GameTime;
					} else if (editModeReminderCount < EditModeReminderMax
							&& Game.GameTime - editModeReminderMs >= EditModeReminderIntervalMs) {
						editModeReminderMs = Game.GameTime;
						editModeReminderCount++;
						Warn("Edit Mode still on", "- your look isn't being defended. Save and turn it off in the menu.");
					}
				} else {
					editModeReminderMs = -1;
					editModeReminderCount = 0;
				}

				if (AnyMenuVisible()) {
					SyncSpoofItem();
					RefreshSpoofAvailability();
				}
				if (DebugMenu.Visible) {
					RefreshDebugMenu();
				}
			} catch (Exception ex) {
				// A held entity may go invalid between ticks and throw on access. Log and bail
				// for this frame rather than crashing the script.
				Logger.LogError(ex.ToString());
			}
		}

		// === Spoof gates + shim sync (from FreemodeWallet) =================================

		// Gate for the auto/persisted spoof re-engage. Must NOT spoof a half-loaded ped or
		// race our own appearance apply (which swaps the model ~0.5s after fade-in). Require a
		// live freemode BODY (not a genuine protagonist), the screen faded in, and the ped + model
		// UNCHANGED for a settle window — the stability check means we only engage AFTER the
		// ped (including our own apply) has stopped changing.
		bool AutoSpoofReady() {
			Ped ped = Game.Player?.Character;
			bool stable = ped != null && ped.Exists() && PlayerIdentity.IsFreemodeBody(ped)
				&& GTA.UI.Screen.IsFadedIn;
			if (!stable) {
				spoofSettleTicks = 0;
				return false;
			}
			int pedHandle = ped.Handle;
			int modelHash = ped.Model.Hash;
			if (pedHandle != spoofSettlePed || modelHash != spoofSettleModel) {
				spoofSettlePed = pedHandle;
				spoofSettleModel = modelHash;
				spoofSettleTicks = 0;
				return false;
			}
			if (spoofSettleTicks < SpoofSettleTarget) {
				spoofSettleTicks++;
				return false;
			}
			return true;
		}

		// Keep the native spend-shim in sync. Redirect is live only when the wallet is on AND
		// we're spoofed to a protagonist. The shim reports money events back as a signed
		// pendingDelta it accumulates; we apply + zero it, then push the authoritative balance.
		void SyncShim() {
			if (!shim.TryConnect()) {
				return; // shim not installed — earning still works, spending just won't redirect
			}
			bool redirect = walletEnabled && spoof.Held;
			int activeStat = redirect ? Identity.WalletStat(spoof.Target) : 0;
			int activeBankStat = redirect ? Identity.WalletBankStat(spoof.Target) : 0;

			if (redirect != redirectLogged) {
				redirectLogged = redirect;
				Logger.Log(redirect
					? $"Redirect ON — charging wallet as {spoof.Target} (stat 0x{activeStat:X8})."
					: "Redirect OFF — shop spending passes through.");
			}

			// Apply the shim's accumulated signed delta (debit < 0 / income > 0). As a delta it
			// composes with a same-tick world-pickup credit instead of overwriting it. Drain it
			// even when not redirecting, so a payout captured on the frame the spoof drops isn't
			// stranded.
			int delta = shim.ReadAndClearPendingDelta();
			if (delta != 0) {
				wallet.Apply(delta);
				Logger.Log($"{(delta < 0 ? "Debit" : "Income")} ${Math.Abs(delta)} via shim -> wallet ${wallet.Balance}.");
			}

			shim.Push(redirect, activeStat, activeBankStat, wallet.Balance, logLevel <= LogLevel.Debug ? 1 : 0);
		}

		// === Loadout: sample + restore weapons/armor/health ===============================

		// True once vitals (armor/health) have been restored this session. They restore on the FIRST
		// apply after load and on an appearance-enable — NOT on a respawn re-apply, where re-filling
		// what the game just reset on death would soften it. Weapons, by contrast, restore on every
		// re-apply (a recreated ped is bare). One-shot, re-armed by an explicit appearance-enable.
		bool loadoutVitalsRestored;
		// True once the loadout has been replayed onto the live ped at least once this session. Until
		// then the sampler must NOT run: on a (warm) load the appearance re-apply fires a few seconds
		// AFTER the first tick, so an early sample would read the bare just-loaded body and overwrite the
		// stored loadout with nothing — the "loadout gone after load" bug. Gating sampling on a completed
		// restore guarantees the store is replayed before we ever capture from the loaded ped.
		bool loadoutRestoredOnce;

		// Replay the saved loadout onto the live player ped. Called after an apply recreates the ped (it
		// spawns bare). Weapons always (gated by their toggle); vitals only when the caller says so (cold
		// load / appearance-enable). No-op unless the loadout feature is on.
		void RestoreLoadout(bool includeVitals) {
			if (!loadoutEnabled) {
				return;
			}
			Ped ped = Game.Player?.Character;
			if (ped == null || !ped.Exists()) {
				return;
			}
			if (loadoutWeapons) {
				loadout.RestoreWeapons(ped);
			}
			if (includeVitals) {
				loadout.RestoreVitals(ped, loadoutArmor, loadoutHealth);
				loadoutVitalsRestored = true;
			}
			loadoutRestoredOnce = true;
			Logger.LogDebug($"Loadout restored (weapons={loadoutWeapons} vitals={includeVitals} hasWeapons={loadout.HasWeapons}).");
		}

		// Sample the live ped's loadout on the shared period. Gated to the defended freemode identity:
		//   - Appearance must be ENABLED — the loadout belongs to the identity we wear; with appearance
		//     off the player is a genuine protagonist (or an undefended ped), whose own loadout we must
		//     not capture into the identity's store (the "Franklin's gear overwrote mine" bug).
		//   - the live BODY must be freemode — a second guard so a stale Enabled flag during a swap, or a
		//     genuine protagonist reached some other way, can't be sampled.
		//   - the stored loadout must already have been replayed once (loadoutRestoredOnce) — see below.
		//   - not mid-snapshot or in Edit Mode, where the ped is transient or being externally edited.
		//   - not dying / arrested / mid-transition (checked below) — the ped is being wiped, so a sample
		//     would capture a half-dead state.
		void SampleLoadout() {
			if (!loadoutEnabled || !Enabled || EditMode || SnapshotInProgress) {
				return;
			}
			// Wait for the stored loadout to be replayed onto the loaded ped before sampling, or the
			// first sample on a warm load reads the bare just-loaded body and clobbers the store.
			if (!loadoutRestoredOnce) {
				return;
			}
			if (lastLoadoutSampleMs >= 0 && Game.GameTime - lastLoadoutSampleMs < loadoutSavePeriodMs) {
				return;
			}
			Ped ped = Game.Player?.Character;
			if (ped == null || !ped.Exists() || !PlayerIdentity.IsFreemodeBody(ped)) {
				return;
			}
			// Don't sample during a death, arrest, or any non-playing/faded-out transition: the ped is
			// mid-wipe (the game strips weapons + drains health on the way down), so a sample then would
			// store a half-dead snapshot — and on the next restore replay that broken state. Only capture
			// a settled, in-control, faded-in freemode ped, i.e. a genuine "what I'm carrying" moment.
			if (ped.IsDead || !GTA.UI.Screen.IsFadedIn
					|| !Game.Player.CanControlCharacter
					|| GTA.Native.Function.Call<bool>(GTA.Native.Hash.IS_PLAYER_BEING_ARRESTED, Game.Player, false)) {
				return;
			}
			lastLoadoutSampleMs = Game.GameTime;
			// Log only when the snapshot actually changed (CaptureFrom persisted) — at a 2s period most
			// samples are identical, so an every-sample line would flood the log.
			if (loadout.CaptureFrom(ped, loadoutWeapons, loadoutArmor, loadoutHealth)) {
				Logger.LogDebug($"Loadout changed: weapons={loadout.WeaponCount} armor={loadout.Armor} health={loadout.Health}.");
			}
		}

		// === Appearance: snapshot / apply (from AppearanceKeeper) ==========================

		void SnapshotToNewSlot() {
			Ped player = Game.Player?.Character;
			if (player == null) {
				return;
			}
			if (SnapshotInProgress) {
				NotifyBusy();
				return;
			}
			string name = Game.GetUserInput(WindowTitle.EnterMessage20, string.Empty, 32);
			if (string.IsNullOrWhiteSpace(name)) {
				return;
			}
			BeginSnapshot(player, name.Trim(), ManualCaptureOptions(), makeActive: true);
		}

		void OverwriteSlot(string name) {
			Ped player = Game.Player?.Character;
			if (player == null) {
				return;
			}
			if (SnapshotInProgress) {
				NotifyBusy();
				return;
			}
			BeginSnapshot(player, name, ManualCaptureOptions(), makeActive: true);
		}

		void OverwriteActiveSlot() {
			if (string.IsNullOrEmpty(ActiveSlot) || !XmlAppearanceStorage.Exists(ActiveSlot)) {
				Warn("No active slot", "- set one in Saved Appearances first.");
				return;
			}
			OverwriteSlot(ActiveSlot);
		}

		void ApplyActiveSlot() {
			if (string.IsNullOrEmpty(ActiveSlot) || !XmlAppearanceStorage.Exists(ActiveSlot)) {
				Warn("No active slot", "- set one in Saved Appearances first.");
				return;
			}
			ApplySlot(ActiveSlot);
		}

		PedAppearance.CaptureOptions ManualCaptureOptions() {
			// DecorationsSupported is the single kill-switch for tattoos (true on both editions now
			// that the base resolves by pattern). Kept so a future build that can't resolve can
			// cleanly disable them without unreadable-set captures.
			bool tattoos = ManualTattoos && GameBuild.DecorationsSupported;
			return new PedAppearance.CaptureOptions { Tattoos = tattoos, MovingStyle = ManualMovingStyle, Mood = ManualMood };
		}

		// Shared snapshot kickoff. Mood + tattoos can't be read synchronously (the facial task
		// churns; the tattoo array base must be discovered), so they run tick-driven off the hot
		// path; we set SnapshotPending and the tick loop runs DoSnapshot once they finish.
		// The player's REAL model hash, seeing through a live spoof. Used by the capture path so a
		// snapshot taken while spoofed sees Freemode, not the impersonated protagonist.
		int RealPlayerModelHash() => PlayerIdentity.RealModelHash(Game.Player?.Character, spoof);

		void BeginSnapshot(Ped player, string slotName, PedAppearance.CaptureOptions opts, bool makeActive = false) {
			if (PedAppearance.IsFreemodeHash(RealPlayerModelHash())) {
				if (opts.Mood) {
					MoodMemory.Begin(player);
				} else {
					MoodMemory.Disable();
				}
				PedHeadBlendMemory.BeginFind(player);
				// Arm the decoration base from the native shim (it scans the live .text — the only way
				// on Enhanced, whose .text is encrypted) or our own pattern scan (Legacy plaintext).
				// Both are instant content-free pattern resolves; if neither matches (a future build),
				// tattoos are simply skipped this snapshot — we never touch the ped's decorations to go
				// looking, so a miss can't wipe a real tattoo.
				if (opts.Tattoos && !PedDecorationMemory.BaseKnown) {
					shim.TryConnect();
					DecorationBaseScan.TryArm(shim.DecorationBase);
				}
				PendingSlotName = slotName;
				PendingOptions = opts;
				PendingMakeActive = makeActive;
				SnapshotPending = true;
				Notify($"Preparing snapshot for \"{slotName}\" - saving in a moment...");
				return;
			}
			DoSnapshot(player, slotName, opts, makeActive);
		}

		void DoSnapshot(Ped player, string slotName, PedAppearance.CaptureOptions opts, bool makeActive = false) {
			AppearanceData ad = PedAppearance.Capture(player, opts, RealPlayerModelHash());
			if (ad == null) {
				Warn("Not a freemode character",
					"- only Freemode Male/Female peds are preserved. A custom or addon model's face is part of the model.");
				return;
			}
			try {
				ad.Name = slotName;
				CarryForwardUncaptured(ad, opts, XmlAppearanceStorage.Get(slotName));
				XmlAppearanceStorage.Save(ad);
				Logger.Log($"Saved slot \"{slotName}\"");
				if (makeActive || string.IsNullOrEmpty(ActiveSlot)) {
					SetActiveSlot(slotName);
				}
				RebuildSlotsMenu();
				if (!ad.FaceUsable) {
					// This face lives outside the head-blend system (e.g. Menyoo's randomizer), so it
					// can't round-trip. Apply will keep the ped's current face rather than restore one.
					Warn($"Saved \"{slotName}\" without the face",
						"- this face can't be preserved. Build one with the heritage sliders or the in-game creator. Clothes, hair and props were saved.");
				} else {
					Notify($"Saved appearance \"{slotName}\"");
				}
			} catch (Exception ex) {
				Logger.LogError(ex.ToString());
				Fail("Couldn't save appearance", "- see the log.");
			}
		}

		static void CarryForwardUncaptured(AppearanceData ad, PedAppearance.CaptureOptions opts, AppearanceData prev) {
			if (prev == null) {
				return;
			}
			if (!opts.Tattoos) {
				ad.Decorations = prev.Decorations;
				ad.DecorationsFromMemory = prev.DecorationsFromMemory;
			}
			if (!opts.Mood) {
				ad.Mood = prev.Mood;
			}
			if (!opts.MovingStyle) {
				ad.MovingStyle = prev.MovingStyle;
			}
		}

		void ApplySlot(string name, bool silent = false) {
			if (SnapshotInProgress) {
				if (!silent) {
					NotifyBusy();
				}
				return;
			}
			AppearanceData ad = XmlAppearanceStorage.Get(name);
			if (ad == null) {
				if (!silent) {
					Fail("No such saved appearance", $"- \"{name}\" is missing.");
				}
				return;
			}
			RememberSourceIfProtagonist();
			try {
				bool ok = PedAppearance.Apply(ad);
				Logger.Log($"Apply slot=\"{name}\" model={ad.Model} -> {(ok ? "OK" : "FAILED (model switch?)")}{(silent ? " (auto)" : "")}");
				if (ok) {
					// This is now the worn look the clobber-defense should re-assert, and the ped it
					// landed on (so a later respawn that recreates the ped is detected). Start the
					// cooldown too, so the swap we just did isn't itself read as a clobber next tick.
					WornLook = ad;
					LastAppliedPedHandle = Game.Player?.Character?.Handle ?? 0;
					LastAutoApplyMs = Game.GameTime;
					// The apply recreated a bare ped: restore the saved loadout. Vitals ride the same
					// first-time-this-session gate as the auto-apply path (re-armed by appearance-enable),
					// so enabling Appearance brings back health/armor while a routine re-apply doesn't.
					RestoreLoadout(includeVitals: !loadoutVitalsRestored);
					// A manual Apply during the post-revive walk-out would otherwise leave the backstop
					// armed to fire a second, redundant swap ~15s later. (Only the timer — NOT AutoApplyDone,
					// which the appearance-switch flow re-arms here on purpose.)
					ReviveApplyPending = false;
				}
				if (!silent) {
					if (ok) {
						Notify($"Applied \"{name}\"");
					} else {
						Fail("Couldn't apply appearance", "- the model wouldn't switch.");
					}
				}
			} catch (Exception ex) {
				Logger.LogError(ex.ToString());
				if (!silent) {
					Fail("Couldn't apply appearance", "- see the log.");
				}
			}
		}

		// World-ready gate for a respawn re-apply. ReapplyWornLook's SET_PLAYER_MODEL recreates the
		// ped, which is fatal if the spawn isn't streamed in yet: the ped drops through unloaded
		// collision (the "void fall") or the respawn fade never completes (the "infinite black
		// screen"), both reported on heavy modlists where a foreign respawn manager flips control/fade
		// before the world is actually safe — so control+fade alone don't prove it's safe to swap.
		// Returns the name of the first failing precondition, or null when safe — so a stuck wait can
		// be logged with the reason instead of silently never re-applying.
		string SwapBlockedReason() {
			Ped p = Game.Player?.Character;
			if (p == null || !p.Exists() || p.IsDead) return "ped not alive";
			if (GTA.Native.Function.Call<bool>(GTA.Native.Hash.GET_IS_LOADING_SCREEN_ACTIVE)) return "loading screen";
			if (!GTA.Native.Function.Call<bool>(GTA.Native.Hash.IS_PLAYER_PLAYING, Game.Player)) return "player not playing";
			if (!GTA.Native.Function.Call<bool>(GTA.Native.Hash.HAS_COLLISION_LOADED_AROUND_ENTITY, p)) return "collision not loaded";
			// A warm load (load-save while running) restores the saved protagonist body asynchronously,
			// and the world fades in / reports playing BEFORE that restore finishes. Our forced
			// SET_PLAYER_MODEL firing into that in-flight restore builds the freemode body against the
			// still-loading protagonist resources — the broken floating-head render. The engine drives
			// the restore through the player-switch machinery, so block the swap until it's done.
			if (GTA.Native.Function.Call<bool>(GTA.Native.Hash.IS_PLAYER_SWITCH_IN_PROGRESS)) return "player switch in progress";
			// Hospital respawn fall-through: right after a revive the world fades in and collision
			// reports loaded a beat BEFORE the floor mesh under the NEW spawn point is actually solid.
			// A forced SET_PLAYER_MODEL then recreates the ped onto an unsolid floor and it drops
			// through. The tell is the ped sitting in the air with nothing under it — block the swap
			// until it's grounded (or in a vehicle, where height-above-ground is meaningless). The
			// threshold is generous so a step/curb never blocks; only a real "no floor yet" gap (the
			// ped hovering metres up post-respawn) trips it.
			if (!p.IsInVehicle()) {
				float heightAboveGround = GTA.Native.Function.Call<float>(GTA.Native.Hash.GET_ENTITY_HEIGHT_ABOVE_GROUND, p);
				if (heightAboveGround > 3.0f) return "not grounded (floor not solid yet)";
			}
			return null;
		}

		// Silent clobber re-assert. Re-applies the look actually worn (an applied backup, or the
		// active slot's current saved data) — NOT blindly the active slot, so a worn backup survives a
		// respawn instead of being reverted to the active slot. Reads the active slot fresh so an
		// Overwrite between applies is honoured; a worn backup is re-applied from its in-memory data.
		void ReapplyWornLook() {
			if (SnapshotInProgress) return;
			AppearanceData ad = WornLook ?? XmlAppearanceStorage.Get(ActiveSlot);
			if (ad == null) return;
			RememberSourceIfProtagonist();
			try {
				// FORCE a real SET_PLAYER_MODEL whenever the live body isn't already the worn freemode
				// model. The clobber path drops any spoof before getting here, so RealPlayerModelHash now
				// reads honestly: if the game swapped the body to a protagonist (story-character restore
				// after load, a force-swap), a real swap is needed — painting our clothes onto a
				// protagonist body would just leave it looking wrong. When the body IS already our model
				// (spoof ped-churn that kept it), don't force: re-paint IN PLACE so the look that a
				// recreate wiped is restored without a swap that recreates the ped and re-fires the loop.
				bool force = RealPlayerModelHash() != new Model(ad.Model).Hash;
				bool ok = PedAppearance.Apply(ad, force, force ? 0 : RealPlayerModelHash());
				Logger.Log($"Reapply worn look model={ad.Model} -> {(ok ? "OK" : "FAILED (model switch?)")}{(force ? " (forced)" : "")} (auto)");
				if (ok) {
					LastAppliedPedHandle = Game.Player?.Character?.Handle ?? 0;
					// The re-apply recreated a bare ped: give weapons back every time. Restore vitals only
					// the FIRST time this session (the cold-load apply) — a respawn-triggered re-apply must
					// not re-fill the health/armor the game just reset on death.
					RestoreLoadout(includeVitals: !loadoutVitalsRestored);
				}
			} catch (Exception ex) {
				Logger.LogError(ex.ToString());
			}
		}

		// The live ped's true freemode model hash, for the spoof's stranded-poison self-heal. The
		// ped's own Model.Hash can't be trusted here (that's exactly what a poison corrupts), so
		// prefer the model the worn look applied, then the persisted engage-time source, then the
		// body's gender. Returns 0 if none resolves (heal then declines, leaving the refuse path).
		uint RealFreemodeHash(Ped ped) {
			AppearanceData worn = WornLook ?? (XmlAppearanceStorage.Exists(ActiveSlot) ? XmlAppearanceStorage.Get(ActiveSlot) : null);
			if (worn != null && !string.IsNullOrEmpty(worn.Model)) {
				uint h = unchecked((uint)new Model(worn.Model).Hash);
				if (PedAppearance.IsFreemodeHash(unchecked((int)h))) return h;
			}
			if (PedAppearance.IsFreemodeHash(unchecked((int)spoofSourceHash))) return spoofSourceHash;
			if (ped != null && ped.Exists()) {
				string model = ped.Gender == Gender.Female ? PedAppearance.FemaleModel : PedAppearance.MaleModel;
				return unchecked((uint)new Model(model).Hash);
			}
			return 0;
		}

		// Record the live story protagonist as the "original" to return to on Disable. No-ops once
		// captured / while the body is freemode (incl. a spoof painting a protagonist hash — that
		// reading is OURS, not real, and PlayerIdentity sees through it from the live body).
		void RememberSourceIfProtagonist() {
			if (!string.IsNullOrEmpty(SourceModel)) return;
			string genuine = PlayerIdentity.GenuineProtagonist(Game.Player?.Character, spoof);
			if (genuine == null) return;
			SourceModel = Identity.ModelName(genuine);
			Config.SetValue("State", "SourceModel", SourceModel);
			Config.Save();
			Logger.Log($"Captured source protagonist model={SourceModel}.");
		}

		// True from the moment an Appearance toggle starts a model swap until that swap has settled.
		// Each flip does a blocking SET_PLAYER_MODEL (apply a freemode look, or swap back to the
		// protagonist) that destroys + recreates the player ped; flipping again before it settles
		// thrashed the swap and eventually FROZE the game (observed: ~6 enable/disable cycles in 12s
		// deadlocked the streamer). While this is set, the Appearance checkbox is greyed in OnTick so
		// the switch can't be re-pressed mid-swap. Cleared once the auto-apply settles (OnTick) or the
		// disable's return swap finishes.
		bool appearanceSwitching;

		// Appearance master toggle. Enable applies the active freemode look; Disable swaps back
		// to the story protagonist.
		void SetEnabled(bool on) {
			if (on == Enabled) return;
			// A switch is still in flight. Greying the item only dims it — LemonUI still flips Checked
			// and fires this on a press — so we must REVERT the checkbox to the real state and bail,
			// or the box desyncs from Enabled and the mismatch drives an apply/return loop.
			if (appearanceSwitching) {
				if (EnabledItem != null) EnabledItem.Checked = Enabled;
				return;
			}

			// Disabling swaps the player to a genuine protagonist, which a held spoof can't sit on
			// (Spoof.Tick auto-releases on the model change, and re-engage is gated on a freemode
			// ped — so it can't hijack the real protagonist's cash). Release the live hold now so
			// the swap is clean, but DON'T clear spoofEnabled: the spoof is a wallet feature, and
			// turning appearance off shouldn't forget the user's wallet-spoof intent — it just
			// goes dormant until they're a freemode ped again.
			//
			// Capture the spoof target BEFORE stopping: with no captured SourceModel (you began as a
			// freemode ped, never a real protagonist), the spoof's impersonated identity is the only
			// sensible character to return to. Reading it after spoof.Stop would lose it and fall
			// back to ReturnProtagonist (the "disable returned me to Michael not Franklin" bug).
			string spoofedIdentity = null;
			if (!on && spoof.Held) {
				spoofedIdentity = spoof.Target;
				spoof.Stop("appearance disable");
				if (SpoofItem != null) SpoofItem.Checked = spoofEnabled;
			}

			// Don't touch Edit Mode here. Force-clearing it on disable desynced the flag from its
			// checkbox (LemonUI won't redraw a closed submenu), so the box read on while the mod
			// resumed defending the look. Only SetEditMode flips it now, so the two can't disagree.
			Enabled = on;
			Config.SetValue("Appearance", "Enabled", Enabled);
			Config.Save();
			if (EnabledItem != null) EnabledItem.Checked = Enabled;

			if (SnapshotInProgress) {
				NotifyBusy();
				return;
			}
			if (on) {
				// Skip the apply only when Edit Mode is on AND we're still on the edited freemode ped —
				// snapping the slot back would clobber the live edit. After a disable swapped us to the
				// protagonist there's no edit left to protect, so apply normally.
				if (EditMode && PlayerIdentity.IsFreemodeBody(Game.Player?.Character)) {
					Warn("Edit Mode is on", "- your live edit is left alone. Turn Edit Mode off to apply the active slot.");
					return;
				}
				if (string.IsNullOrEmpty(ActiveSlot) || !XmlAppearanceStorage.Exists(ActiveSlot)) {
					Warn("No active slot", "- set one to apply a saved look.");
					return;
				}
				// Enabling Appearance is an explicit "make me my identity" — bring vitals back with it,
				// so re-arm the one-shot the apply below consumes.
				loadoutVitalsRestored = false;
				BeginAppearanceSwitch();
				ApplySlot(ActiveSlot);
			} else {
				BeginAppearanceSwitch();
				ReturnToSourceProtagonist(spoofedIdentity);
			}
		}

		// Open the switching window: grey the toggle (OnTick) until the swap fully settles. The
		// blocking SET_PLAYER_MODEL has already returned by the time we get here; the window covers
		// the FOLLOWING ticks where the new ped is still settling (and the spoof may be re-engaging),
		// which is when a re-press used to thrash the swap.
		//
		// Re-arm the settle gate (AutoApplyDone + the spoof settle counter) so SwitchFullySettled
		// measures THIS swap, not the previous one. Without this the gate reads the stale AutoApplyDone
		// left true by the last settled apply and releases the lock on the very first tick — which let
		// a fast re-press through again. Re-arming forces auto-apply to re-land and the spoof to
		// re-reach intent before the toggle frees up.
		void BeginAppearanceSwitch() {
			appearanceSwitching = true;
			appearanceSwitchTicks = 0;
			AutoApplyDone = false;
			SettleTicks = 0;
			spoofSettleTicks = 0;
		}
		int appearanceSwitchTicks;
		const int AppearanceSwitchTimeoutTicks = 600; // ~10s hard backstop so the toggle can't lock off

		// The appearance switch is FULLY settled — safe to re-enable the master toggle — when the
		// screen is faded in, the auto-apply has landed (AutoApplyDone), AND the spoof has reached its
		// intended state: not wanted, or actually held, or we're disabled and standing on a genuine
		// protagonist (where it correctly can't engage). Deliberately does NOT wait out the 4s reapply
		// COOLDOWN — that exists only to stop the clobber path re-firing on a fresh swap, not as a
		// settle signal; gating the button on it made the toggle feel locked for several seconds. All
		// signals are concrete/honest (no per-frame head-blend native in the steady path).
		bool SwitchFullySettled(Ped player) {
			if (!GTA.UI.Screen.IsFadedIn) return false;
			if (Enabled) {
				if (!AutoApplyDone) return false;
				// Spoof must have reached intent: off, or actually engaged.
				if (spoofEnabled && !EditMode && !spoof.Held) return false;
				return true;
			}
			// Disabled: settled once we're back on a genuine protagonist body (the return swap landed).
			return PlayerIdentity.GenuineProtagonist(player, spoof) != null;
		}

		// Wallet master toggle. Off makes the wallet inert: earning and the shop spend-redirect
		// both key on walletEnabled in OnTick, so flipping the flag stops them. The spoof is NOT
		// touched — it's an independent disguise. With the wallet off the shop still opens and the
		// purchase completes, but the charge can't stick: the shop pokes the SP cash GLOBAL, which
		// the game reverts from the unchanged real stat, so the protagonist's balance never actually
		// moves — spending is effectively free, not redirected (verified in-game).
		void SetWalletEnabled(bool on) {
			if (on == walletEnabled) return;
			walletEnabled = on;
			Config.SetValue("Wallet", "Enabled", walletEnabled);
			Config.Save();
			if (WalletEnabledItem != null) WalletEnabledItem.Checked = walletEnabled;

			if (!on && spoof.Held) {
				Warn("Wallet off", "- purchases go through but the protagonist's balance won't change.");
			}
		}

		// Edit Mode toggle. On: stop defending the look — the OnTick auto-apply and spoof re-engage
		// gates already check !EditMode, so they go quiet; here we also drop any LIVE spoof so its
		// per-tick hash write can't fight an external edit. Off: nothing to do — the gates reopen and
		// the next tick re-applies the active slot and re-engages the spoof (so Save before turning
		// it off if you want to KEEP the edit, or it gets overwritten by the active slot). The spoof
		// INTENT is never touched, so it returns exactly as it was.
		void SetEditMode(bool on) {
			if (on == EditMode) return;
			EditMode = on;
			if (on && spoof.Held) {
				spoof.Stop("edit mode");
				if (SpoofItem != null) SpoofItem.Checked = spoofEnabled;
			}
			if (on) {
				Warn("Edit Mode on", "- change your ped freely, then Save and turn this off.");
			} else {
				Notify("Edit Mode off - defending your active look again.");
			}
			Logger.Log($"Edit Mode {(on ? "on" : "off")}.");
		}

		// Swap the player back to the protagonist we replaced: captured SourceModel first, else the
		// identity we were impersonating (passed in from SetEnabled, captured before the spoof was
		// stopped), else the configured ReturnProtagonist.
		void ReturnToSourceProtagonist(string spoofedIdentity = null) {
			string target = !string.IsNullOrEmpty(SourceModel) ? SourceModel
				: Identity.ModelName(spoofedIdentity) ?? ReturnProtagonist;
			try {
				// force: a spoof/stranded hash can make Model.Hash already read as `target`, which
				// would no-op a non-forced swap and leave the player really freemode. Force a real
				// SET_PLAYER_MODEL so disable always genuinely returns the story character.
				bool ok = PedAppearance.SwitchModel(target, force: true);
				Logger.Log($"Disabled: return to protagonist model={target} -> {(ok ? "OK" : "FAILED")}.");
				if (ok) {
					Notify("Disabled - returned to your story character.");
				} else {
					Fail("Couldn't return to your story character", "- see the log.");
				}
				if (ok) {
					SourceModel = string.Empty;
					Config.SetValue("State", "SourceModel", SourceModel);
					Config.Save();
				}
			} catch (Exception ex) {
				Logger.LogError(ex.ToString());
				Fail("Couldn't return to your story character", "- see the log.");
			}
		}

		void SetActiveSlot(string name) {
			ActiveSlot = name ?? string.Empty;
			Config.SetValue("State", "ActiveSlot", ActiveSlot);
			Config.Save();
		}

		void RenameSlot(string oldName) {
			if (SnapshotInProgress) {
				NotifyBusy();
				return;
			}
			string newName = Game.GetUserInput(WindowTitle.EnterMessage20, oldName, 32);
			if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName.Trim(), oldName, StringComparison.Ordinal)) {
				return;
			}
			newName = newName.Trim();
			if (XmlAppearanceStorage.Exists(newName)) {
				Warn("Name already taken", $"- a slot named \"{newName}\" exists.");
				return;
			}
			if (XmlAppearanceStorage.Rename(oldName, newName)) {
				if (string.Equals(ActiveSlot, oldName, StringComparison.OrdinalIgnoreCase)) {
					SetActiveSlot(newName);
				}
				RebuildSlotsMenu();
				Notify($"Renamed to \"{newName}\"");
			}
		}

		void DeleteSlot(string name) {
			if (SnapshotInProgress) {
				NotifyBusy();
				return;
			}
			XmlAppearanceStorage.Delete(name);
			if (string.Equals(ActiveSlot, name, StringComparison.OrdinalIgnoreCase)) {
				SetActiveSlot(string.Empty);
			}
			RebuildSlotsMenu();
			Notify($"Deleted \"{name}\"");
		}

		// Copy the slot's current saved data into its single backup, overwriting any previous one.
		// Take this before an Overwrite you might regret; recover with Apply from Backup.
		void BackupSlot(string name) {
			if (XmlAppearanceStorage.Backup(name)) {
				RebuildSlotsMenu(); // refresh the active marker — yellow (no backup) -> green (has backup)
				Notify($"Backed up \"{name}\"");
			} else {
				Warn("Nothing to back up", $"- \"{name}\" isn't saved yet.");
			}
		}

		// Apply the slot's backup to the player (does NOT change the saved slot — Overwrite to keep
		// it). No-op with a warning when the slot has no backup.
		void ApplyBackup(string name) {
			if (SnapshotInProgress) {
				NotifyBusy();
				return;
			}
			AppearanceData bak = XmlAppearanceStorage.GetBackup(name);
			if (bak == null) {
				Warn("No backup", $"- \"{name}\" has no backup yet. Use Backup first.");
				return;
			}
			RememberSourceIfProtagonist();
			try {
				bool ok = PedAppearance.Apply(bak);
				Logger.Log($"Apply backup slot=\"{name}\" model={bak.Model} -> {(ok ? "OK" : "FAILED (model switch?)")}");
				if (ok) {
					// The backup is now the worn look the clobber-defense defends (not the active slot's
					// own file), so a different-model backup isn't reverted. The saved slot is untouched.
					WornLook = bak;
					LastAppliedPedHandle = Game.Player?.Character?.Handle ?? 0;
					LastAutoApplyMs = Game.GameTime;
					RestoreLoadout(includeVitals: !loadoutVitalsRestored);
					Notify($"Applied backup of \"{name}\"");
				} else {
					Fail("Couldn't apply backup", "- the model wouldn't switch.");
				}
			} catch (Exception ex) {
				Logger.LogError(ex.ToString());
				Fail("Couldn't apply backup", "- see the log.");
			}
		}

		// === Menu ==========================================================================

		// Main-menu subtitle: version, plus each ENABLED feature's live state — the active slot
		// (blue) while appearance is on, the balance (green) while the wallet is on. A disabled
		// feature drops its segment entirely, so the title mirrors exactly what's active. Only
		// writes when the text changes. KeepNameCasing preserves the lowercase ~b~/~g~ colour
		// codes (LemonUI otherwise upper-cases them and they render plain white).
		string lastSubtitle;
		void RefreshSubtitle() {
			string subtitle = $"~o~{MenuVersion}~s~";
			if (Enabled) {
				string active = string.IsNullOrEmpty(ActiveSlot) ? "no active slot" : ActiveSlot;
				subtitle += $"  ·  ~b~{active}~s~";
			}
			if (walletEnabled) {
				subtitle += $"  ·  ~g~${wallet.Balance}~s~";
			}
			if (subtitle != lastSubtitle) {
				lastSubtitle = subtitle;
				MainMenu.Name = subtitle;
			}

			// Mirror the active slot into the Appearance submenu's header (blue, like the main menu)
			// so the hub shows what's active without dropping back out. Only while it's open, and only
			// on change.
			if (AppearanceMenu.Visible) {
				string active = string.IsNullOrEmpty(ActiveSlot) ? "no active slot" : ActiveSlot;
				string appSubtitle = $"~b~{active}~s~";
				if (appSubtitle != lastAppearanceSubtitle) {
					lastAppearanceSubtitle = appSubtitle;
					AppearanceMenu.Name = appSubtitle;
				}
			}
		}
		string lastAppearanceSubtitle;

		// The spoof checkbox reflects persisted INTENT, not the live hold (which drops
		// transiently on a model change and re-engages on its own).
		void SyncSpoofItem() {
			if (SpoofItem.Checked != spoofEnabled) {
				SpoofItem.Checked = spoofEnabled;
			}
		}

		// Grey out the Spoofing items while the player is a GENUINE protagonist — engaging
		// there is blocked (would hijack real money). PlayerIdentity sees through our own spoof
		// from the live body, so a spoofed freemode ped is never mistaken for a real protagonist.
		//
		// Recomputed ONLY when the player ped/model changes, not every visible frame. The
		// genuine-vs-freemode tell is the live head blend (GET_PED_HEAD_BLEND_DATA), and on Legacy
		// that native returns mix values for a settled protagonist that drift in and out of the
		// in-range check frame-to-frame — polling it per frame flipped the items' Enabled state and
		// the menu flickered. A protagonist↔freemode change always moves the ped handle or model
		// hash, so keying off those catches every real transition while killing the per-frame churn.
		int spoofAvailPed = -1;
		int spoofAvailModel;
		void RefreshSpoofAvailability() {
			Ped ped = Game.Player?.Character;
			int pedHandle = ped?.Handle ?? 0;
			int modelHash = ped?.Model.Hash ?? 0;
			if (pedHandle == spoofAvailPed && modelHash == spoofAvailModel) return;
			spoofAvailPed = pedHandle;
			spoofAvailModel = modelHash;

			string genuine = PlayerIdentity.GenuineProtagonist(ped, spoof);
			bool available = genuine == null;
			if (SpoofMenuItem.Enabled != available) SpoofMenuItem.Enabled = available;
			if (SpoofItem.Enabled != available) SpoofItem.Enabled = available;
			if (TargetItem.Enabled != available) TargetItem.Enabled = available;
			SpoofMenuItem.Description = genuine != null
				? $"Unavailable as a real {genuine} - switch to a freemode character."
				: "Disguise as a protagonist so shops open.";
		}

		bool AnyMenuVisible() =>
			MainMenu.Visible || AppearanceMenu.Visible || SlotsMenu.Visible
			|| ManualMenu.Visible || WalletMenu.Visible || SpoofMenu.Visible
			|| LoadoutMenu.Visible || DebugMenu.Visible;

		void OnKeyDown(object sender, KeyEventArgs e) {
			// Match the key + its Shift/Ctrl/Alt modifiers, but MASK OFF other flags Windows
			// may report (Caps/Num-Lock) which a strict KeyData != MenuKey would let silently
			// swallow the press.
			Keys pressed = (e.KeyData & Keys.KeyCode) | (e.Modifiers & (Keys.Shift | Keys.Control | Keys.Alt));
			if (pressed != menuKey) {
				return;
			}
			// Toggle the whole UI: close whatever's open; only open the main menu when nothing
			// is showing (otherwise a submenu being open would re-open MainMenu on top of it).
			if (AnyMenuVisible()) {
				MainMenu.Visible = false;
				AppearanceMenu.Visible = false;
				SlotsMenu.Visible = false;
				ManualMenu.Visible = false;
				WalletMenu.Visible = false;
				SpoofMenu.Visible = false;
				LoadoutMenu.Visible = false;
				DebugMenu.Visible = false;
			} else {
				MainMenu.Visible = true;
			}
		}

		void MenuInit() {
			MainMenu = new NativeMenu("Freemode Identity", $"~o~{MenuVersion}~s~");
			MainMenu.KeepNameCasing = true;
			Pool.Add(MainMenu);

			EnabledItem = new NativeCheckboxItem("Appearance Enabled",
				"Turn on to wear your saved look, or off to go back to your story character.",
				Enabled);
			EnabledItem.CheckboxChanged += (s, a) => SetEnabled(EnabledItem.Checked);
			MainMenu.Add(EnabledItem);

			WalletEnabledItem = new NativeCheckboxItem("Wallet Enabled",
				"Turn off to stop earning from pickups and routing shop charges to your wallet.",
				walletEnabled);
			WalletEnabledItem.CheckboxChanged += (s, a) => SetWalletEnabled(WalletEnabledItem.Checked);
			MainMenu.Add(WalletEnabledItem);

			LoadoutEnabledItem = new NativeCheckboxItem("Loadout Enabled",
				"Periodically saves your carried weapons, armor and health, and restores them when your look is applied.",
				loadoutEnabled);
			LoadoutEnabledItem.CheckboxChanged += (s, a) => SetLoadoutEnabled(LoadoutEnabledItem.Checked);
			MainMenu.Add(LoadoutEnabledItem);

			SpoofItem = new NativeCheckboxItem("Spoofing Enabled",
				"~y~Required for a fully working wallet.~s~ Reads you as a protagonist so shops open, jobs pay out, and charges route to your wallet. Off = shops stay closed, and spending draws the protagonist's cash without changing their real balance.",
				spoofEnabled);
			SpoofItem.CheckboxChanged += (s, a) => SetSpoofEnabled(SpoofItem.Checked);
			MainMenu.Add(SpoofItem);

			BuildAppearanceMenu();
			BuildWalletMenu();
			BuildLoadoutMenu();
			BuildSpoofMenu();
			BuildDebugMenu();

			RebuildSlotsMenu();
		}

		void BuildAppearanceMenu() {
			AppearanceMenu = new NativeMenu("Appearance", "Appearance");
			AppearanceMenu.KeepNameCasing = true; // see RefreshSubtitle
			Pool.Add(AppearanceMenu);
			AppearanceMenuItem = MainMenu.AddSubMenu(AppearanceMenu);
			AppearanceMenuItem.Description = "Save, apply and auto-apply your freemode look.";

			SnapshotItem = new NativeItem("Save to New Slot",
				"Snapshot the current freemode character into a new slot.");
			OverwriteActiveItem = new NativeItem("Overwrite Active Slot",
				"Re-snapshot the current look into the active slot.");
			ApplyActiveItem = new NativeItem("Apply Active Slot",
				"Re-apply the active slot, discarding live edits.");

			SlotsMenu = new NativeMenu("Saved Appearances", "Saved Appearances");
			SlotsMenu.KeepNameCasing = true; // preserve the active-row marker's colour codes (see the marker constants)
			Pool.Add(SlotsMenu);

			EditModeItem = new NativeCheckboxItem("Edit Mode",
				"Turn on to stop the mod re-applying your look and spoof so tools like Menyoo can "
				+ "change your ped freely. Save when you are done, then turn this off.",
				EditMode);

			// Gallery-first: Saved Appearances (the hub - set active, apply, back up) on top, then the
			// one-shot actions, then the capture/edit toggles.
			SlotsMenuItem = AppearanceMenu.AddSubMenu(SlotsMenu);
			SlotsMenuItem.Description = "Apply, set active, overwrite, back up, rename or delete a saved appearance.";
			AppearanceMenu.Add(SnapshotItem);
			AppearanceMenu.Add(OverwriteActiveItem);
			AppearanceMenu.Add(ApplyActiveItem);
			BuildManualMenu(AppearanceMenu);
			AppearanceMenu.Add(EditModeItem);

			SnapshotItem.Activated += (s, a) => SnapshotToNewSlot();
			OverwriteActiveItem.Activated += (s, a) => OverwriteActiveSlot();
			ApplyActiveItem.Activated += (s, a) => ApplyActiveSlot();
			SlotsMenu.ItemActivated += OnSlotItemActivated;
			EditModeItem.CheckboxChanged += (s, a) => SetEditMode(EditModeItem.Checked);
		}

		void BuildWalletMenu() {
			WalletMenu = new NativeMenu("Wallet", "Wallet");
			Pool.Add(WalletMenu);
			WalletMenuItem = MainMenu.AddSubMenu(WalletMenu);
			WalletMenuItem.Description = "Earn from pickups and route shop charges here while spoofing.";

			EarningItem = new NativeCheckboxItem("Pickups Enabled",
				"Credit the wallet the real value of collected cash pickups.",
				earningEnabled);
			EarningItem.CheckboxChanged += (s, a) => {
				earningEnabled = EarningItem.Checked;
				Config.SetValue("Wallet", "Earning", earningEnabled);
				Config.Save();
			};
			WalletMenu.Add(EarningItem);
		}

		// Spoofing master toggle (top-level checkbox). On reads the player as a protagonist so
		// shops open; off releases the disguise. Refuses on a GENUINE protagonist (would redirect
		// real story cash) and reverts the checkbox so we never persist an intent we can't honour.
		// Other Start misses are transient (ped not ready) and left to the OnTick auto-retry.
		void SetSpoofEnabled(bool on) {
			if (on) {
				string current = PlayerIdentity.GenuineProtagonist(Game.Player?.Character, spoof);
				if (current != null) {
					Warn($"Can't spoof as {current}", "- switch to a freemode character first.");
					SpoofItem.Checked = false;
					return;
				}
				spoofEnabled = true;
				if (spoof.Start(spoofTarget)) {
					PersistSpoofSource(spoof.OriginalHash);
				}
			} else {
				spoofEnabled = false;
				spoof.Stop("menu");
				PersistSpoofSource(0);
			}
			Config.SetValue("Spoof", "Enabled", spoofEnabled);
			Config.Save();
		}

		void BuildSpoofMenu() {
			SpoofMenu = new NativeMenu("Spoofing", "Spoofing");
			Pool.Add(SpoofMenu);
			SpoofMenuItem = MainMenu.AddSubMenu(SpoofMenu);
			SpoofMenuItem.Description = "Disguise as a protagonist so shops open.";

			TargetItem = new NativeListItem<string>("Target", Identity.All);
			TargetItem.Description = "Which protagonist to impersonate while spoofing.";
			TargetItem.SelectedIndex = Math.Max(0, Array.IndexOf(Identity.All, spoofTarget));
			TargetItem.ItemChanged += (s, a) => {
				spoofTarget = TargetItem.SelectedItem;
				Config.SetValue("Spoof", "Target", spoofTarget);
				Config.Save();
				if (spoof.Held) {
					spoof.Stop("retarget");
					spoof.Start(spoofTarget);
				}
			};
			SpoofMenu.Add(TargetItem);
		}

		// Loadout master toggle. Off makes the whole subsystem inert: OnTick's sampler and every
		// restore key on loadoutEnabled, so flipping it stops both capture and replay. Doesn't touch
		// the stored file — turning it back on resumes from the last snapshot.
		void SetLoadoutEnabled(bool on) {
			if (on == loadoutEnabled) return;
			loadoutEnabled = on;
			Config.SetValue("Loadout", "Enabled", loadoutEnabled);
			Config.Save();
			if (LoadoutEnabledItem != null) LoadoutEnabledItem.Checked = loadoutEnabled;
		}

		void BuildLoadoutMenu() {
			LoadoutMenu = new NativeMenu("Loadout", "Loadout");
			Pool.Add(LoadoutMenu);
			LoadoutMenuItem = MainMenu.AddSubMenu(LoadoutMenu);
			LoadoutMenuItem.Description = "Choose what to keep (weapons, armor, health) and how often to save it. The master switch is on the main menu.";

			LoadoutWeaponsItem = new NativeCheckboxItem("Weapons",
				"Keep your guns, ammo, attachments and tints. Restored whenever your look is re-applied.",
				loadoutWeapons);
			LoadoutWeaponsItem.CheckboxChanged += (s, a) => {
				loadoutWeapons = LoadoutWeaponsItem.Checked;
				Config.SetValue("Loadout", "Weapons", loadoutWeapons);
				Config.Save();
			};
			LoadoutMenu.Add(LoadoutWeaponsItem);

			LoadoutArmorItem = new NativeCheckboxItem("Armor",
				"Keep your body armor. Restored on load and when you enable your look, not after a respawn.",
				loadoutArmor);
			LoadoutArmorItem.CheckboxChanged += (s, a) => {
				loadoutArmor = LoadoutArmorItem.Checked;
				Config.SetValue("Loadout", "Armor", loadoutArmor);
				Config.Save();
			};
			LoadoutMenu.Add(LoadoutArmorItem);

			LoadoutHealthItem = new NativeCheckboxItem("Health",
				"Keep your health. Restored on load and when you enable your look, not after a respawn.",
				loadoutHealth);
			LoadoutHealthItem.CheckboxChanged += (s, a) => {
				loadoutHealth = LoadoutHealthItem.Checked;
				Config.SetValue("Loadout", "Health", loadoutHealth);
				Config.Save();
			};
			LoadoutMenu.Add(LoadoutHealthItem);

			LoadoutPeriodItem = new NativeListItem<string>("Save Period", LoadoutPeriodLabels);
			LoadoutPeriodItem.Description = "How often the carried state is saved.";
			LoadoutPeriodItem.SelectedIndex = Math.Max(0, Array.IndexOf(LoadoutPeriodsMs, loadoutSavePeriodMs));
			LoadoutPeriodItem.ItemChanged += (s, a) => {
				loadoutSavePeriodMs = LoadoutPeriodsMs[LoadoutPeriodItem.SelectedIndex];
				Config.SetValue("Loadout", "SavePeriodSeconds", loadoutSavePeriodMs / 1000);
				Config.Save();
			};
			LoadoutMenu.Add(LoadoutPeriodItem);
		}

		// Models the Force Model escape hatch can swap to, label -> model name. Order matches
		// the list item below.
		static readonly string[] ForceModelLabels = { "Freemode Female", "Freemode Male", "Michael", "Franklin", "Trevor" };
		static readonly string[] ForceModelNames = { PedAppearance.FemaleModel, PedAppearance.MaleModel, "player_zero", "player_one", "player_two" };

		void BuildDebugMenu() {
			DebugMenu = new NativeMenu("Debug", "Debug");
			Pool.Add(DebugMenu);
			NativeItem anchor = MainMenu.AddSubMenu(DebugMenu);
			anchor.Description = "Log level, live identity read-outs, and a force-model escape hatch.";

			// --- Interactive controls (top) ---
			// Log level — write-through to the ini + Logger so verbosity changes need no rebuild.
			// Pass the options as an explicit array, NOT params: the params overload mis-binds the
			// first string as the description, yielding a short list that then throws "index over
			// the limit" when SelectedIndex is set — which silently fails the whole script load.
			string[] logLevels = { nameof(LogLevel.Info), nameof(LogLevel.Debug), nameof(LogLevel.Error) };
			LogLevelItem = new NativeListItem<string>("Log Level", logLevels);
			LogLevelItem.Description = "Log verbosity (C# + shim trace). Info by default.";
			LogLevelItem.SelectedIndex = Math.Max(0, Array.IndexOf(logLevels, logLevel.ToString()));
			LogLevelItem.ItemChanged += (s, a) => {
				logLevel = ParseLogLevel(LogLevelItem.SelectedItem);
				Logger.Threshold = logLevel;
				Config.SetValue("General", "LogLevel", logLevel.ToString());
				Config.Save();
			};
			DebugMenu.Add(LogLevelItem);

			// Force Model — an escape hatch for when another mod leaves you stuck reading as the
			// wrong character: scroll to a model and press Enter to genuinely become it (a real
			// SET_PLAYER_MODEL that bypasses the spoof's hash overlay). Activated fires on Enter.
			NativeListItem<string> forceItem = new NativeListItem<string>("Force Model", ForceModelLabels);
			forceItem.Description = "Press Enter to forcibly become the selected model.";
			forceItem.Activated += (s, a) => ForceModel(ForceModelNames[forceItem.SelectedIndex]);
			DebugMenu.Add(forceItem);

			// --- Read-only live status rows (bottom; text refreshed each tick by RefreshDebugMenu) ---
			DbgSeenAsItem = AddInfoRow("Game sees you as", "Which identity your model reads as now.");
			DbgBaseModelItem = AddInfoRow("Base / real model", "Your real model under any spoof.");
			DbgSpoofItem = AddInfoRow("Spoof", "Live hold now, and who we'll auto-spoof as (want).");
			DbgSourceItem = AddInfoRow("Disable returns to", "Who appearance Disable swaps you back to.");
			DbgShimItem = AddInfoRow("Spend shim", "Native .asi link - redirects shop charges and stat payouts to the wallet.");
		}

		// A non-selectable status row whose Title carries a "Label: value" the tick loop updates.
		NativeItem AddInfoRow(string label, string description) {
			var item = new NativeItem(label, description) { Enabled = false };
			DebugMenu.Add(item);
			return item;
		}

		// Friendly name for a model hash: the two freemode models + three protagonists, else the
		// raw hex. Used by the live read-outs so a hash is human-readable.
		static string DescribeModel(uint hash) {
			int h = unchecked((int)hash);
			if (h == new Model(PedAppearance.FemaleModel).Hash) return "Freemode Female";
			if (h == new Model(PedAppearance.MaleModel).Hash) return "Freemode Male";
			if (h == new Model("player_zero").Hash) return "Michael";
			if (h == new Model("player_one").Hash) return "Franklin";
			if (h == new Model("player_two").Hash) return "Trevor";
			return $"unknown ({hash:X8})";
		}

		// Friendly name for a stored MODEL NAME (e.g. "player_one" -> "Franklin"); empty -> "-".
		static string DescribeModelName(string model) =>
			string.IsNullOrEmpty(model) ? "-" : DescribeModel(Joaat.Hash(model));

		// Update the Debug submenu's live read-out rows. Cheap reads only; runs solely while the
		// Debug menu is open.
		void RefreshDebugMenu() {
			string seenAs = Identity.Current() ?? "freemode";
			DbgSeenAsItem.Title = $"Game sees you as: {seenAs}";

			// Base = the real model under any spoof, read from the live body (see PlayerIdentity).
			uint baseHash = unchecked((uint)RealPlayerModelHash());
			DbgBaseModelItem.Title = baseHash != 0 ? $"Base / real model: {DescribeModel(baseHash)}" : "Base / real model: -";

			// "want" = the standing intent (spoofEnabled): which protagonist we'll auto-reengage as
			// the moment we're back on a freemode ped — distinct from whether the hold is live now.
			string want = spoofEnabled ? spoofTarget : "none";
			DbgSpoofItem.Title = spoof.Held
				? $"Spoof: holding {spoof.Target} (want: {want})"
				: $"Spoof: off (want: {want})";

			DbgSourceItem.Title = string.IsNullOrEmpty(SourceModel)
				? $"Disable returns to: {DescribeModelName(ReturnProtagonist)} (fallback)"
				: $"Disable returns to: {DescribeModelName(SourceModel)}";

			DbgShimItem.Title = $"Spend shim: {(shim.Available ? "connected" : "not loaded")}";
		}

		// Escape hatch: forcibly become a real model. Releases any held spoof first (its per-tick
		// re-assert would otherwise re-paint a hash over the new ped), then does a forced real
		// swap so it lands even when Model.Hash already reads as the target (stranded/spoofed hash).
		void ForceModel(string model) {
			if (spoof.Held) {
				spoof.Stop("force model");
			}
			try {
				bool ok = PedAppearance.SwitchModel(model, force: true);
				Logger.Log($"Force model -> {model}: {(ok ? "OK" : "FAILED")}.");
				if (ok) {
					Notify($"Forced model to {DescribeModelName(model)}.");
				} else {
					Fail("Force model failed", "- see the log.");
				}
			} catch (Exception ex) {
				Logger.LogError($"Force model: {ex}");
				Fail("Force model failed", "- see the log.");
			}
		}

		void BuildManualMenu(NativeMenu parent) {
			ManualMenu = new NativeMenu("Manual Save", "Manual Save");
			Pool.Add(ManualMenu);
			ManualMenuItem = parent.AddSubMenu(ManualMenu);
			ManualMenuItem.Description = "Which features a manual Save to New Slot / Overwrite captures.";

			// Ordered light-first, the one heavier option last. Moving Style and Tattoos are both a
			// quick read now (the decoration base resolves by a single pattern scan, no sweep); Mood
			// still does a brief memory scan, so it sits at the bottom.
			ManualMovingStyleItem = new NativeCheckboxItem("Save Moving Style",
				"Walk clipset. ~g~Light~s~ - a quick read.", ManualMovingStyle);
			ManualTattoosItem = new NativeCheckboxItem("Save Tattoos",
				"Tattoos/decals. ~g~Light~s~ - a quick read.", ManualTattoos);
			ManualMoodItem = new NativeCheckboxItem("Save Mood",
				"Facial mood. ~y~Medium~s~ - a brief memory scan.", ManualMood);

			ManualMenu.Add(ManualMovingStyleItem);
			ManualMenu.Add(ManualTattoosItem);
			ManualMenu.Add(ManualMoodItem);

			ManualTattoosItem.CheckboxChanged += (s, a) => {
				ManualTattoos = ManualTattoosItem.Checked;
				Config.SetValue("ManualSave", "Tattoos", ManualTattoos);
				Config.Save();
			};
			ManualMoodItem.CheckboxChanged += (s, a) => {
				ManualMood = ManualMoodItem.Checked;
				Config.SetValue("ManualSave", "Mood", ManualMood);
				Config.Save();
			};
			ManualMovingStyleItem.CheckboxChanged += (s, a) => {
				ManualMovingStyle = ManualMovingStyleItem.Checked;
				Config.SetValue("ManualSave", "MovingStyle", ManualMovingStyle);
				Config.Save();
			};
		}

		void RebuildSlotsMenu() {
			// Clear+re-add resets both the row cursor AND each row's chosen action back to the top /
			// Apply. An in-place rebuild (Apply / Set Active re-marks the active row but keeps the
			// same rows) should leave the player exactly where they were, so snapshot the cursor and
			// the per-slot action before clearing and restore them after.
			int prevIndex = SlotsMenu.SelectedIndex;
			var prevActions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			foreach (NativeItem old in SlotsMenu.Items) {
				if (old is NativeListItem<string> li) {
					prevActions[StripActiveMarker(li.Title)] = li.SelectedIndex;
				}
			}

			SlotsMenu.Clear();
			foreach (AppearanceData ad in XmlAppearanceStorage.GetAll()) {
				bool active = string.Equals(ad.Name, ActiveSlot, StringComparison.OrdinalIgnoreCase);
				// The active row's marker is green when the slot has a backup (the look is recoverable),
				// yellow when it has none. Non-active rows carry no marker.
				bool hasBackup = active && XmlAppearanceStorage.HasBackup(ad.Name);
				string title = active ? (hasBackup ? ActiveMarkerGreen : ActiveMarkerYellow) + ad.Name : ad.Name;
				var item = new NativeListItem<string>(title, SlotActions);
				item.Description = active
					? (hasBackup
						? "Active (has backup) - auto-applied on load. Scroll to pick an action, then press. Delete also removes the backup."
						: "Active (no backup yet) - auto-applied on load. Scroll to pick an action, then press.")
					: "Scroll to pick an action (Apply, Set Active, Overwrite, Backup, Rename, Delete), then press. Delete also removes the backup.";
				// Keyed by slot name so it survives reordering; clamp in case SlotActions changed.
				if (prevActions.TryGetValue(ad.Name, out int act)) {
					item.SelectedIndex = Math.Min(act, SlotActions.Length - 1);
				}
				SlotsMenu.Add(item);
			}
			if (SlotsMenu.Items.Count == 0) {
				var empty = new NativeItem("(no saved appearances)",
					"Use \"Save to New Slot\" on the main menu to create one.");
				empty.Enabled = false;
				SlotsMenu.Add(empty);
			}
			// Restore within range (a Delete shrinks the list, so clamp to the last row).
			if (prevIndex > 0 && SlotsMenu.Items.Count > 0) {
				SlotsMenu.SelectedIndex = Math.Min(prevIndex, SlotsMenu.Items.Count - 1);
			}
		}

		void OnSlotItemActivated(object sender, ItemActivatedArgs e) {
			if (!(e.Item is NativeListItem<string> item)) {
				return;
			}
			string name = StripActiveMarker(item.Title);
			switch (item.SelectedItem) {
				case "Apply":
					ApplySlot(name);
					SetActiveSlot(name);
					RebuildSlotsMenu();
					break;
				case "Set Active":
					SetActiveSlot(name);
					RebuildSlotsMenu();
					Notify($"\"{name}\" is now the active slot");
					break;
				case "Overwrite":
					OverwriteSlot(name);
					break;
				case "Backup":
					BackupSlot(name);
					break;
				case "Apply from Backup":
					ApplyBackup(name);
					break;
				case "Rename":
					RenameSlot(name);
					break;
				case "Delete":
					DeleteSlot(name);
					break;
			}
		}
	}
}
