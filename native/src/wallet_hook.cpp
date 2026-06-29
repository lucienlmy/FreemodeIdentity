#include "wallet_hook.hpp"

#include "build_edition.hpp"
#include "decoration.hpp"
#include "logger.hpp"
#include "natives.hpp"
#include "natives_legacy.hpp"
#include "waypoint.hpp"
#include "rage.hpp"
#include "shared_state.hpp"

#include "MinHook.h"

#include <windows.h>

#include <cstdint>

namespace {

// TRANSLATED (build-specific) native hashes — the registration table is keyed by these on
// BOTH editions, not the stable documented hashes. RAGE shuffles native hashes per build; the
// stable hash (e.g. STAT_SET_INT 0xB3271D7AB655B441) is mapped through a crossmap to the
// build's hash before any lookup. These values come from FiveM CrossMapping_Universal.h column
// 27 — the frozen post-b2944 Legacy column, which b3788 uses, and ALSO the Enhanced ~1013.34
// values (the two builds share this column). So one pair serves both editions here.
//   stable 0xB3271D7AB655B441 (STAT_SET_INT) -> 0x1164A75E490C27B6  (bucket 0xB6)
//   stable 0x767FBC2AC802EF3D (STAT_GET_INT) -> 0xDF7F16323520B858  (bucket 0x58)
constexpr uint64_t XHASH_STAT_SET_INT = 0x1164A75E490C27B6;
constexpr uint64_t XHASH_STAT_GET_INT = 0xDF7F16323520B858;

// The shared bridge block. C# resolves its address (export below) and drives
// redirectEnabled / activeStat / balance; the hooks read it. C# is the authority.
ShimBridgeState g_state = { SHIM_BRIDGE_STATE_VERSION, 0, 0, 0, 0, 0, 0, 0, 0, {0, 0, 0, 0} };

rage::scrNativeHandler g_origStatSetInt = nullptr;
rage::scrNativeHandler g_origStatGetInt = nullptr;
bool g_installed = false;

// --- Distinguishing a real money event from a HUD echo --------------------------------
// While spoofed the game polls the active stat with GETs every frame and periodically
// re-SETs it to a value it last displayed — an echo of a value we handed out via GET, not a
// transaction. The SET hook must adopt only genuine events, by value alone, on the game
// thread. Two kinds qualify:
//   DEBIT  — a shop purchase: value strictly BELOW balance, paired to a recent redirected
//            affordability GET (the purchase is debited right after the shop reads your money).
//            Echo rejection must NOT gate it: an echo re-writes the value we reported
//            (== balance), so a strict decrease is never an echo — and gating on it wrongly
//            dropped a debit to exactly $0 (WasRecentlyReported(0) is true on the
//            zero-initialised ring), silently failing any purchase that empties the wallet.
//   INCOME — a script payout: value ABOVE balance. e.g. any mod paying via SHVDN
//            Game.Player.Money (`Money += reward`) is a STAT_SET_INT raising the stat, which
//            while spoofed is ours. Echo rejection alone gates it: we only ever report
//            `balance` via GET, so a value strictly above it was never reported by us and
//            can't be an echo. (No pairing — a payout isn't preceded by a shop read.)
// Echo rejection (remember recent GET-reported values, reject a SET matching one) is the
// primary, timing-free signal for both. The protagonist's real cash is never written, and a
// rejected real event self-heals — C# re-pushes the wallet's truth and the next genuine SET
// re-fires.
constexpr int kReportedRing = 8;        // how many recent GET-reported values to remember
constexpr ULONGLONG kAffordWindowMs = 750; // GET→SET pairing window for a purchase

int g_reported[kReportedRing] = {};     // ring of values we recently handed out via GET
int g_reportedCount = 0;                // total reports (index via % kReportedRing)
ULONGLONG g_lastAffordGetTick = 0;      // tick of the last redirected affordability GET
int g_lastTracedGet = -1;               // last GET value we trace-logged, to collapse per-frame repeats

// Trace gate: at Debug log level (C# pushes it from the ini [Logging] Level), log every
// redirected GET/SET on the active stat — value + the gate booleans — to inspect the call
// pattern when something doesn't add up. Flips at runtime without a rebuild.
bool TraceOn() { return g_state.logLevel >= 1; }

void RememberReported(int value) {
	g_reported[g_reportedCount % kReportedRing] = value;
	++g_reportedCount;
}

bool WasRecentlyReported(int value) {
	int n = g_reportedCount < kReportedRing ? g_reportedCount : kReportedRing;
	for (int i = 0; i < n; ++i) {
		if (g_reported[i] == value)
			return true;
	}
	return false;
}

// Redirect this stat access to the wallet right now? Only when C# enabled redirect AND
// this is one of the active wallet stats C# chose (the SPx cash or bank stat the spoofed
// model resolves to). One wallet backs both, so either stat reads/charges the same total.
bool ShouldRedirect(int statHash) {
	return g_state.redirectEnabled != 0 && statHash != 0
	    && (statHash == g_state.activeStat || statHash == g_state.activeBankStat);
}

void HookStatSetInt(rage::scrNativeCallContext* ctx) {
	// STAT_SET_INT(int statHash, int value, BOOL save)
	int statHash = ctx->GetArg<int>(0);
	if (ShouldRedirect(statHash)) {
		int value = ctx->GetArg<int>(1);
		int save = ctx->GetArg<int>(2);
		// Never let the real write through — the protagonist's cash stays untouched. Adopt the
		// value only as a debit or income per the discrimination notes above; a HUD echo is
		// dropped and C# re-pushes the truth next tick.
		bool echo = WasRecentlyReported(value);
		bool decrease = value >= 0 && value < g_state.balance;
		bool increase = value > g_state.balance;
		bool paired = (GetTickCount64() - g_lastAffordGetTick) <= kAffordWindowMs;
		// No !echo here — a strict decrease can't be an echo, and gating on it drops a debit to $0.
		bool debit = decrease && paired;
		bool income = increase && !echo;

		if (TraceOn()) {
			Logger::Debugf("redirect SET stat=0x%08X val=$%d save=%d bal=$%d "
			               "[dec=%d inc=%d echo=%d paired=%d] -> %s",
			               statHash, value, save, g_state.balance,
			               decrease, increase, echo, paired,
			               debit ? "DEBIT" : income ? "INCOME" : "skip");
		}

		if (debit || income) {
			int delta = value - g_state.balance;
			Logger::Logf("redirect %s stat=0x%08X $%d -> $%d (%+d)",
			             income ? "INCOME" : "DEBIT", statHash,
			             g_state.balance, value, delta);
			// Report the EVENT as a signed delta C# accumulates + applies; also advance the
			// live mirror so a follow-up SET this same frame measures against the new total.
			// C# re-pushes the authoritative balance next tick (and zeroes the delta).
			g_state.pendingDelta += delta;
			g_state.balance = value;
		}
		return;
	}
	g_origStatSetInt(ctx);
}

void HookStatGetInt(rage::scrNativeCallContext* ctx) {
	// STAT_GET_INT(int statHash, int* out, int) — report the wallet balance for
	// affordability. Let the original run (sets the success return + the real value),
	// then overwrite the out-param with our balance.
	int statHash = ctx->GetArg<int>(0);
	if (ShouldRedirect(statHash)) {
		int value = g_state.balance;
		g_origStatGetInt(ctx);
		if (int* out = ctx->GetArg<int*>(1))
			*out = value;
		// Tag this as a recent affordability read and remember the value we handed out, so
		// the SET hook can pair a debit to it and reject a later echo of this same value.
		g_lastAffordGetTick = GetTickCount64();
		RememberReported(value);
		// The game polls this stat EVERY FRAME while spoofed, so tracing each GET floods the log
		// with identical lines. Log only when the reported value changes — that's the only GET
		// that carries information; the steady-state poll is noise.
		if (TraceOn() && value != g_lastTracedGet) {
			g_lastTracedGet = value;
			Logger::Debugf("redirect GET stat=0x%08X -> reported $%d", statHash, value);
		}
		return;
	}
	g_origStatGetInt(ctx);
}

// Native handlers on Enhanced are short thunks that jmp to the real body; MinHook can't
// relocate those (MH_ERROR_UNSUPPORTED_FUNCTION). Follow a leading jmp. Handles E9
// (rel32) and FF 25 (jmp qword [rip+disp32]). Legacy handlers are direct functions, so this
// is a no-op there (the leading byte won't be a jmp).
void* ResolveThunk(void* fn) {
	uint8_t* p = reinterpret_cast<uint8_t*>(fn);
	if (p[0] == 0xE9) {
		int32_t rel = *reinterpret_cast<int32_t*>(p + 1);
		return p + 5 + rel;
	}
	if (p[0] == 0xFF && p[1] == 0x25) {
		int32_t disp = *reinterpret_cast<int32_t*>(p + 2);
		return *reinterpret_cast<void**>(p + 6 + disp);
	}
	return fn;
}

// Resolve a native handler for the running edition. Both editions key on the SAME translated
// hash (XHASH_*); only the resolver differs — Legacy walks the scrNativeRegistration table,
// Enhanced uses the InitNativeTables trick.
rage::scrNativeHandler ResolveHandler(uint64_t hash) {
	return BuildEdition::IsLegacy() ? NativesLegacy::GetHandler(hash) : Natives::GetHandler(hash);
}

bool InstallOne(uint64_t hash, void* detour, void** trampoline, const char* name) {
	rage::scrNativeHandler handler = ResolveHandler(hash);
	if (!handler) {
		Logger::Logf("shim: could not resolve handler for %s (%016llX)", name, hash);
		return false;
	}
	// Legacy handlers are direct functions; Enhanced ones are jmp thunks MinHook can't relocate.
	void* target = BuildEdition::IsLegacy()
	                   ? reinterpret_cast<void*>(handler)
	                   : ResolveThunk(reinterpret_cast<void*>(handler));
	if (MH_CreateHook(target, detour, trampoline) != MH_OK) {
		Logger::Logf("shim: MH_CreateHook(%s) failed", name);
		return false;
	}
	if (MH_EnableHook(target) != MH_OK) {
		Logger::Logf("shim: MH_EnableHook(%s) failed", name);
		return false;
	}
	Logger::Logf("shim: hooked %s @ %p", name, target);
	return true;
}

} // namespace

