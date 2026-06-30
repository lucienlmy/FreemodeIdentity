using System;
using System.Runtime.InteropServices;

namespace FreemodeIdentity {
	// Which GTA V edition we're running under. The mod ships ONE binary that runs on both;
	// this is the single switch every version-pinned constant routes through (offset tables
	// below), so build-specific knowledge lives in exactly one place.
	internal enum Edition { Enhanced, Legacy }

	// Detects the running edition once at startup and exposes the per-edition constants the
	// rest of the mod reads. Almost everything is build-independent (stable native hashes,
	// SP cash stat hashes, content-located memory reads); only the handful of pinned offsets
	// and script globals below differ, and only those belong here.
	//
	// Detection is by HOST MODULE NAME (GTA5_Enhanced.exe vs GTA5.exe) — the same signal
	// ScriptPaths reasons about, and independent of any SHVDN version enum (which itself
	// differs between the Enhanced and Legacy SHVDN builds). An ini override ([General] Build)
	// forces a value if detection is ever wrong.
	internal static class GameBuild {
		[DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
		static extern IntPtr GetModuleHandle(string name);

		static bool resolved;
		static Edition edition;

		// The detected (or overridden) edition. Resolved lazily on first read; Configure can
		// pin it ahead of that from the ini override.
		public static Edition Current {
			get {
				if (!resolved) {
					edition = Detect();
					resolved = true;
				}
				return edition;
			}
		}

		public static bool IsEnhanced => Current == Edition.Enhanced;
		public static bool IsLegacy => Current == Edition.Legacy;

		// Apply the optional ini override ([General] Build = Enhanced|Legacy). "Auto" (or any
		// unrecognised value) keeps auto-detection. Call before any constant is read so the
		// override wins over the lazy detect. Returns the resolved edition for the startup log.
		public static Edition Configure(string overrideValue) {
			if (!string.IsNullOrWhiteSpace(overrideValue)) {
				string v = overrideValue.Trim();
				if (v.Equals("Enhanced", StringComparison.OrdinalIgnoreCase)) {
					edition = Edition.Enhanced;
					resolved = true;
					Logger.LogBanner("GameBuild: edition forced to Enhanced by ini override.");
					return edition;
				}
				if (v.Equals("Legacy", StringComparison.OrdinalIgnoreCase)) {
					edition = Edition.Legacy;
					resolved = true;
					Logger.LogBanner("GameBuild: edition forced to Legacy by ini override.");
					return edition;
				}
			}
			edition = Detect();
			resolved = true;
			return edition;
		}

		// Enhanced ships as GTA5_Enhanced.exe; Legacy as GTA5.exe. The Enhanced module is the
		// reliable positive tell — its absence means Legacy (the long-standing classic exe).
		static Edition Detect() {
			Edition e = GetModuleHandle("GTA5_Enhanced.exe") != IntPtr.Zero ? Edition.Enhanced : Edition.Legacy;
			Logger.LogBanner($"GameBuild: detected {e} edition (by host module name).");
			return e;
		}

		// --- Per-edition constants ----------------------------------------------------------
		// Only values that genuinely differ between builds live here. Stable native hashes, the
		// SP{N}_TOTAL_CASH stat hashes, and all content-located reads are build-independent and
		// stay at their use sites.

		// SP active-character index global (spoof lever 2). Not cosmetic after all: the gun shop
		// reads weapon ownership from a per-protagonist stat keyed on THIS index, so without the
		// write Ammu-Nation reads the wrong slot and re-charges for weapons you own (cash dodges it
		// — that's model-hash keyed via the shim, a separate path). Enhanced: global 21627 (0x547B).
		// Legacy: global 1574927, the same selector read by gunclub_shop / player_controller /
		// main_persistent (verified b3570). Legacy global IDs can renumber across point-releases, so
		// the spoof logs the value it reads here — a sane 0/1/2 confirms the address on a given build.
		public static int CharIndexGlobal => IsEnhanced ? 0x547B : 1574927;

		// CPedIntelligence pointer offset within CPed. Verified IDENTICAL on Enhanced (1013.34)
		// and Legacy (b3258): 0x10A0. Kept here so it has one home and the parity is documented.
		public const int PedIntelligenceOffset = 0x10A0;

		// CPed decoration buffer-index offset (uint16 indexing the global PedEntry array).
		// 0x2E8 on BOTH Enhanced and Legacy (b3788 probed to the same value). The array BASE is
		// resolved by pattern per build (Legacy: FiveM global-ptr; Enhanced: the imul-by-0x7D8
		// signature — both in the native shim / DecorationBaseScan), but this index offset is shared.
		public static int PedDecorationBufferIndexOffset => 0x2E8;

		// Whether tattoo/decoration capture AND apply are supported. True on both builds (the base
		// resolves via pattern on each). Kept as a single flag so a future build that can't resolve
		// either way can cleanly disable decorations again.
		public static bool DecorationsSupported => true;
	}
}
