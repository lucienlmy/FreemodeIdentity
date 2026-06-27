#pragma once

#include "rage.hpp"

#include <cstdint>

// Resolves engine native-handler function pointers by HASH on GTA V Enhanced.
//
// The engine function InitNativeTables(scrProgram*) takes a program whose
// m_NativeEntrypoints array is pre-filled with native HASHES and rewrites each
// entry in place into the resolved HANDLER pointer. We exploit that: hand it a
// throwaway program full of the hashes we want, let it resolve, read them back.
// (Mechanism verified against YimMenuV2 `enhanced` NativeInvoker::CacheHandlers.)
namespace Natives {

// Locate InitNativeTables via pattern scan. Returns false (fail-safe) if missing.
bool Init();

// Resolve one native hash to its handler. nullptr if unresolved.
rage::scrNativeHandler GetHandler(uint64_t hash);

// Diagnostic: resolve several distinct hashes in one pass and log each address.
void Probe();

} // namespace Natives
