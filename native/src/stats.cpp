#include "stats.hpp"

#include "build_edition.hpp"
#include "logger.hpp"
#include "pattern.hpp"

#include <windows.h>

#include <cstdint>

// See stats.hpp for why this exists. Layout (from YimMenuV2 + TupoyeMenu, both editions):
//   CStatsMgr { bool m_Initialized@0x00; atArray<sStatMap> m_Stats@0x08 }
//   atArray   { T* data; uint16_t count; uint16_t capacity }   (0x10)
//   sStatMap  { uint32_t hash@0x00; (uint32_t unk@0x04 Enhanced); sStatData* data@0x08 }  (0x10)
//   sStatData : polymorphic, vtable@0x00 — value reached via vfunc, NOT a fixed offset.
// The "All Stats Array" code site loads m_Stats rip-relative; we resolve it then +8 past
// m_Initialized to land on the atArray. Linear-walk the array for a hash to get its sStatData*.
namespace {

// Per-edition "All Stats Array" signatures (TupoyeMenu/CustomLocalSaves pointers.cpp, the
// `ptr.add(6).rip().add(8)` chain). Both load the manager with `lea reg,[rip+disp32]` (a 3-byte
// opcode 48 8D xx), but the lea starts 3 bytes INTO the match (after a preceding 3-byte instr), so
// the disp32 sits at +6 and the NEXT instruction begins at +10. RIP-relative resolution is
// `target = (match + nextInstrOffset) + disp32` — nextInstrOffset is the offset of the byte AFTER
// the lea, not the lea's own length, which is why this is 10 here (vs decoration.cpp's lea-at-start).
struct Sig {
	const char* pattern;
	int leaDispOffset;    // offset of the lea's disp32 within the match
	int nextInstrOffset;  // offset within the match of the instruction after the lea (rip base)
};

// LEGACY: `41 B0 01 | 48 8D 0D <disp32> | E8 ...` — `mov r8b,1`, then `lea rcx`, then a call.
constexpr Sig kLegacySig = { "41 B0 01 48 8D 0D ?? ?? ?? ?? E8", 6, 10 };
// ENHANCED: `45 31 C0 | 48 8D 3D <disp32> | 48 89 ...` — `xor r8d,r8d`, then `lea rdi`.
constexpr Sig kEnhancedSig = { "45 31 C0 48 8D 3D ?? ?? ?? ?? 48 89", 6, 10 };

// Resolved atArray address: { void* data; u16 count; u16 cap }. 0 until Init succeeds.
uintptr_t g_statsArray = 0;

// sStatData::SetIntData is vtable slot 2 on both editions (CustomLocalSaves gta/stat.hpp). This is
// the most fragile constant here — if a write doesn't take, re-derive it first.
constexpr int kSetIntVtIdx = 2;

bool Readable(const void* p, size_t n) {
	if (!p) return false;
	MEMORY_BASIC_INFORMATION mbi;
	if (VirtualQuery(p, &mbi, sizeof(mbi)) == 0) return false;
	if (mbi.State != MEM_COMMIT) return false;
	const DWORD bad = PAGE_NOACCESS | PAGE_GUARD;
	if (mbi.Protect & bad) return false;
	// The query covers [BaseAddress, BaseAddress+RegionSize); ensure the whole span fits.
	auto start = reinterpret_cast<uintptr_t>(mbi.BaseAddress);
	auto end = start + mbi.RegionSize;
	auto want = reinterpret_cast<uintptr_t>(p) + n;
	return want <= end;
}

// The atArray fields. data = *(array+0); size = *(u16*)(array+8), capacity = *(u16*)(array+0xA) —
// the classic rage::atArray<T> { T*; u16 size; u16 capacity } (manager dump: size 0x9CA5=40101,
// cap 0x9CB0=40144 packed in the one qword at +0x10). Reading +8 as u32 wrongly fused size+cap.
void** ArrayData() { return reinterpret_cast<void**>(*reinterpret_cast<uintptr_t*>(g_statsArray)); }
uint32_t ArrayCount() { return *reinterpret_cast<uint16_t*>(g_statsArray + 8); }

// A tiny hash->object cache. The stats table holds ~40k entries, so a linear walk is expensive to
// run every frame for every pinned skill; but a stat's sStatData object is allocated once and never
// moves, so the first lookup's result stays valid for the session. Small fixed cache (only a handful
// of skills are ever pinned) — no eviction needed.
struct CacheEntry { uint32_t hash; void* obj; };
constexpr int kCacheSize = 16;
CacheEntry g_cache[kCacheSize] = {};
int g_cacheCount = 0;

void* CacheGet(uint32_t hash) {
	for (int i = 0; i < g_cacheCount; ++i)
		if (g_cache[i].hash == hash) return g_cache[i].obj;
	return nullptr;
}

void CachePut(uint32_t hash, void* obj) {
	if (g_cacheCount < kCacheSize)
		g_cache[g_cacheCount++] = { hash, obj };
}

// Find a stat's sStatData* by hash. Cached after the first hit (the object is stable for the
// session); otherwise a linear walk of the 0x10-stride entries (hash@0x00, pointer@0x08 on both
// editions). nullptr if unresolved / not found / memory unreadable.
void* GetStatData(uint32_t hash) {
	if (!g_statsArray) return nullptr;
	if (void* hit = CacheGet(hash)) return hit;
	if (!Readable(reinterpret_cast<void*>(g_statsArray), 0x10)) return nullptr;
	auto* base = reinterpret_cast<uint8_t*>(ArrayData());
	uint32_t count = ArrayCount();
	// Sanity: the full SP+MP stats table is large (~40k entries on Legacy b3788). Cap generously to
	// reject a misread header, but well above the real count — an earlier 0x4000 ceiling wrongly
	// rejected the legitimate ~40101.
	if (!base || count == 0 || count > 0x20000) return nullptr;
	if (!Readable(base, static_cast<size_t>(count) * 0x10)) return nullptr;
	for (uint32_t i = 0; i < count; ++i) {
		uint8_t* entry = base + static_cast<size_t>(i) * 0x10;
		uint32_t h = *reinterpret_cast<uint32_t*>(entry);
		if (h == hash) {
			void* obj = *reinterpret_cast<void**>(entry + 0x08);
			CachePut(hash, obj);
			return obj;
		}
	}
	return nullptr;
}

// Call sStatData::SetInt(value) through the vtable. The object is `this`; vtable is at *obj.
using SetIntFn = void (*)(void* self, int value);

} // namespace

