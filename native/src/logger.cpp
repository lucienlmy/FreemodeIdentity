#include "logger.hpp"

#include <shlobj.h>

#include <cstdarg>
#include <cstdio>
#include <ctime>
#include <mutex>

#pragma comment(lib, "shell32.lib")

namespace {
std::string g_path;
std::string g_dir;
std::mutex g_mutex; // the STAT hook runs on the game thread; ScriptMain on ours.

std::string Timestamp() {
	SYSTEMTIME st;
	GetLocalTime(&st);
	char buf[32];
	// WHY local wall-clock: cross-referenced by hand against in-game actions.
	std::snprintf(buf, sizeof(buf), "%02d:%02d:%02d.%03d",
	              st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
	return buf;
}
} // namespace

namespace Logger {

void Init(HMODULE /*module*/) {
	// Write OUTSIDE the game folder. GTA V Enhanced's host opens every file that
	// exists under the game tree at launch with a no-write-share lock held for the
	// whole session — so a log beside the .asi takes the startup line, then every
	// later append silently fails (proven across this author's SHVDN mods). %APPDATA%
	// is never scanned by the host, so a fixed filename there stays writable.
	char appData[MAX_PATH] = {0};
	if (FAILED(SHGetFolderPathA(nullptr, CSIDL_APPDATA, nullptr, 0, appData)))
		return;

	// Shared vendor parent (matches the author's other mods), then this mod's dir —
	// the SAME folder the C# side writes into, so both halves share one location.
	g_dir = std::string(appData) + "\\GTA V Mods\\KernelPryanic\\FreemodeIdentity";
	SHCreateDirectoryExA(nullptr, g_dir.c_str(), nullptr); // makes the nested path
	// Distinct from the C# mod's FreemodeIdentity.log — both write into this same folder.
	g_path = g_dir + "\\FreemodeIdentity.shim.log";

	// Truncate on startup so each session's log stands alone.
	if (FILE* f = std::fopen(g_path.c_str(), "w")) {
		std::fputs("FreemodeIdentity shim log\n", f);
		std::fclose(f);
	}
}

const std::string& DataDir() { return g_dir; }

void Log(const std::string& line) {
	if (g_path.empty())
		return;
	std::lock_guard<std::mutex> lock(g_mutex);
	if (FILE* f = std::fopen(g_path.c_str(), "a")) {
		std::fprintf(f, "[%s] %s\n", Timestamp().c_str(), line.c_str());
		std::fclose(f);
	}
}

void Logf(const char* fmt, ...) {
	char buf[1024];
	va_list args;
	va_start(args, fmt);
	std::vsnprintf(buf, sizeof(buf), fmt, args);
	va_end(args);
	Log(buf);
}

void Debugf(const char* fmt, ...) {
	char buf[1024];
	va_list args;
	va_start(args, fmt);
	std::vsnprintf(buf, sizeof(buf), fmt, args);
	va_end(args);
	if (g_path.empty())
		return;
	std::lock_guard<std::mutex> lock(g_mutex);
	if (FILE* f = std::fopen(g_path.c_str(), "a")) {
		std::fprintf(f, "[%s] [DEBUG] %s\n", Timestamp().c_str(), buf);
		std::fclose(f);
	}
}

} // namespace Logger
