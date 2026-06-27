#include "natives.hpp"

#include "logger.hpp"
#include "pattern.hpp"

#include <windows.h>

namespace {

using InitNativeTablesFn = void (*)(rage::scrProgram*);
InitNativeTablesFn g_initNativeTables = nullptr;

} // namespace

namespace Natives {

bool Init() {
	// InitNativeTables prologue; .Sub(0x2A) steps back to the function entry.
	// Pattern verified from YimMenuV2 enhanced Pointers.cpp (build ~1013.34).
	uint8_t* hit = Pattern::Scan("EB 2A 0F 1F 40 00 48 8B 54 17 10");
	if (!hit) {
		Logger::Log("Natives::Init FAILED — InitNativeTables pattern not found.");
		return false;
	}
	g_initNativeTables = reinterpret_cast<InitNativeTablesFn>(hit - 0x2A);
	Logger::Logf("Natives::Init OK — InitNativeTables @ %p", (void*)g_initNativeTables);
	return true;
}

rage::scrNativeHandler GetHandler(uint64_t hash) {
	if (!g_initNativeTables)
		return nullptr;

	// Fabricate a throwaway program with a single-entry entrypoint table holding
	// the hash; the engine resolver rewrites it in place to the handler pointer.
	rage::scrProgram program = {};
	rage::scrNativeHandler entry = reinterpret_cast<rage::scrNativeHandler>(hash);
	program.m_NativeCount = 1;
	program.m_NativeEntrypoints = &entry;

	g_initNativeTables(&program);

	// If the hash was unknown the resolver leaves it as the raw hash — reject that.
	if (reinterpret_cast<uint64_t>(entry) == hash)
		return nullptr;
	return entry;
}

void Probe() {
	if (!g_initNativeTables) {
		Logger::Log("Natives::Probe — resolver not initialised.");
		return;
	}
	// Resolve several distinct hashes in ONE table pass and log each result, to
	// confirm per-slot resolution works. Uses TRANSLATED (build-1013.34) hashes —
	// InitNativeTables only resolves those, not the stable hashes.
	struct Item { const char* name; uint64_t hash; };
	const Item items[] = {
	    {"STAT_SET_INT", 0x1164A75E490C27B6},
	    {"STAT_GET_INT", 0xDF7F16323520B858},
	    {"PLAYER_PED_ID", 0x4A8C381C258A124D},
	    {"GET_ENTITY_MODEL", 0x4B423FAA24E8ABF0},
	};
	const int n = 4;
	rage::scrNativeHandler entries[n];
	for (int i = 0; i < n; ++i)
		entries[i] = reinterpret_cast<rage::scrNativeHandler>(items[i].hash);

	rage::scrProgram program = {};
	program.m_NativeCount = n;
	program.m_NativeEntrypoints = entries;
	g_initNativeTables(&program);

	for (int i = 0; i < n; ++i) {
		bool resolved = reinterpret_cast<uint64_t>(entries[i]) != items[i].hash;
		Logger::Logf("Natives::Probe  %-18s -> %p%s", items[i].name,
		             (void*)entries[i], resolved ? "" : "  (UNRESOLVED)");
	}
}

} // namespace Natives
