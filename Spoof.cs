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
		// SP active-character index global (lever 2). Enhanced: 0x547B (g21627). Legacy has no
		// clean equivalent, so GameBuild returns -1 there and lever 2 is skipped — see GameBuild.
		static int CharIndexGlobal => GameBuild.CharIndexGlobal;

		[System.Runtime.InteropServices.DllImport("ScriptHookV.dll", ExactSpelling = true, EntryPoint = "?getGlobalPtr@@YAPEA_KH@Z")]
		static extern IntPtr GetGlobalPtr(int index);

		public bool Held { get; private set; }
		public string Target { get; private set; } // protagonist identity being held

		// The real model-info hash the ped had BEFORE we overwrote it (captured at engage). FW
		// persists this so a reload-while-spoofed can restore the freemode hash it can no longer
		// read off the (spoofed) ped. 0 when not held / never engaged.
		public uint OriginalHash => Held ? originalHash : 0u;

		// The protagonist model hash we paint while held (0 when not held). The WaypointKeeper needs
		// it to re-key the waypoint entry to match the identity the lookup sees while spoofed.
		public uint SpoofHash => Held ? spoofHash : 0u;

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
			if (!PlayerIdentity.IsFreemodeBody(ped)) {
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
		//
		// realFreemodeHash (0 = none) is the player's TRUE freemode model, used to SELF-HEAL a
		// stranded poison: on Legacy a prior spoof's restore can leave a protagonist hash on the
		// SHARED freemode model-info, so a genuine freemode body reads that protagonist hash. Left
		// unhandled, Start refuses every tick ("not a freemode model") and the auto-reengage spins —
		// a busy-loop that froze the game on enable. When the slot holds a non-freemode hash but the
		// BODY is confirmed freemode and a valid freemode hash is supplied, we rewrite the slot back
		// to freemode first, then engage normally.
		public bool Start(string identity, uint realFreemodeHash = 0) {
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
			// The slot must currently hold a real FREEMODE hash. If it doesn't, it's stranded — a
			// prior spoof's protagonist hash left on the shared model-info (Legacy: the release
			// restore didn't land). Engaging as-is would capture that garbage as originalHash and
			// later restore it, poisoning the shared freemode model-info for good. Two outcomes:
			//   - the BODY is a confirmed freemode ped AND we know the real freemode hash → SELF-HEAL:
			//     rewrite the slot to the real freemode model, then fall through and engage. Without
			//     this the auto-reengage refuses every tick and spins (the on-enable freeze).
			//   - otherwise → refuse (writing freemode onto a genuine protagonist would corrupt it).
			if (!PedAppearance.IsFreemodeHash(unchecked((int)memHash))) {
				bool canHeal = realFreemodeHash != 0
					&& PedAppearance.IsFreemodeHash(unchecked((int)realFreemodeHash))
					&& PlayerIdentity.IsFreemodeBody(ped);
				if (!canHeal) {
					Logger.LogError($"Spoof: archetype hash {memHash:X8} is not a freemode model — slot looks stranded, not engaging.");
					return false;
				}
				if (!MemScan.WriteUInt32(hashAddr, realFreemodeHash)) {
					Logger.LogError("Spoof: stranded-poison heal failed — page not writable.");
					return false;
				}
				Logger.Log($"Spoof: healed stranded poison {memHash:X8} -> freemode {realFreemodeHash:X8} before engage.");
				memHash = realFreemodeHash;
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
			validatedPedHandle = -1; // force Tick's slow-path validation on the first held frame

			// Lever 2: the active-character index global. Best-effort — hold the hash even if
			// this one can't be written. Skipped entirely when the build has no such global
			// (Legacy: CharIndexGlobal == -1) — the hash spoof alone still opens shops; only the
			// pause-menu name stays freemode while spoofed.
			heldIndexAddr = CharIndexGlobal >= 0 ? GetGlobalPtr(CharIndexGlobal) : IntPtr.Zero;
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

		// The ped handle we last fully validated, so the steady-state path can skip the expensive
		// re-resolution. -1 = nothing validated yet (force the slow path next tick).
		int validatedPedHandle = -1;

		// Re-assert each tick so a game rewrite can't silently drop the spoof, and so we
		// auto-release if the held ped/archetype changed under us (model swap, respawn).
		//
		// Steady state (held, same ped, every frame) is the hot path — the old code re-resolved the
		// ped address and ran two VirtualQuery syscalls EVERY frame just to re-verify an unchanging
		// state, measured at ~1.25ms/frame. But the held addresses only move when the ped is replaced
		// or its model swaps, and BOTH change the player ped HANDLE. So: validate fully only when the
		// handle changes; otherwise the cached heldHashAddr/heldIndexAddr are still valid and we just
		// re-write the value (a couple of cheap memory ops, no natives, no VirtualQuery).
		public void Tick() {
			if (!Held) return;
			try {
				int handle = Game.Player.Character?.Handle ?? 0;
				if (handle == 0) {
					Restore("ped gone");
					return;
				}
				if (handle != heldPedHandle) {
					// The player ped was replaced under us (a story character switch / respawn / our own
					// re-apply gives a new handle). Release via Restore: heldHashAddr is the SHARED
					// freemode model-info slot we poisoned, so restoring writes the freemode hash back
					// THERE — it never touches the new ped's own archetype, and the spoofHash guard in
					// Restore skips the write if a genuinely different model already sits at that slot.
					Restore("ped replaced under hold");
					return;
				}

				// Slow path: only when the handle first appears or changed. Re-resolve the archetype and
				// confirm it still points at the slot we poisoned; bail if the model swapped under the
				// same handle (rare). On success cache the handle so subsequent frames fast-path.
				if (handle != validatedPedHandle) {
					Ped ped = Game.Player.Character;
					IntPtr addr = ped != null ? ped.MemoryAddress : IntPtr.Zero;
					if (addr == IntPtr.Zero) {
						Restore("ped gone");
						return;
					}
					IntPtr archetype = MemScan.SafeReadPtr(addr + ArchetypeOffset);
					if (archetype + HashOffset != heldHashAddr) {
						// The archetype slot moved (model swap). Restore targets the old poisoned slot and
						// is gated on it still holding our spoof value.
						Restore("archetype changed under hold");
						return;
					}
					validatedPedHandle = handle;
				}

				// Hot path: addresses validated and the handle hasn't changed, so just keep the spoof
				// value asserted with guarded writes — no natives, no VirtualQuery. heldHashAddr was
				// proven readable at engage and can't have moved without a handle change.
				if (MemScan.ReadUInt32(heldHashAddr) != spoofHash) {
					MemScan.WriteUInt32(heldHashAddr, spoofHash);
				}
				if (heldIndexAddr != IntPtr.Zero && MemScan.ReadInt32(heldIndexAddr) != spoofIndex) {
					MemScan.WriteUInt32(heldIndexAddr, unchecked((uint)spoofIndex));
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
			validatedPedHandle = -1;
		}
	}
}
