#pragma once

#include <windows.h>
#include <string>

// Tiny file logger. Writes next to the loaded .asi, truncates on Init, and
// swallows its own IO failures — a locked/unwritable log must never crash the game.
namespace Logger {

// Resolve the log path from the module and truncate it. Call once on attach.
void Init(HMODULE module);

// The %APPDATA% data directory this plugin writes into (log, wallet store). Empty if
// the path couldn't be resolved. Available after Init. Other modules build file paths
// from this so the writable-location rule lives in one place.
const std::string& DataDir();

void Log(const std::string& line);

// printf-style convenience.
void Logf(const char* fmt, ...);

// Verbose diagnostics, tagged [DEBUG]. The CALLER decides whether to emit (the shim gates
// on the shared logLevel), so this always writes when called.
void Debugf(const char* fmt, ...);

} // namespace Logger
