# AGENTS.md — FreemodeWallet (hybrid C# mod + native spend shim)

Gives a **freemode (MP-model) single-player character a real spendable wallet** —
earns from cash pickups, spends in shops — like a story protagonist. It is an
**add-on to AppearanceKeeper**: the wallet is only useful while a freemode ped is
spoofed to a protagonist model (AppearanceKeeper's job), since shops gate entry and
money resolution on the model. Ships and versions separately, but depends on
AppearanceKeeper at runtime.

## Why hybrid (read this first)
The single hard engine fact that shapes everything: **SP shop spending is resolved
from the player MODEL → char index → a per-character cash slot, and the live wallet
the HUD/shops read is a script GLOBAL the running game scripts continuously rewrite.**
There is **no native or stat** a managed script can flip to make a freemode char spend
a protagonist's wallet — proven dead in the original C# attempt. The only thing that
works is intercepting the shop's `STAT_GET_INT`/`STAT_SET_INT` on the active
`SP{N}_TOTAL_CASH` stat with a MinHook detour — and **SHVDN/C# cannot hook native
handlers**. So:

- **C# `.dll` (SHVDN3)** — the bulk: earning, the wallet balance + persistence, config,
  hotkeys, menu. Matches the workspace's other mods. House style:
  `d:\workspace\gta5\AppearanceKeeper\AGENTS.md`.
- **Native `.asi` shim (C++)** — does ONLY what C# can't: the STAT_GET/SET_INT detour
  that makes shops charge our wallet. As small as possible; it should rarely change.
  House style: `d:\workspace\gta5\FreemodeWalletHook\AGENTS.md`.

Most logic lives in C#. The native side is a fixed, minimal spend-redirect shim. Don't
grow the native side with anything C# can do.

## The bridge (C# ↔ native)
The STAT detour runs **synchronously on the game thread inside the shop's native call**
— it cannot call back into managed C#. So the two sides share memory, not callbacks:

- The native shim owns a tiny shared state block: at least `{ int balance; int
  redirectEnabled; }`. The detour reads `balance` for affordability (GET) and writes it
  on debit (SET); it only redirects when `redirectEnabled` and the model resolves to a
  shadowed SPx.
- The native shim **exports** an accessor (e.g. `extern "C" __declspec(dllexport)
  void* FreemodeWallet_GetState()`); C# resolves it via `LoadLibrary("FreemodeWallet.asi")`
  + `GetProcAddress`, then reads/writes the shared ints directly (the same memory-access
  approach AppearanceKeeper's `MemScan` uses). **C# is the authority** for the balance
  (it earns + persists); the native block is the live mirror the shop reads.
- Keep the shared layout in ONE place documented on both sides; a field added on one
  side and not the other is the classic "spend didn't register" bug.

## Verified engine facts (build VER_EN_1_0_1013_34, x64 — re-verify per build)
These were settled in-game; don't re-derive or second-guess them without a probe.
- **Earning bypasses every stat native.** A collected cash pickup credits the SP cash
  global directly — no `STAT_SET_INT`/`GET_INT`/`INCREMENT` fires. A freemode char earns
  NOTHING from pickups (the credit has no slot to land in). So we credit the wallet
  ourselves on pickup collection.
- **Pickup object struct:** cash VALUE is an `int` at **+0x480**; pickup TYPE hash
  (joaat) at **+0x468**. Found by a sentinel ground-truth probe. Filter to money pickup
  types (`PICKUP_MONEY_VARIABLE` 0xFE18F3AF, `_CASE` 0xCE6FDD6B, `_WALLET` 0x5DE0AD3E,
  `_PURSE` 0x1E9A99F8, `_DEP_BAG` 0x20893292, `_MED_BAG` 0x14568F28, `_PAPER_TRAIL`
  0xA3435C38), read +0x480, credit once when the tracked pickup leaves the world pool.
- **Spend stats:** `SP0_TOTAL_CASH` 0x0324C31D (Michael), `SP1` 0x44BD6982 (Franklin),
  `SP2` 0x8D75047D (Trevor). Redirect only the SPx the live model resolves to
  (player_zero→SP0, _one→SP1, _two→SP2); pass the others through.
- **Native-handler resolution on Enhanced needs TRANSLATED (build-specific) hashes**, not
  stable ones (from YimMenuV2 crossmap.txt). MinHook must follow leading `E9`/`FF25`
  thunks to the real body. (Native side only; SHVDN `Function.Call` uses stable hashes.)
- The live SP cash global is `getGlobalPtr(0xF2FA)[charIdx]`; WRITING it is reverted by
  the game scripts (read-only for our purposes).

## Cross-cutting rules (both sides)
- **Activation is opt-in (a hotkey toggle, OFF by default).** A spoofed protagonist
  reads identical to genuine story play, so auto-redirect would hijack a real
  protagonist's wallet. Earning and spending share the one toggle.
- **Files go next to the artifact's writable location, never the game root.** C#:
  next to the DLL (`scripts/`). Native: Enhanced LOCKS files under the game tree at
  launch, so the shim writes to `%APPDATA%\GTA V Mods\KernelPryanic\FreemodeWallet\`.
  The wallet balance persists there (the in-game stats can't hold it). Missing file =
  $0 (valid empty state); corrupt = log + $0; reads never throw.
- **Raw memory reads are VirtualQuery-gated** on both sides — an AV on an unmapped page
  is uncatchable and kills the game.
- No work in `DllMain` (native) / keep `OnTick` cheap and throttled (C#). Heavy natives
  must run on the script thread, not the input/keyboard thread.
- No "ASI" token in target/zip/identifier names (the `.asi` extension aside).

## Build / package
- C# side: `make build` / `lint` / `package` per the SHVDN house style (Release x64,
  .NET 4.8; ship the `.dll` + `.ini` into `scripts/`).
- Native side: CMake + MSVC x64, `/MT`, output renamed `FreemodeWallet.asi` per the
  native house style; report md5 after each build.
- The user DEPLOYS and runs in-game tests (game CLOSED to overwrite the locked `.asi`);
  don't copy into the game folder or push a remote unless asked.
- Packaging ships both artifacts (`.dll`+`.ini` in `scripts/`, `.asi` in the game root)
  plus the Vortex `gta5mod.json`. No README in the package.

## Don't do
- Don't put in the native shim anything C# can do — it stays a minimal spend-redirect.
- Don't try to make spending work without the native hook (the managed/stat/global
  routes are all proven dead — see the engine facts above).
- Don't hardcode an engine ADDRESS that must come from a runtime pattern scan; stat and
  native hashes are version-stable and may be named constants, struct offsets are pinned
  per build and must be re-verified.
- Don't auto-redirect without the explicit toggle (would hijack real story money).
- No dead code, no comments that restate code — comment only WHY.
