#include "pattern.hpp"

#include <windows.h>
#include <psapi.h>
#include <vector>

#pragma comment(lib, "psapi.lib")

namespace {

struct PatternByte {
	uint8_t value;
	bool wildcard;
};

// Parse "EB 2A ? 48" → bytes, with "?"/"??" marking a wildcard.
std::vector<PatternByte> Parse(const char* sig) {
	std::vector<PatternByte> out;
	for (const char* p = sig; *p;) {
		if (*p == ' ') {
			++p;
			continue;
		}
		if (*p == '?') {
			out.push_back({0, true});
			++p;
			if (*p == '?')
				++p; // accept "??"
			continue;
		}
		auto hex = [](char c) -> int {
			if (c >= '0' && c <= '9') return c - '0';
			if (c >= 'a' && c <= 'f') return c - 'a' + 10;
			if (c >= 'A' && c <= 'F') return c - 'A' + 10;
			return -1;
		};
		int hi = hex(p[0]);
		int lo = hex(p[1]);
		if (hi < 0 || lo < 0)
			break; // malformed — stop parsing
		out.push_back({static_cast<uint8_t>((hi << 4) | lo), false});
		p += 2;
	}
	return out;
}

// Bounds of the main module's .text (executable) section.
bool GetTextSection(uint8_t*& begin, size_t& size) {
	HMODULE base = GetModuleHandleA(nullptr);
	if (!base)
		return false;

	auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
	if (dos->e_magic != IMAGE_DOS_SIGNATURE)
		return false;
	auto* nt = reinterpret_cast<IMAGE_NT_HEADERS*>(
	    reinterpret_cast<uint8_t*>(base) + dos->e_lfanew);
	if (nt->Signature != IMAGE_NT_SIGNATURE)
		return false;

	auto* section = IMAGE_FIRST_SECTION(nt);
	for (WORD i = 0; i < nt->FileHeader.NumberOfSections; ++i, ++section) {
		if (memcmp(section->Name, ".text", 5) == 0) {
			begin = reinterpret_cast<uint8_t*>(base) + section->VirtualAddress;
			size = section->Misc.VirtualSize;
			return true;
		}
	}
	return false;
}

} // namespace

namespace Pattern {

uint8_t* Scan(const char* signature) {
	std::vector<PatternByte> pat = Parse(signature);
	if (pat.empty())
		return nullptr;

	uint8_t* begin = nullptr;
	size_t size = 0;
	if (!GetTextSection(begin, size) || size < pat.size())
		return nullptr;

	const size_t last = size - pat.size();
	for (size_t i = 0; i <= last; ++i) {
		uint8_t* at = begin + i;
		bool hit = true;
		for (size_t j = 0; j < pat.size(); ++j) {
			if (!pat[j].wildcard && at[j] != pat[j].value) {
				hit = false;
				break;
			}
		}
		if (hit)
			return at;
	}
	return nullptr;
}

} // namespace Pattern