namespace WalletHook {

bool Install() {
	// ScriptHookV re-invokes ScriptMain on a session reload WITHOUT unloading the .asi, so
	// MinHook is still initialised from the first run — MH_ERROR_ALREADY_INITIALIZED is not a
	// failure, the existing hooks are simply still live. Only a genuine init error aborts.
	MH_STATUS init = MH_Initialize();
	if (init != MH_OK && init != MH_ERROR_ALREADY_INITIALIZED) {
		Logger::Logf("shim: MH_Initialize failed (%d).", init);
		return false;
	}
	if (init == MH_ERROR_ALREADY_INITIALIZED) {
		Logger::Log("shim: MinHook already initialised (session reload) — hooks remain active.");
		g_installed = true;
		return true; // hooks from the first install are still in place; nothing to redo
	}
	Logger::Logf("shim: detected %s edition — using its native-resolution path.", BuildEdition::Name());

	// Resolve the ped-decoration array base from the live .text and publish it for C#. Independent
	// of the STAT hooks (it needs no MinHook) — a wallet-hook failure below must not lose it, and
	// even with the wallet off C# wants it for tattoo capture. 0 just means C# skips tattoos.
	g_state.decorationBase = Decoration::ResolveArrayBase();

	// Resolve the Enhanced WaypointInfoArray entries so C# can re-key the waypoint across an identity
	// spoof. No-op on Legacy (C# finds the array itself). A miss just leaves zeros — C# skips the fix.
	Waypoint::ResolveEntries(g_state.waypointInfoArray);

	// Legacy: scrNativeRegistration table walk. Enhanced: the InitNativeTables resolver. Both
	// editions key on the SAME translated hashes (XHASH_*) — they share crossmap column 27.
	bool resolverReady = BuildEdition::IsLegacy() ? NativesLegacy::Init() : Natives::Init();
	if (!resolverReady)
		return false; // pattern miss already logged; fail safe

	bool ok = true;
	ok &= InstallOne(XHASH_STAT_SET_INT, reinterpret_cast<void*>(&HookStatSetInt),
	                 reinterpret_cast<void**>(&g_origStatSetInt), "STAT_SET_INT");
	ok &= InstallOne(XHASH_STAT_GET_INT, reinterpret_cast<void*>(&HookStatGetInt),
	                 reinterpret_cast<void**>(&g_origStatGetInt), "STAT_GET_INT");

	g_installed = ok;
	Logger::Log(ok ? "shim: hooks installed." : "shim: install incomplete — see failures.");
	return ok;
}

void Uninstall() {
	if (g_installed) {
		MH_DisableHook(MH_ALL_HOOKS);
		MH_RemoveHook(MH_ALL_HOOKS);
		g_installed = false;
	}
	MH_Uninitialize();
}

ShimBridgeState* State() { return &g_state; }

} // namespace WalletHook
