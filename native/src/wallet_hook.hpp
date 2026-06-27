#pragma once

struct FreemodeWalletState;

// The native spend-redirect shim. Hooks STAT_GET_INT / STAT_SET_INT so that, while C#
// has redirect enabled (the player is spoofed to a protagonist), the shop's reads/writes
// of the active SP{N}_TOTAL_CASH stat route to the shared wallet balance instead of the
// real protagonist stat. The balance + the enable/active-stat flags live in the shared
// state block C# drives; this shim only does the hook C# can't.
namespace WalletHook {

bool Install();
void Uninstall();

// The shared bridge block (see shared_state.hpp). Exported to C# via dllmain.
FreemodeWalletState* State();

} // namespace WalletHook
