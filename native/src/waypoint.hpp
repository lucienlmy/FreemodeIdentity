#pragma once

#include <cstdint>

// Resolves the Enhanced WaypointInfoArray entry addresses by scanning the live (decrypted) .text —
// the one thing C# can't do on Enhanced (encrypted .text). See waypoint.cpp. C# reads the four
// addresses from the shared block and re-keys the waypoint entry to follow the identity spoof.
namespace Waypoint {

// Resolve the four WaypointInfoArray entry addresses into out[4]. Enhanced's array is unrolled into
// four separate globals (not a contiguous range like Legacy). Zeroes any slot it can't resolve; C#
// treats a zero slot as "skip the waypoint fix this session". No-op (leaves zeros) on Legacy, which
// resolves the array in C# via Game.FindPattern instead.
void ResolveEntries(uint64_t out[4]);

} // namespace Waypoint
