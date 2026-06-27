# Freemode Identity - build / lint / test / package (hybrid: C# SHVDN3 + native .asi)
#
# Conventions mirror the workspace AGENTS.md: `make build`, `make lint`, `make test`
# are the entry points; `make package` produces an upload-ready zip. SHVDN3 C# mod, so
# the C# build is MSBuild; no test runner (the game is the integration test). The native
# spend-shim (.asi) builds via CMake + MSVC under native/.
#
# NOTE on names: both halves carry the mod name — FreemodeIdentity.dll (C#) and
# FreemodeIdentity.asi (native). The .asi filename, the C++ export symbol
# (FreemodeIdentity_GetState) and ShimBridge.AsiName form a lockstep trio: rename one
# and the shim silently won't connect, so keep all three matched.
#
# MSBuild is discovered via vswhere; override if needed:
#   make build MSBUILD="/c/path/to/MSBuild.exe"

PROJECT     := FreemodeIdentity.csproj
CONFIG      ?= Release
PLATFORM    ?= x64

DLL         := FreemodeIdentity.dll
ASI         := FreemodeIdentity.asi
MOD_NAME    := Freemode Identity
DIST        := dist
STAGE       := $(DIST)
SCRIPTS     := $(DIST)/scripts
ZIP         := $(DIST)/FreemodeIdentity.zip

# Native spend-shim (.asi) — CMake + MSVC, built under native/build. Its output drops
# into the game ROOT (next to ScriptHookV.dll), not scripts/.
NATIVE_DIR  := native
# CMake generator for the native shim. Default to the VS multi-config generator (local
# dev); CI overrides with Ninja (NATIVE_GEN=Ninja), which builds via the MSVC dev env on
# PATH and avoids the VS-instance discovery that fails on hosted runners. Ninja is
# single-config, so the .asi lands at build/ not build/Release/ — track that per generator.
NATIVE_GEN  ?= Visual Studio 17 2022
ifeq ($(NATIVE_GEN),Ninja)
NATIVE_ASI  := $(NATIVE_DIR)/build/$(ASI)
# Force MSVC cl (not a stray MinGW gcc that may be on PATH) — the sources use MSVC-only
# flags (/W4, /permissive-). On CI the MSVC dev env (vcvars) puts cl on PATH.
NATIVE_CFG  := -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_COMPILER=cl -DCMAKE_CXX_COMPILER=cl
else
NATIVE_ASI  := $(NATIVE_DIR)/build/Release/$(ASI)
NATIVE_CFG  := -A x64
endif

VERSION     := $(shell sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$(PROJECT)" | head -1 | sed 's:\.0$$::')

# Assemblies the player already has from their ScriptHookV .NET install — referenced
# (SpecificVersion=False) but never bundled.
PLAYER_PROVIDED := LemonUI.dll LemonUI.SHVDN3.dll ScriptHookVDotNet3.dll

VSWHERE := /c/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe
MSBUILD ?= $(shell "$(VSWHERE)" -latest -requires Microsoft.Component.MSBuild \
             -find 'MSBuild\**\Bin\MSBuild.exe' 2>/dev/null | head -1)

MSB := "$(MSBUILD)" "$(PROJECT)" -nologo -v:minimal \
       -p:Configuration=$(CONFIG) -p:Platform=$(PLATFORM)

.DEFAULT_GOAL := build
.PHONY: build native lint test package clean rebuild help

build: ## compile the C# mod (Release x64) to bin/
	@test -n "$(MSBUILD)" || { echo "MSBuild not found; pass MSBUILD=/path/to/MSBuild.exe"; exit 1; }
	$(MSB) -t:Build

native: ## build the native spend-shim (.asi) via CMake, print its md5
	cmake -S "$(NATIVE_DIR)" -B "$(NATIVE_DIR)/build" -G "$(NATIVE_GEN)" $(NATIVE_CFG)
	cmake --build "$(NATIVE_DIR)/build" --config Release
	@test -f "$(NATIVE_ASI)" || { echo "native build missing $(ASI)"; exit 1; }
	@powershell -NoProfile -Command "(Get-FileHash '$(NATIVE_ASI)' -Algorithm MD5).Hash.ToLower() + '  $(ASI)'"

lint: ## static check (a clean compile is the static check for C#)
	@test -n "$(MSBUILD)" || { echo "MSBuild not found; pass MSBUILD=/path/to/MSBuild.exe"; exit 1; }
	$(MSB) -t:Rebuild -p:WarningLevel=4

test: ## no automated tests - the game is the integration test
	@echo "No test runner: copy bin/$(CONFIG)/$(DLL) into the game's scripts/ folder and"
	@echo "the native $(ASI) into the game root, then reload scripts in-game. To test:"
	@echo "  1. As a freemode (MP) character, open the menu (Shift+X by default)."
	@echo "  2. Save/apply an appearance slot; confirm auto-apply on load restores it."
	@echo "  3. Enable the wallet, pick a spoof target, toggle Spoofing to shop as them."
	@echo "  4. Collect street cash — the wallet balance rises by the real amount."
	@echo "  5. Buy in a shop — it charges the wallet, not the protagonist's real cash."

rebuild: ## clean then build
	@test -n "$(MSBUILD)" || { echo "MSBuild not found; pass MSBUILD=/path/to/MSBuild.exe"; exit 1; }
	$(MSB) -t:Rebuild

package: build native ## build BOTH halves, then zip a deploy-ready archive into dist/
	@test -n "$(VERSION)" || { echo "could not read <Version> from $(PROJECT)"; exit 1; }
	@rm -rf "$(SCRIPTS)" "$(STAGE)/gta5mod.json" "$(STAGE)/$(ASI)" "$(ZIP)"
	@mkdir -p "$(SCRIPTS)"
	@# The C# mod DLL goes in scripts/ (plus any non-player-provided dep it emitted).
	@for dll in bin/$(CONFIG)/*.dll; do \
		name=$$(basename "$$dll"); \
		case " $(PLAYER_PROVIDED) " in *" $$name "*) continue;; esac; \
		cp "$$dll" "$(SCRIPTS)/"; \
	done
	@test -f "$(SCRIPTS)/$(DLL)" || { echo "build output missing $(DLL)"; exit 1; }
	@# The native spend-shim .asi goes in the game ROOT (archive root, next to scripts/).
	@cp "$(NATIVE_ASI)" "$(STAGE)/$(ASI)"
	@printf '{\n  "name": "%s",\n  "version": "%s"\n}\n' "$(MOD_NAME)" "$(VERSION)" > "$(STAGE)/gta5mod.json"
	@powershell -NoProfile -Command "Compress-Archive -Path '$(DIST)/scripts','$(DIST)/$(ASI)','$(DIST)/gta5mod.json' -DestinationPath '$(ZIP)' -Force"
	@echo "packaged $(ZIP) (v$(VERSION)):"
	@powershell -NoProfile -Command "Add-Type -A System.IO.Compression.FileSystem; [IO.Compression.ZipFile]::OpenRead((Resolve-Path '$(ZIP)')).Entries | ForEach-Object { '  ' + \$$_.FullName }"

clean: ## remove build output (bin/, obj/, dist/, native/build/)
	@rm -rf bin obj $(DIST) $(NATIVE_DIR)/build
	@echo "cleaned bin/, obj/, $(DIST)/ and $(NATIVE_DIR)/build/"

help: ## list targets
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) | \
		awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-10s\033[0m %s\n", $$1, $$2}'
