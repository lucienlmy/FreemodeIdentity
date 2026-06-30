cmake_minimum_required(VERSION 3.20)

# FreemodeIdentity native spend-shim — a ScriptHookV plugin (.asi) that does ONLY the
# STAT_GET/SET_INT spend-redirect hook the C# mod can't do. The wallet balance + feature
# state live in C#; this shim shares them via an exported state block (shared_state.hpp).
project(FreemodeIdentityShim LANGUAGES C CXX)

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
	message(FATAL_ERROR "GTA V Enhanced is x64 only — configure with the x64 toolset.")
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# /MT — static CRT so the player needs no VC++ redist.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# --- MinHook (vendored) --------------------------------------------------------
add_library(minhook STATIC
	third_party/minhook/src/buffer.c
	third_party/minhook/src/hook.c
	third_party/minhook/src/trampoline.c
	third_party/minhook/src/hde/hde32.c
	third_party/minhook/src/hde/hde64.c
)
target_include_directories(minhook PUBLIC third_party/minhook/include)

# --- The shim (output renamed FreemodeIdentity.asi) ----------------------------
add_library(FreemodeIdentity SHARED
	src/dllmain.cpp
	src/logger.cpp
	src/pattern.cpp
	src/build_edition.cpp
	src/decoration.cpp
	src/waypoint.cpp
	src/stats.cpp
	src/natives.cpp
	src/natives_legacy.cpp
	src/wallet_hook.cpp
)

target_include_directories(FreemodeIdentity PRIVATE
	src
	third_party/ScriptHookV/inc
	third_party/minhook/include
)

target_link_libraries(FreemodeIdentity PRIVATE
	minhook
	"${CMAKE_SOURCE_DIR}/third_party/ScriptHookV/lib/ScriptHookV.lib"
)

target_compile_options(FreemodeIdentity PRIVATE /W4 /permissive- /wd4127 /wd4505)
target_compile_definitions(FreemodeIdentity PRIVATE _CRT_SECURE_NO_WARNINGS)

set_target_properties(FreemodeIdentity PROPERTIES
	OUTPUT_NAME "FreemodeIdentity"
	SUFFIX ".asi"
	PREFIX ""
)
