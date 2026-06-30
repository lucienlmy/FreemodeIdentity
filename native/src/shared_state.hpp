#pragma once

#include <cstdint>

// The bridge between the C# mod and this native shim. SHVDN can't hook native handlers,
// so the shim owns the STAT spend-redirect detour; but the wallet balance + the feature
// state live in C#. They share THIS struct: the shim holds one static instance and
// exports its address; C# resolves that address (GetProcAddress on the loaded .asi) and
// reads/writes the fields directly with Marshal.
//
// C# is the authority for `balance`, `redirectEnabled` and `activeStat`; the shim reads
// them inside the shop's STAT hook. Money EVENTS flow shim -> C# as a signed `pendingDelta`
// the shim ACCUMULATES (a shop debit is negative, a script payout is positive); C# applies
// and zeroes it each tick. Reporting a delta (an event) rather than an absolute balance is
// what lets a same-tick world-pickup credit (applied in C#) and a shop/job change (the
// delta) BOTH land without one overwriting the other. Keep this layout IDENTICAL on both
// sides — a field added here but not in the C# mirror is the classic "spend didn't register"
// bug.
//
// The seven per-character ability stats the skill redirect pins (strength, stamina, shooting,
// stealth, flying, driving, lung). Fixed-size so the layout stays static across both sides.
constexpr int32_t SHIM_SKILL_COUNT = 7;

// Ints are 4-byte; the 8-byte pointers are placed LAST and 8-aligned. The int32 region before the
// first pointer must be an EVEN count so decorationBase lands on an 8-byte boundary with no padding.
// Count: 8 wallet/log ints + 1 skillsPinned + 7 skillHashes + 7 skillValues + 1 reserved2 = 24 (even).
// Version guards an accidental layout mismatch.
struct ShimBridgeState {
	int32_t version;          // = STATE_VERSION; C# checks it before trusting the block
	int32_t redirectEnabled;  // C# -> shim: 1 = redirect the active wallet stats to us
	int32_t activeStat;       // C# -> shim: the SP{N}_TOTAL_CASH joaat to redirect (0 = none)
	int32_t activeBankStat;   // C# -> shim: the SP{N}_BANK_BALANCE joaat to redirect too (0 = none)
	int32_t balance;          // C# -> shim: the live wallet total the shim mirrors + reports
	int32_t pendingDelta;     // shim -> C#: accumulated signed change (debit<0/income>0); C# zeroes it
	int32_t logLevel;         // C# -> shim: log verbosity (0 = Info, 1 = Debug); ini [Logging] Level
	int32_t reserved;         // padding to an even int count so decorationBase stays 8-aligned
	// Skill redirect: pin the spoofed protagonist's SP{N} skill stats to a user-set profile. Unlike
	// cash, skills aren't transacted — the game's stat system keeps REVERTING a managed write back to
	// the real protagonist's saved values, so a pure-C# set can't hold. The shim answers a GET on any
	// of these hashes with the matching value and SWALLOWS a SET, so our profile wins. C# fills the
	// hashes for the active spoof target and the values from the profile; both 0 when nothing to pin.
	int32_t skillsPinned;                  // C# -> shim: 1 = pin the skill stats below (Skills on + spoofed)
	int32_t skillHashes[SHIM_SKILL_COUNT]; // C# -> shim: the active SP{N}_<skill> joaats to redirect
	int32_t skillValues[SHIM_SKILL_COUNT]; // C# -> shim: the profile value (0..100) for each hash
	int32_t reserved2;        // keeps the int32 region an even count so decorationBase stays 8-aligned
	uint64_t decorationBase;  // shim -> C#: ped-decoration array base resolved by .text scan (0 = none)
	// shim -> C#: the four WaypointInfoArray entry addresses (Enhanced's array is unrolled into four
	// separate globals, not a contiguous range). Each entry is { int modelHash; int blipHandle; ... };
	// C# re-keys modelHash to follow the spoof. 0 in any slot = unresolved, C# skips the waypoint fix.
	uint64_t waypointInfoArray[4];
};

constexpr int32_t SHIM_BRIDGE_STATE_VERSION = 5;
