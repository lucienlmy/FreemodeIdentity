#pragma once

#include <cstddef>
#include <cstdint>

// Minimal RAGE engine type layouts needed to resolve and hook native handlers on
// GTA V Enhanced (build ~1013.34). Offsets/layout verified against YimMenuV2's
// `enhanced` branch (types/script/scrProgram.hpp, scrNativeHandler.hpp).

namespace rage {

using scrNativeHash = uint64_t;
using scrNativeHandler = void (*)(struct scrNativeCallContext*);

// The argument/return marshalling block the engine passes to every native handler.
// We only read GetArg<T>(i); layout (and 0x80 size) is locked.
struct scrNativeCallContext {
	void* m_ReturnValue;                // 0x00
	uint32_t m_ArgCount;                // 0x08
	void* m_Args;                       // 0x10
	int32_t m_NumVectorRefs;            // 0x18
	void* m_VectorRefTargets[4];        // 0x20
	uint8_t m_VectorRefSources[0x40];   // 0x40 (4x fvector3, unused here)

	template <typename T>
	T GetArg(size_t index) const {
		// Args are stored one-per-8-bytes regardless of T's real width.
		return *reinterpret_cast<T*>(reinterpret_cast<uint64_t*>(m_Args) + index);
	}
};
static_assert(sizeof(scrNativeCallContext) == 0x80, "scrNativeCallContext layout");

// A compiled YSC script program. We never receive a real one — we fabricate a
// throwaway whose m_NativeEntrypoints is pre-filled with native HASHES, then let
// the engine's InitNativeTables() rewrite each entry in place to its HANDLER ptr.
// Only the two fields the resolver touches need correct offsets; the rest is pad
// so the struct reaches the real 0x80 size the engine expects to write within.
struct scrProgram {
	uint8_t pad_0000[0x2C];             // 0x00 (pgBase + code/header fields)
	uint32_t m_NativeCount;             // 0x2C
	uint8_t pad_0030[0x10];             // 0x30
	scrNativeHandler* m_NativeEntrypoints; // 0x40
	uint8_t pad_0048[0x38];             // 0x48
};
static_assert(sizeof(scrProgram) == 0x80, "scrProgram layout");
static_assert(offsetof(scrProgram, m_NativeCount) == 0x2C, "m_NativeCount offset");
static_assert(offsetof(scrProgram, m_NativeEntrypoints) == 0x40, "m_NativeEntrypoints offset");

} // namespace rage
