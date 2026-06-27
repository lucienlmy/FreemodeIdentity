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

		// Cosmetic prefix marking the active slot's list row: a coloured '>' via GTA colour codes (the
		// SlotsMenu sets KeepNameCasing so the lowercase codes survive). GREEN when the active slot
		// has a backup, YELLOW when it doesn't — a glance shows whether the active look is recoverable.
		// A plain ASCII glyph the menu font actually has — the ● and ★ symbols rendered as a missing-
		// glyph box. Both forms share the trailing "> " so StripActiveMarker recovers the slot name.
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
		// The three story-protagonist models, in character-wheel order (Michael, Franklin, Trevor).
		static readonly string[] Protagonists = { "player_zero", "player_one", "player_two" };

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
		// True while we're waiting out the post-revive sequence before re-applying. The dead→alive
		// edge fires, but the game then runs a walk-out cutscene that MOVES the player with control
		// taken away; our re-apply does a real SET_PLAYER_MODEL (recreates the ped), and doing that
		// mid-cutscene dropped the ped through the ground. So we hold until player control is back
		// (IS_PLAYER_CONTROL_ON via CanControlCharacter) — the reliable "cutscene over" signal — no
		// matter how long it runs. A wall-clock timeout backstops it so the re-apply can't be lost.
		bool ReviveApplyPending;
		int ReviveTimeoutMs = -1;
		const int ReviveTimeoutGraceMs = 15000; // backstop if control never reads on

		bool SnapshotPending;

		// True while a user snapshot is still being written (deferred capture). Apply must
		// wait or it would restore the PREVIOUS save.
		bool SnapshotInProgress =>
			SnapshotPending || MoodMemory.IsRunning || PedHeadBlendMemory.FindRunning || DecorationBaseFinder.IsRunning;

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

		// Spoof settle gate (see AutoSpoofReady). > SettleTarget so appearance lands first.
		const int SpoofSettleTarget = 45;
		int spoofSettleTicks;
		int spoofSettlePed;
		int spoofSettleModel;

		bool redirectLogged; // last-logged redirect state, to edge-trigger the transition log

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
			Logger.LogBanner($"Config: enabled={Enabled} wallet={walletEnabled} earning={earningEnabled} spoof={spoofEnabled} target={spoofTarget} menuKey={menuKey}.");

			XmlAppearanceStorage.Initialize(ScriptPaths.DataDirectory);
			MenuInit();

			Tick += OnTick;
			KeyDown += OnKeyDown;
			Aborted += (s, a) => spoof.Stop("script abort"); // never leave a held spoof on teardown
		}

		// Read every setting once. The ini is grouped by feature, with all runtime state (not
		// user-editable) corralled in [State]:
		//   [General]    MenuKey, LogLevel
		//   [Appearance] Enabled, ReturnProtagonist
		//   [Wallet]     Enabled, Earning
		//   [Spoof]      Enabled, Target
		//   [ManualSave] Tattoos, Mood, MovingStyle
		//   [State]      ActiveSlot, SourceModel, SpoofSourceHash
		void LoadConfig() {
			menuKey = Config.GetValue("General", "MenuKey", Keys.Shift | Keys.X);
			// Default to Debug for the 0.x pre-releases so every reported issue arrives with full
			// triage detail without asking the user to re-run. No path logs per tick, so the file
			// stays small. Flip this back to Info at 1.0. The menu description still says Info — the
			// user-facing "normal" level — on purpose.
			logLevel = ParseLogLevel(Config.GetValue("General", "LogLevel", nameof(LogLevel.Debug)));
			Logger.Threshold = logLevel;

			Enabled = Config.GetValue("Appearance", "Enabled", true);
			ReturnProtagonist = Config.GetValue("Appearance", "ReturnProtagonist", "player_zero");

			walletEnabled = Config.GetValue("Wallet", "Enabled", true);
			earningEnabled = Config.GetValue("Wallet", "Earning", true);

			spoofEnabled = Config.GetValue("Spoof", "Enabled", false);
			spoofTarget = Config.GetValue("Spoof", "Target", Identity.Franklin);
			if (Array.IndexOf(Identity.All, spoofTarget) < 0) {
				spoofTarget = Identity.Franklin;
			}

			ManualTattoos = Config.GetValue("ManualSave", "Tattoos", false);
			ManualMood = Config.GetValue("ManualSave", "Mood", false);
			ManualMovingStyle = Config.GetValue("ManualSave", "MovingStyle", true);

			ActiveSlot = Config.GetValue("State", "ActiveSlot", string.Empty);
			SourceModel = Config.GetValue("State", "SourceModel", string.Empty);
			spoofSourceHash = ParseHashHex(Config.GetValue("State", "SpoofSourceHash", "0"));

			// Write every key back so the ini always reflects the full, current layout (seeds a
			// fresh install, and completes a just-migrated one).
			Config.SetValue("General", "MenuKey", menuKey);
			Config.SetValue("General", "LogLevel", logLevel.ToString());
			Config.SetValue("Appearance", "Enabled", Enabled);
			Config.SetValue("Appearance", "ReturnProtagonist", ReturnProtagonist);
			Config.SetValue("Wallet", "Enabled", walletEnabled);
			Config.SetValue("Wallet", "Earning", earningEnabled);
			Config.SetValue("Spoof", "Enabled", spoofEnabled);
			Config.SetValue("Spoof", "Target", spoofTarget);
			Config.SetValue("ManualSave", "Tattoos", ManualTattoos);
			Config.SetValue("ManualSave", "Mood", ManualMood);
			Config.SetValue("ManualSave", "MovingStyle", ManualMovingStyle);
			Config.SetValue("State", "ActiveSlot", ActiveSlot);
			Config.SetValue("State", "SourceModel", SourceModel);
			Config.SetValue("State", "SpoofSourceHash", spoofSourceHash.ToString("X8"));
			Config.Save();
		}

		static LogLevel ParseLogLevel(string value) =>
			Enum.TryParse(value, true, out LogLevel level) ? level : LogLevel.Info;

		static uint ParseHashHex(string value) =>
			uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out uint h) ? h : 0u;

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
				bool busy = SnapshotInProgress;
				if (AppearanceMenuItem != null) AppearanceMenuItem.Enabled = Enabled;
				if (WalletMenuItem != null) WalletMenuItem.Enabled = walletEnabled;
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
				if (DecorationBaseFinder.IsRunning) { DecorationBaseFinder.Tick(); return; }
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
				WasDead = isDead;

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
							Logger.Log(revived ? "Active look clobbered (revived from death); re-applying once control returns."
								: modelClobbered ? $"Active look clobbered (model now {player.Model.Hash:X8}, expected {active.Model}); re-applying."
								: "Active look clobbered (player ped recreated — respawn); re-applying.");
						}
					}
					if (!AutoApplyDone) {
						// After a revive, hold the re-apply until the player is back IN CONTROL — the
						// reliable end-of-cutscene signal (a backstop timeout releases it regardless so
						// the look can't be lost). A model swap mid-cutscene spawns the ped underground.
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
						bool stable = player != null && GTA.UI.Screen.IsFadedIn && !reviveHeld;
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
				if (spoofEnabled && !EditMode && !spoof.Held && AutoSpoofReady()) {
					Logger.LogDebug($"Auto-spoof gate ready (current={Identity.Current() ?? "freemode"}) — engaging {spoofTarget}.");
					if (spoof.Start(spoofTarget)) {
						PersistSpoofSource(spoof.OriginalHash);
					}
				}
				spoof.Tick();

				// Earning credits only while the wallet is on AND earning is on; it still tracks
				// pickups otherwise so the baseline stays correct.
				earning.Tick(walletEnabled && earningEnabled);

				SyncShim();

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
		// live freemode (non-protagonist) ped, the screen faded in, and the ped + model
		// UNCHANGED for a settle window — the stability check means we only engage AFTER the
		// ped (including our own apply) has stopped changing.
		bool AutoSpoofReady() {
			Ped ped = Game.Player?.Character;
			bool stable = ped != null && ped.Exists() && Identity.Current() == null
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

			shim.Push(redirect, activeStat, wallet.Balance, logLevel <= LogLevel.Debug ? 1 : 0);
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
			return new PedAppearance.CaptureOptions { Tattoos = ManualTattoos, MovingStyle = ManualMovingStyle, Mood = ManualMood };
		}

		// Shared snapshot kickoff. Mood + tattoos can't be read synchronously (the facial task
		// churns; the tattoo array base must be discovered), so they run tick-driven off the hot
		// path; we set SnapshotPending and the tick loop runs DoSnapshot once they finish.
		// The player's REAL freemode model hash, seeing through a live spoof (which overwrites the
		// archetype hash that ped.Model.Hash reads). 0 if there's no ped. Used by the capture path
		// so a snapshot taken while spoofed sees Freemode, not the impersonated protagonist.
		int RealPlayerModelHash() {
			if (spoof.Held && spoof.OriginalHash != 0) return unchecked((int)spoof.OriginalHash);
			Ped ped = Game.Player?.Character;
			return ped != null ? ped.Model.Hash : 0;
		}

		void BeginSnapshot(Ped player, string slotName, PedAppearance.CaptureOptions opts, bool makeActive = false) {
			if (PedAppearance.IsFreemodeHash(RealPlayerModelHash())) {
				if (opts.Mood) {
					MoodMemory.Begin(player);
				} else {
					MoodMemory.Disable();
				}
				PedHeadBlendMemory.BeginFind(player);
				if (opts.Tattoos && !PedDecorationMemory.BaseKnown) {
					DecorationBaseFinder.Begin(player);
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
				bool bodyAlreadyOurs = RealPlayerModelHash() == new Model(ad.Model).Hash;
				bool force = !bodyAlreadyOurs;
				bool ok = PedAppearance.Apply(ad, force, force ? 0 : RealPlayerModelHash());
				Logger.Log($"Reapply worn look model={ad.Model} -> {(ok ? "OK" : "FAILED (model switch?)")}{(force ? " (forced)" : "")} (auto)");
				if (ok) {
					LastAppliedPedHandle = Game.Player?.Character?.Handle ?? 0;
				}
			} catch (Exception ex) {
				Logger.LogError(ex.ToString());
			}
		}

		// Record the live story protagonist as the "original" to return to on Disable. No-ops
		// once captured / while freemode. The spoof check is now spoof.Held IN MEMORY: if our
		// own spoof is engaged the protagonist reading is OURS, not real, so don't capture it.
		void RememberSourceIfProtagonist() {
			if (!string.IsNullOrEmpty(SourceModel)) return;
			Ped ped = Game.Player?.Character;
			if (ped == null) return;
			string match = null;
			foreach (string m in Protagonists) {
				if (ped.Model.Hash == new Model(m).Hash) {
					match = m;
					break;
				}
			}
			if (match == null) return;
			// Reads as a protagonist — but if our spoof is holding, that hash is the SPOOF, not
			// real. (In-process check; no file read.)
			if (spoof.Held) return;
			SourceModel = match;
			Config.SetValue("State", "SourceModel", SourceModel);
			Config.Save();
			Logger.Log($"Captured source protagonist model={SourceModel}.");
		}

		// Appearance master toggle. Enable applies the active freemode look; Disable swaps back
		// to the story protagonist.
		void SetEnabled(bool on) {
			if (on == Enabled) return;

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
				if (EditMode && PedAppearance.IsFreemode(Game.Player?.Character)) {
					Warn("Edit Mode is on", "- your live edit is left alone. Turn Edit Mode off to apply the active slot.");
					return;
				}
				if (string.IsNullOrEmpty(ActiveSlot) || !XmlAppearanceStorage.Exists(ActiveSlot)) {
					Warn("No active slot", "- set one to apply a saved look.");
					return;
				}
				ApplySlot(ActiveSlot);
			} else {
				ReturnToSourceProtagonist(spoofedIdentity);
			}
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
		}

		// The spoof checkbox reflects persisted INTENT, not the live hold (which drops
		// transiently on a model change and re-engages on its own).
		void SyncSpoofItem() {
			if (SpoofItem.Checked != spoofEnabled) {
				SpoofItem.Checked = spoofEnabled;
			}
		}

		// Grey out the Spoofing items while the player is a GENUINE protagonist — engaging
		// there is blocked (would hijack real money). "Genuine" = reads as a protagonist AND
		// our own spoof isn't the cause (spoof.Held in memory).
		void RefreshSpoofAvailability() {
			bool onRealProtagonist = !spoof.Held && Identity.Current() != null;
			bool available = !onRealProtagonist;
			if (SpoofMenuItem.Enabled != available) SpoofMenuItem.Enabled = available;
			if (SpoofItem.Enabled != available) SpoofItem.Enabled = available;
			if (TargetItem.Enabled != available) TargetItem.Enabled = available;
			SpoofMenuItem.Description = onRealProtagonist
				? $"Unavailable as a real {Identity.Current()} - switch to a freemode character."
				: "Disguise as a protagonist so shops open.";
		}

		bool AnyMenuVisible() =>
			MainMenu.Visible || AppearanceMenu.Visible || SlotsMenu.Visible
			|| ManualMenu.Visible || WalletMenu.Visible || SpoofMenu.Visible || DebugMenu.Visible;

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

			SpoofItem = new NativeCheckboxItem("Spoofing Enabled",
				"Read as a protagonist so shops open and jobs pay out. Turn the wallet on to make money actually change - with it off, the protagonist's balance never moves.",
				spoofEnabled);
			SpoofItem.CheckboxChanged += (s, a) => SetSpoofEnabled(SpoofItem.Checked);
			MainMenu.Add(SpoofItem);

			BuildAppearanceMenu();
			BuildWalletMenu();
			BuildSpoofMenu();
			BuildDebugMenu();

			RebuildSlotsMenu();
		}

		void BuildAppearanceMenu() {
			AppearanceMenu = new NativeMenu("Appearance", "Appearance");
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
			// Keep casing so the active-slot row's ~g~●~s~ green-dot marker keeps its lowercase
			// colour codes (LemonUI otherwise upper-cases them and they render as literal text).
			SlotsMenu.KeepNameCasing = true;
			Pool.Add(SlotsMenu);

			EditModeItem = new NativeCheckboxItem("Edit Mode",
				"Turn on to stop the mod re-applying your look and spoof so tools like Menyoo can "
				+ "change your ped freely. Save when you are done, then turn this off.",
				EditMode);

			AppearanceMenu.Add(SnapshotItem);
			AppearanceMenu.Add(OverwriteActiveItem);
			AppearanceMenu.Add(ApplyActiveItem);
			SlotsMenuItem = AppearanceMenu.AddSubMenu(SlotsMenu);
			SlotsMenuItem.Description = "Apply, set active, overwrite, back up, rename or delete a saved appearance.";
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
				string current = Identity.Current();
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

			// Base = the real model under any spoof: its captured original when held, else (no
			// spoof layer) the live model is the real one.
			Ped ped = Game.Player?.Character;
			uint baseHash = spoof.Held ? spoof.OriginalHash : (ped != null ? unchecked((uint)ped.Model.Hash) : 0u);
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

			ManualTattoosItem = new NativeCheckboxItem("Save Tattoos",
				"Tattoos/decals. ~o~Heavy~s~ memory sweep - on only if this character has tattoos.",
				ManualTattoos);
			ManualMoodItem = new NativeCheckboxItem("Save Mood",
				"Facial mood. ~y~Medium~s~ - a brief memory scan.", ManualMood);
			ManualMovingStyleItem = new NativeCheckboxItem("Save Moving Style",
				"Walk clipset. ~g~Light~s~ - a quick read.", ManualMovingStyle);

			ManualMenu.Add(ManualTattoosItem);
			ManualMenu.Add(ManualMoodItem);
			ManualMenu.Add(ManualMovingStyleItem);

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
