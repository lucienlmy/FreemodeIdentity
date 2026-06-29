#include "waypoint.hpp"

#include "build_edition.hpp"
#include "logger.hpp"
#include "pattern.hpp"

#include <cstdint>

// Resolve the Enhanced WaypointInfoArray entry addresses from the live (decrypted) .text. The array
// holds { int modelHash; int blipHandle; float x; float y } entries; the game finds the active
// waypoint by matching the player ped's model hash. C# re-keys the matching entry's modelHash to the
// spoofed protagonist so the waypoint follows the identity (see WaypointKeeper.cs).
//
// Enhanced inlines/unrolls the array access into four separate rip-relative loads (Legacy keeps a
// contiguous start..end range, resolved in C# via Game.FindPattern — the shim does nothing there).
// Patterns mirror SHVDN NativeMemory.cs's Enhanced waypoint resolution.
namespace {

// First entry: `lea rdx,[rip+disp]; cmp dword [rdx+rax*..],..`. disp at +3, instr len 7.
constexpr const char* kEntry0Sig = "48 8D 15 ?? ?? ?? ?? 83 7C C2";

// Entries 1-3: a run of `cmp [rip+disp],ecx; jz ..` loads. The three disps sit at fixed offsets in
// the matched block: +2 (instr len 6), +15 (len 19), +28 (len 32).
constexpr const char* kEntry123Sig = "39 0D ?? ?? ?? ?? 74 9F";

uint64_t RipTarget(uint8_t* hit, int dispOffset, int instrLen) {
	int32_t disp = *reinterpret_cast<int32_t*>(hit + dispOffset);
	return reinterpret_cast<uint64_t>(hit + instrLen + disp);
}

} // namespace

namespace Waypoint {

void ResolveEntries(uint64_t out[4]) {
	out[0] = out[1] = out[2] = out[3] = 0;
	if (BuildEdition::IsLegacy())
		return; // Legacy resolves the contiguous array in C#; nothing for the shim to do.

	uint8_t* hit0 = Pattern::Scan(kEntry0Sig);
	uint8_t* hit123 = Pattern::Scan(kEntry123Sig);
	if (!hit0 || !hit123) {
		Logger::Log("Waypoint: Enhanced WaypointInfoArray pattern not found — C# skips the waypoint fix.");
		return;
	}

	out[0] = RipTarget(hit0, 3, 7);
	out[1] = RipTarget(hit123, 2, 6);
	out[2] = RipTarget(hit123, 15, 19);
	out[3] = RipTarget(hit123, 28, 32);
	Logger::Logf("Waypoint: Enhanced WaypointInfoArray entries @ %p %p %p %p.",
	             (void*)out[0], (void*)out[1], (void*)out[2], (void*)out[3]);
}

} // namespace Waypoint
