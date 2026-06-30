#pragma once

#include <cstdint>

// Direct character-stat memory writer. The SP{N} skill stats (stamina/strength/...) can't be held
// by the STAT_SET_INT native — the game's stat manager reverts a managed write back to the real
// protagonist's saved profile (verified in-game). The gameplay code (e.g. sprint exhaustion) reads
// the REAL stat object in memory, bypassing the native. So this resolves the stats array, finds a
// stat's sStatData object by hash, and writes its value through the object's SetInt vfunc — which
// the manager does NOT revert (verified: the value holds frame-over-frame). Build-pinned (Enhanced
// ~1013.34, Legacy ~b3788); a miss is fatal only to skill pinning (logged), never to the rest of the shim.
namespace Stats {

// Resolve the stats array from the live (decrypted) .text. Safe to call once at install; cached
// thereafter. A miss just means WriteInt no-ops.
bool Init();

// Write an int stat's value directly in memory by its joaat hash. No-op if the array wasn't
// resolved or the hash isn't a stat in the table. Returns true if it wrote.
bool WriteInt(uint32_t hash, int value);

// Read an int stat's value directly from memory (the object's GetInt vfunc), bypassing the
// STAT_GET_INT native. Used to snapshot a stat's real value before we overwrite it, so the
// original can be restored when pinning stops. Returns false if unresolved / not found; value -> *out.
bool ReadInt(uint32_t hash, int* out);

} // namespace Stats
