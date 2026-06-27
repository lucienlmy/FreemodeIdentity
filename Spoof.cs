using System;
using GTA;

namespace FreemodeIdentity {
	// Identity spoof: makes a FREEMODE-bodied player ped read as a story protagonist to the
	// game's systems (shops, wallet name/HUD) while the body keeps rendering freemode. This
	// is what lets a freemode character walk into a shop and have the purchase resolve to a
	// protagonist's SPx stat (which the native shim then redirects to the freemode wallet).
	//
	// Two levers, held together each tick and restored together on release/abort/model-change:
	//   1. archetype model-hash: ped+0x20 (archetype) +0x18 (hash) <- protagonist joaat.
	//      Makes the game treat the ped as that protagonist (wallet/name/abilities).
	//   2. active-character index global 0x547B <- 0/1/2. Drives the pause-menu name/portrait.
	//
	// NOTE: lever 2 does NOT make pickups credit the protagonist (verified in-game — a
	// freemode char's earned cash still vanishes), so EARNING is handled separately by reading
	// the pickup value directly. The spoof is purely for SPENDING access + identity display.
	//
	// CAVEAT: archetype+0x18 is on the SHARED model info — every freemode ped of this model
	// reads as the protagonist while held. The hold auto-restores on abort/model change so it
	// can never leak into a teardown. g0x547B is savegame-backed, so it is ALWAYS restored.
	internal sealed class Spoof {
		const int ArchetypeOffset = 0x20;
		const int HashOffset = 0x18;
		const int CharIndexGlobal = 0x547B; // SP active-character index (g21627)

		[System.Runtime.InteropServices.DllImport("ScriptHookV.dll", ExactSpelling = true, EntryPoint = "?getGlobalPtr@@YAPEA_KH@Z")]
		static extern IntPtr GetGlobalPtr(int index);

		public bool Held { get; private set; }
		public string Target { get; private set; } // protagonist identity being held

		// The real model-info hash the ped had BEFORE we overwrote it (captured at engage). FW
		// persists this so a reload-while-spoofed can restore the freemode hash it can no longer
		// read off the (spoofed) ped. 0 when not held / never engaged.
		public uint OriginalHash => Held ? originalHash : 0u;

		IntPtr heldHashAddr = IntPtr.Zero;
		uint originalHash;
		uint spoofHash;
		int heldPedHandle; // the ped we engaged on; a change means the ped was replaced (swap/respawn)

		IntPtr heldIndexAddr = IntPtr.Zero;
		int originalIndex;
		int spoofIndex;

		// Recover from a stranded spoof left by a script reload. When the previous instance was
		// torn down (scripts reloaded) WHILE the spoof was held, our hash write stays on the ped's
		// archetype but the live hold (and originalHash) is gone — the ped now reads as the target
		// protagonist with nothing holding it. The new instance then misreads it as a GENUINE
		// protagonist and locks spoofing out, with no way to recover. Given the real freemode model
		// we persisted at engage time, this overwrites the stranded hash back to that real model so
		// the world reads freemode again; the normal auto-reengage then takes over cleanly. Returns
		// true if it actually rewrote a stranded hash. Safe no-op if the ped isn't the strand case.
		public bool RecoverStranded(uint realModelHash) {
			if (Held || realModelHash == 0) return false;
			// Only ever restore a real FREEMODE hash. A persisted source that isn't freemode is itself
			// corrupt (the bug that wrote a garbage "real model" like 705E61F2) — writing it back onto
			// the shared model-info would poison it, making even a genuine protagonist read wrong.
			if (!PedAppearance.IsFreemodeHash(unchecked((int)realModelHash))) {
				Logger.LogError($"Spoof: persisted source {realModelHash:X8} is not a freemode model — refusing stranded restore.");
				return false;
			}
			Ped ped = Game.Player?.Character;
			if (ped == null || !ped.Exists() || ped.MemoryAddress == IntPtr.Zero) return false;
			// The BODY must actually be a freemode ped, not a genuine story protagonist. On a full game
			// restart the savegame loads as a real protagonist (e.g. Franklin) while spoofSourceHash still
			// names last session's freemode model — so the hash differs and looks "stranded", but writing
			// freemode onto a REAL protagonist's shared model-info corrupts it (the model then can't be
			// re-requested, so even a forced SET_PLAYER_MODEL no-ops and the protagonist body sticks). A
			// freemode body has a valid head blend; a protagonist has none — the hash-independent tell.
			if (!PedAppearance.HasFreemodeBody(ped)) {
				Logger.Log("Spoof: player body isn't a freemode ped (genuine protagonist on load) — skipping stranded restore.");
				return false;
			}
			IntPtr archetype = MemScan.SafeReadPtr(ped.MemoryAddress + ArchetypeOffset);
			if (archetype == IntPtr.Zero) return false;
			IntPtr hashAddr = archetype + HashOffset;
			uint memHash = MemScan.ReadUInt32(hashAddr);
			// Only act when the ped is wearing a DIFFERENT hash than its real model — i.e. a strand.
			// If it already reads as the real model there's nothing stranded to undo.
			if (memHash == realModelHash) return false;
			if (!MemScan.WriteUInt32(hashAddr, realModelHash)) {
				Logger.LogError("Spoof: stranded-hash restore failed — page not writable.");
				return false;
			}
			Logger.Log($"Spoof: restored stranded hash {memHash:X8} -> real model {realModelHash:X8} after reload.");
			return true;
		}