namespace Stats {

bool Init() {
	if (g_statsArray) return true;
	const Sig& sig = BuildEdition::IsLegacy() ? kLegacySig : kEnhancedSig;
	uint8_t* hit = Pattern::Scan(sig.pattern);
	if (!hit) {
		Logger::Logf("Stats: %s 'All Stats Array' pattern not found — skill memory-write unavailable.",
		             BuildEdition::Name());
		return false;
	}
	int32_t disp = *reinterpret_cast<int32_t*>(hit + sig.leaDispOffset);
	// The lea loads &CStatsMgr (its m_Initialized@0x00); the rip base is the instruction AFTER the
	// lea, then +0x08 steps past m_Initialized to the embedded atArray (CustomLocalSaves' +6 rip +8).
	uintptr_t mgr = reinterpret_cast<uintptr_t>(hit + sig.nextInstrOffset + disp);
	g_statsArray = mgr + 0x08;
	Logger::Logf("Stats: %s stats array @ %p (pattern @ %p, count=%u).",
	             BuildEdition::Name(), (void*)g_statsArray, (void*)hit,
	             Readable(reinterpret_cast<void*>(g_statsArray), 0x10) ? ArrayCount() : 0);
	return true;
}

bool WriteInt(uint32_t hash, int value) {
	void* obj = GetStatData(hash);
	if (!obj || !Readable(obj, 0x10)) return false;
	void** vtable = *reinterpret_cast<void***>(obj);
	if (!Readable(vtable, (kSetIntVtIdx + 1) * sizeof(void*))) return false;
	auto setInt = reinterpret_cast<SetIntFn>(vtable[kSetIntVtIdx]);
	setInt(obj, value);
	return true;
}

} // namespace Stats
