#pragma once

#include <cstdint>

// Runtime AOB scanner. Enhanced's .text is encrypted on disk and decrypted only in
// memory, so engine addresses MUST be found by scanning the live process — they
// cannot be derived statically. Patterns are build-pinned (~1013.34).
namespace Pattern {

// Scan the main module's executable section for an IDA-style byte pattern, e.g.
// "EB 2A 0F 1F 40 00 48 8B 54 17 10" where "?" / "??" is a wildcard byte.
// Returns the address of the first match, or nullptr if not found.
uint8_t* Scan(const char* signature);

} // namespace Pattern