		// Engage the spoof, making the player read as `identity` (a protagonist). Returns
		// false (and changes nothing) if it can't take hold — already a protagonist, no ped,
		// unwritable memory. Re-entrant safe: a second call while held is a no-op.
		public bool Start(string identity) {
			if (Held) return true;
			int charIdx = Identity.CharIndex(identity);
			uint targetHash = Joaat.Hash(Identity.ModelName(identity) ?? "");
			if (charIdx < 0 || targetHash == 0) {
				Logger.LogError($"Spoof: unknown identity '{identity}'.");
				return false;
			}

			Ped ped = Game.Player.Character;
			if (ped == null || !ped.Exists() || ped.MemoryAddress == IntPtr.Zero) {
				Logger.LogError("Spoof: no player ped — can't engage.");
				return false;
			}
			IntPtr archetype = MemScan.SafeReadPtr(ped.MemoryAddress + ArchetypeOffset);
			if (archetype == IntPtr.Zero) {
				Logger.LogError("Spoof: archetype unreadable — abort.");
				return false;
			}
			IntPtr hashAddr = archetype + HashOffset;
			uint memHash = MemScan.ReadUInt32(hashAddr);
			if (unchecked((int)memHash) != ped.Model.Hash) {
				Logger.LogError($"Spoof: archetype hash mismatch (mem={memHash:X8} api={ped.Model.Hash:X8}) — not engaging.");
				return false;
			}
			// The slot must currently hold a real FREEMODE hash. If it doesn't, it's already stranded
			// (a prior spoof's hash left on the shared model-info, e.g. after a hard reload) — engaging
			// would capture that garbage as originalHash and later restore it back, corrupting the
			// shared freemode model-info so even a genuine protagonist reads wrong. Refuse instead.
			if (!PedAppearance.IsFreemodeHash(unchecked((int)memHash))) {
				Logger.LogError($"Spoof: archetype hash {memHash:X8} is not a freemode model — slot looks stranded, not engaging.");
				return false;
			}
			// SAFETY: never spoof while the player IS a genuine story protagonist. A real
			// protagonist reads identically to a spoofed one, so engaging here would redirect
			// THEIR real cash stat to the wallet (hijacking story money). Spoofing only makes
			// sense from a freemode ped; this guards the manual toggle (the OnTick auto-reengage
			// is separately gated on Identity.Current()==null).
			string currentIdentity = Identity.Current();
			if (currentIdentity != null) {
				Logger.Log($"Spoof: player is protagonist {currentIdentity} — not engaging (would hijack real money).");
				return false;
			}

			if (!MemScan.WriteUInt32(hashAddr, targetHash)) {
				Logger.LogError("Spoof: hash write failed — page not writable.");
				return false;
			}
			originalHash = memHash;
			spoofHash = targetHash;
			heldHashAddr = hashAddr;
			heldPedHandle = ped.Handle;

			// Lever 2: the active-character index global. Best-effort — hold the hash even if
			// this one can't be written.
			heldIndexAddr = GetGlobalPtr(CharIndexGlobal);
			if (heldIndexAddr != IntPtr.Zero && MemScan.IsReadable(heldIndexAddr, 4)) {
				originalIndex = MemScan.ReadInt32(heldIndexAddr);
				spoofIndex = charIdx;
				MemScan.WriteUInt32(heldIndexAddr, unchecked((uint)spoofIndex));
			} else {
				heldIndexAddr = IntPtr.Zero;
			}

			Held = true;
			Target = identity;
			Logger.Log($"Spoof: engaged as {identity} (hash {spoofHash:X8}, charIdx {charIdx}).");
			return true;
		}

