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
// Ints are 4-byte; the 8-byte pointers are placed LAST and 8-aligned. The eight int32s before the
// first pointer are an even count (32 bytes), so it lands on an 8-byte boundary with no padding; the
// waypoint array follows contiguously. Version guards an accidental layout mismatch.
struct ShimBridgeState {
	int32_t version;          // = STATE_VERSION; C# checks it before trusting the block
	int32_t redirectEnabled;  // C# -> shim: 1 = redirect the active wallet stats to us
	int32_t activeStat;       // C# -> shim: the SP{N}_TOTAL_CASH joaat to redirect (0 = none)
	int32_t activeBankStat;   // C# -> shim: the SP{N}_BANK_BALANCE joaat to redirect too (0 = none)
	int32_t balance;          // C# -> shim: the live wallet total the shim mirrors + reports
	int32_t pendingDelta;     // shim -> C#: accumulated signed change (debit<0/income>0); C# zeroes it
	int32_t logLevel;         // C# -> shim: log verbosity (0 = Info, 1 = Debug); ini [Logging] Level
	int32_t reserved;         // padding to an even int count so decorationBase stays 8-aligned
	uint64_t decorationBase;  // shim -> C#: ped-decoration array base resolved by .text scan (0 = none)
	// shim -> C#: the four WaypointInfoArray entry addresses (Enhanced's array is unrolled into four
	// separate globals, not a contiguous range). Each entry is { int modelHash; int blipHandle; ... };
	// C# re-keys modelHash to follow the spoof. 0 in any slot = unresolved, C# skips the waypoint fix.
	uint64_t waypointInfoArray[4];
};

constexpr int32_t SHIM_BRIDGE_STATE_VERSION = 4;
