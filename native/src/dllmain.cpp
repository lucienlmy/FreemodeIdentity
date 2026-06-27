#include "logger.hpp"
#include "shared_state.hpp"
#include "wallet_hook.hpp"

#include <windows.h>

#include "main.h"
#include "nativeCaller.h"

namespace {

HMODULE g_module = nullptr;
bool g_hooksInstalled = false;

constexpr uint64_t HASH_PLAYER_PED_ID = 0xD80958FC74E988A6;
constexpr uint64_t HASH_DOES_ENTITY_EXIST = 0x7239B21A38F536BA;

bool WorldReady() {
	int ped = invoke<int>(HASH_PLAYER_PED_ID);
	return ped != 0 && invoke<BOOL>(HASH_DOES_ENTITY_EXIST, ped) != FALSE;
}

void ScriptMain() {
	Logger::Log("shim ScriptMain started.");
	int waited = 0;
	while (!WorldReady()) {
		scriptWait(0);
		if (++waited % 500 == 0)
			Logger::Logf("shim: waiting for world... (%d ticks)", waited);
	}
	Logger::Log("shim: world ready — installing hooks.");
	g_hooksInstalled = WalletHook::Install();

	// Nothing per-frame: the redirect lives entirely in the STAT hooks, driven by the
	// shared state C# writes. Just keep the fiber alive.
	while (true)
		scriptWait(0);
}

} // namespace

// Exported for the C# mod: returns the address of the shared bridge block so C# can
// drive redirectEnabled / activeStat / balance and read back debits. C resolves it via
// GetProcAddress("FreemodeIdentity_GetState") on the loaded FreemodeIdentity.asi.
extern "C" __declspec(dllexport) FreemodeWalletState* FreemodeIdentity_GetState() {
	return WalletHook::State();
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID /*reserved*/) {
	switch (reason) {
	case DLL_PROCESS_ATTACH:
		g_module = module;
		DisableThreadLibraryCalls(module);
		Logger::Init(module);
		Logger::Log("shim DLL_PROCESS_ATTACH — registering script.");
		scriptRegister(module, ScriptMain);
		break;
	case DLL_PROCESS_DETACH:
		if (g_hooksInstalled)
			WalletHook::Uninstall();
		scriptUnregister(module);
		Logger::Log("shim DLL_PROCESS_DETACH — unregistered.");
		break;
	}
	return TRUE;
}
