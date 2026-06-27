# Freemode Identity

A GTA V single-player mod (ScriptHookV .NET / SHVDN3) that lets a **freemode
(multiplayer) character live in story mode as a real character** - it keeps the
look you authored and gives that look a spendable wallet so shops and money work.

It merges two jobs into one mod:

- **Appearance** - snapshot your freemode ped's look into named slots and re-apply
  it automatically every session, so your character always comes up as you left them.
- **Wallet** - a freemode ped earns nothing and can't shop (the game resolves money
  from the *protagonist* model). Freemode Identity disguises the ped as a story
  protagonist so shops open, and routes the money to a wallet of its own.

It does not author looks - that's left to a full customizer (e.g.
[Menyoo](https://www.gta5-mods.com/scripts/menyoo-pc-sp)). Freemode Identity
snapshots what the game reports back cleanly and replays it.

Built on SHVDN3 + LemonUI. The appearance/wallet logic ships as a single DLL with
**no third-party dependencies** (persistence uses the .NET `XmlSerializer`); a small
native `.asi` shim does the one thing managed code can't (see *How spending works*).

## What gets preserved

Model (Freemode Male/Female), heritage (face shape + skin tone), the 20 face
micro-morphs, head overlays (brows, makeup, beard, wrinkles) with tint + opacity,
hair drawable/texture and tint, eye colour, voice, clothing components, props,
moving style, mood, and tattoos.

Most of it is read back through native getters. A few fields the game exposes **no
getter** for - the micro-morphs, overlay tint/opacity, hair tint, moving style, mood,
and tattoos - are read directly from the live ped's memory. Every memory read is
bounds-checked and locates its data by content (not a hard-coded address), so a read
that doesn't line up falls back to native-only capture rather than applying anything
wrong. The heavier scans run off the main thread across frames, so saving never
freezes the game.

A Menyoo **randomizer** face (and addon/custom models) lives outside the head-blend
system and can't round-trip; the mod detects this, warns at save time, and leaves the
face untouched on apply rather than writing garbage. Faces built with the **heritage
sliders** or the in-game creator capture fine - and so do **manual Menyoo edits** where
you set the **Head Features** yourself, since those stay inside the head-blend system the
mod reads back.

## How spending works

SP shop spending resolves from the player **model → character index → a per-character
cash slot**, and a freemode ped has no slot - so a freemode character normally can't
shop. Freemode Identity writes a protagonist's model hash onto the ped (the **spoof**)
so shops open and money resolves to that protagonist's cash stat. A tiny native
`.asi` shim then intercepts that stat (`STAT_GET_INT`/`STAT_SET_INT`) and redirects it
to the wallet - the one piece managed code can't do, since SHVDN can't hook natives.

The wallet earns from cash pickups (read straight from the pickup object) and from job
payouts that flow through the stat while spoofed. Spending **only sticks with the
wallet on**: with it off, a purchase completes but the protagonist's real balance never
changes (the shop pokes a cash mirror the game reverts), so the wallet redirect is what
makes money real.

Because the wallet rides the protagonist's own cash stat, the rest of the game reads it
as real money: **ATMs and the pause menu show the right balance**, and external job mods
pay into and charge it correctly. Tested with
[Casual Jobs (Rabbit Holes)](https://www.gta5-mods.com/scripts/casual-jobs-rabbit-holes) -
job payouts land in the wallet while spoofed.

## Install

1. Requires [ScriptHookV](http://www.dev-c.com/gtav/scripthookv/) and
   [ScriptHookV .NET (SHVDN3)](https://github.com/scripthookvdotnet/scripthookvdotnet/)
   with LemonUI.
2. Copy `FreemodeIdentity.dll` into your `GTA V/scripts/` folder.
3. Copy `FreemodeIdentity.asi` into the game root (next to `GTA5_Enhanced.exe`).
   Without it, appearance still works and the wallet still earns - only shop spending
   won't redirect.
4. Launch the game (or reload scripts).

## Use

Press **Shift + X** (configurable) to open the menu. The subtitle shows the version,
the active slot while appearance is on, and the wallet balance while the wallet is on.

Three top-level toggles:

- **Appearance Enabled** - wear your saved look. On applies the active slot on load
  and re-applies it after death/respawn/model-swap; off swaps you back to your story
  character.
- **Wallet Enabled** - earn from pickups and route shop charges to the wallet while
  spoofing. Off makes the wallet inert.
- **Spoofing Enabled** - read as a protagonist so shops open and jobs pay out. Turn the
  wallet on too to make money actually change.

Submenus:

- **Appearance ▸**
  - **Save to New Slot** / **Overwrite Active Slot** / **Apply Active Slot**.
  - **Saved Appearances ▸** - every slot. The active slot is flagged with a coloured `>`:
    green when it has a backup, yellow when it doesn't. Scroll a slot to pick an action,
    then press: **Apply**, **Set Active**, **Overwrite**, **Backup**, **Apply from
    Backup**, **Rename**, **Delete**. Backup copies the slot's saved data aside; Apply
    from Backup restores that copy without touching the slot - a safety net around
    Overwrite. Delete removes the backup too.
  - **Manual Save ▸** - per-feature toggles (Tattoos, Mood, Moving Style) gating the
    heavier scans. Tattoos and Mood are **off by default**.
  - **Edit Mode** - pauses the re-apply and drops the spoof so an external tool (Menyoo)
    can change the ped freely. Save your look, then turn it off.
- **Wallet ▸** - **Pickups Enabled** (credit collected cash pickups).
- **Spoofing ▸** - **Target** (which protagonist to impersonate).
- **Debug ▸** - log level, live identity read-outs, and a force-model escape hatch.

When you're a real story protagonist, spoofing is unavailable (it would hijack real
story money) - switch to a freemode ped first.

## Files

Slots, config and logs live **outside** the game folder, under
`%APPDATA%\GTA V Mods\KernelPryanic\FreemodeIdentity\` (the game locks files written
under the game tree at launch, so that location stays writable):

- `Appearances\<name>.xml` - one file per slot (`<name>.bak.xml` is its backup)
- `FreemodeIdentity.ini` - config
- `FreemodeIdentity.log` / `FreemodeIdentity.shim.log` - diagnostics
- `wallet.dat` - the wallet balance

## Config (`FreemodeIdentity.ini`)

Most settings are set through the menu; the keys are also editable by hand. Grouped by
feature, with non-user runtime state isolated in `[State]`:

| Section / Key                            | Default    | Meaning                              |
| ---------------------------------------- | ---------- | ------------------------------------ |
| `[General] MenuKey`                      | `Shift, X` | Key that toggles the menu            |
| `[General] LogLevel`                     | `Debug`    | Log verbosity (Info/Debug/Error)     |
| `[Appearance] Enabled`                   | `True`     | Wear and defend the active look      |
| `[Appearance] ReturnProtagonist`         | `player_zero` | Fallback character to return to   |
| `[Wallet] Enabled` / `Earning`           | `True` / `True` | Wallet redirect / pickup earning |
| `[Spoof] Enabled` / `Target`             | `False` / `Franklin` | Disguise on / protagonist   |
| `[ManualSave] Tattoos/Mood/MovingStyle`  | `False`/`False`/`True` | Features captured on save |
| `[State] …`                              | -          | Runtime state - don't edit by hand   |

`LogLevel` defaults to `Debug` during the 0.x pre-releases so bug reports carry full
detail; it logs nothing per frame, so the file stays small.

## Build

`make build` (Release x64, .NET 4.8) → `bin/Release/FreemodeIdentity.dll`. `make native`
builds the `.asi` shim (CMake/MSVC). `make lint` for a warning-free rebuild,
`make package` to zip a deploy-ready archive. See `AGENTS.md` for conventions.

The build links against `ScriptHookVDotNet3.dll` and `LemonUI.SHVDN3.dll` from
`..\packages\` (not committed); CI fetches pinned copies. Tagging a `MAJOR.MINOR.PATCH`
commit builds and publishes a release - the tag is the version source of truth.

## Known limitations

- **Freemode peds only.** Story protagonists and addon/custom models have their face
  baked into the model and can't be captured.
- **Menyoo randomizer / trainer faces don't round-trip** - they bypass the head-blend
  system, so the face isn't captured. The mod warns and saves the rest of the look.
- **No spending with the wallet off.** A freemode ped can't actually spend a
  protagonist's real cash; only the wallet redirect makes charges stick.
- **Some charges bypass the stat entirely.** A few interactions (e.g. a prostitute's
  service) deduct through their own in-game logic, not the cash stat the shim hooks, so
  they come out free even with the wallet on. Shops, ATMs and stat-based payouts work.
- **Overlay tint colours** round-trip shape and opacity but not the palette colour on
  Enhanced yet; **custom moving styles/moods** outside the known tables are left unset.

## License

MIT - see [LICENSE](LICENSE). You're free to use, modify and redistribute, including in
your own mods, as long as you keep the copyright and licence notice. The bundled MinHook
in `native/third_party/minhook/` is under its own BSD-2-Clause licence (kept alongside it).
