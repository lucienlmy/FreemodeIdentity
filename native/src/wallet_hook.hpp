#pragma once

struct ShimBridgeState;

// The native spend-redirect shim. Hooks STAT_GET_INT / STAT_SET_INT so that, while C#
// has redirect enabled (the player is spoofed to a protagonist), the shop's reads/writes
// of the active SP{N}_TOTAL_CASH stat route to the shared wallet balance instead of the
// real protagonist stat. The balance + the enable/active-stat flags live in the shared
// state block C# drives; this shim only does the hook C# can't.
namespace WalletHook {

bool Install();
void Uninstall();

// Per-frame pump, called from the shim's script fiber. Re-asserts the pinned skill values
// directly into stat memory every frame — the game reads the real stat object for gameplay
// (sprint exhaustion etc.) and may not call STAT_GET_INT at all, so the GET hook alone can't
// hold a skill. Runs on the game thread (safe for the vfunc call). No-op unless skills are pinned.
void TickPinnedSkills();

// The shared bridge block (see shared_state.hpp). Exported to C# via dllmain.
ShimBridgeState* State();

} // namespace WalletHook