		// Release the spoof, restoring both levers. Safe to call when not held.
		public void Stop(string why = "stop") {
			Restore(why);
		}

		// Re-assert each tick so a game rewrite can't silently drop the spoof, and so we
		// auto-release if the held ped/archetype changed under us (model swap, respawn).
		public void Tick() {
			if (!Held) return;
			try {
				Ped ped = Game.Player.Character;
				if (ped == null || !ped.Exists() || ped.MemoryAddress == IntPtr.Zero) {
					Restore("ped gone");
					return;
				}
				// The player ped was replaced under us (a story character switch / respawn / our own
				// re-apply gives a new handle). Release via Restore: heldHashAddr is the SHARED
				// freemode model-info slot we poisoned, so restoring writes the freemode hash back
				// THERE — it never touches the new ped's own archetype, and the spoofHash guard in
				// Restore skips the write if a genuinely different model already sits at that slot.
				// Skipping the restore here was the re-apply loop: our SET_PLAYER_MODEL reloads the
				// same freemode model whose shared archetype stayed poisoned, so Model.Hash kept
				// reading the protagonist hash and the clobber check re-fired forever.
				if (ped.Handle != heldPedHandle) {
					Restore("ped replaced under hold");
					return;
				}
				IntPtr archetype = MemScan.SafeReadPtr(ped.MemoryAddress + ArchetypeOffset);
				if (archetype + HashOffset != heldHashAddr) {
					// The archetype slot moved (model swap). Same reasoning: Restore targets the old
					// poisoned slot and is gated on it still holding our spoof value.
					Restore("archetype changed under hold");
					return;
				}
				if (MemScan.ReadUInt32(heldHashAddr) != spoofHash) {
					MemScan.WriteUInt32(heldHashAddr, spoofHash);
				}
				if (heldIndexAddr != IntPtr.Zero && MemScan.IsReadable(heldIndexAddr, 4)) {
					if (MemScan.ReadInt32(heldIndexAddr) != spoofIndex) {
						MemScan.WriteUInt32(heldIndexAddr, unchecked((uint)spoofIndex));
					}
				}
			} catch (Exception ex) {
				Logger.LogError($"Spoof: Tick exception {ex.GetType().Name}: {ex.Message}");
				Restore("tick exception");
			}
		}

		void Restore(string why) {
			if (heldHashAddr != IntPtr.Zero && originalHash != 0) {
				// Only restore if the page still holds OUR spoof value (don't clobber a new model).
				if (MemScan.ReadUInt32(heldHashAddr) == spoofHash) {
					MemScan.WriteUInt32(heldHashAddr, originalHash);
				}
			}
			// g0x547B is savegame-backed — always restore so the save can't think the wrong
			// character is active.
			if (heldIndexAddr != IntPtr.Zero && MemScan.IsReadable(heldIndexAddr, 4)) {
				MemScan.WriteUInt32(heldIndexAddr, unchecked((uint)originalIndex));
			}
			if (Held) {
				Logger.Log($"Spoof: released ({why}).");
			}
			Held = false;
			Target = null;
			heldHashAddr = IntPtr.Zero;
			heldIndexAddr = IntPtr.Zero;
		}
	}
}
