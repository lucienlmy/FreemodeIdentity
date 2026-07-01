using System;
using System.IO;

namespace FreemodeIdentity {
	// Resolves the mod's writable data directory.
	//
	// Runtime files (log, config, appearance slots, wallet store) MUST NOT live anywhere
	// under the game folder. GTA V Enhanced's native ScriptHookV host opens every file
	// present under the game directory at launch — game root AND scripts\ AND its
	// subfolders — with a no-write-share, no-delete, no-rename lock held for the whole
	// session. So any file that already exists at launch can never be rewritten, deleted,
	// or replaced (the long-standing "log won't update / settings don't persist" bug).
	// Proven with handle64 (GTA5_Enhanced.exe owns the handles) + live write-probes: only
	// files that did NOT exist at launch are writable, and even the game root's own
	// ScriptHookV.log is locked. A subfolder under scripts\ does NOT escape this.
	//
	// The fix is to write OUTSIDE the game tree entirely: %APPDATA%\GTA V Mods\
	// KernelPryanic\FreemodeIdentity\. The host never scans there, so fixed filenames stay
	// writable every session with normal permissions. The native spend-shim writes its own
	// log into this SAME folder, so both halves of the mod share one location. Init() is
	// kept for API compatibility but the data dir no longer depends on the DLL location.
	public static class ScriptPaths {
		// Group all runtime data under %APPDATA%\GTA V Mods\KernelPryanic\ so this author's
		// GTA V mods share one tree instead of scattering top-level folders.
		const string GameFolderName = "GTA V Mods";
		const string VendorFolderName = "KernelPryanic";
		// This mod's own subfolder under the shared parent.
		const string DataFolderName = "FreemodeIdentity";

		// The DLL folder (scripts\), for diagnostics only — never a write target.
		static string directory = AppDomain.CurrentDomain.BaseDirectory ?? ".";
		public static string Directory => directory;

		// The mod's writable data folder: %APPDATA%\GTA V Mods\KernelPryanic\FreemodeIdentity\.
		// All runtime writes (log, config, slots, wallet store) route through here, outside
		// the game's lock scope. Created on first access.
		public static string DataDirectory {
			get {
				string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
				string path = Path.Combine(appData, GameFolderName, VendorFolderName, DataFolderName);
				if (!System.IO.Directory.Exists(path)) {
					try {
						System.IO.Directory.CreateDirectory(path);
					} catch {
						// If creation fails, callers' own IO guards handle the fallout;
						// never crash path resolution.
					}
				}
				return path;
			}
		}

		// Kept for call-site compatibility (the Script ctor still passes BaseDirectory).
		// Records the DLL folder for diagnostics; the data dir is %APPDATA%, independent
		// of where the DLL was loaded from.
		public static void Init(string baseDirectory) {
			if (!string.IsNullOrEmpty(baseDirectory)) {
				directory = baseDirectory;
			}
		}

		// Resolve a runtime file inside the writable data folder.
		public static string For(string fileName) => Path.Combine(DataDirectory, fileName);
	}

	// Ordered by severity: a line writes only when its level is at least the threshold.
	public enum LogLevel { Debug, Info, Error }

	public static class Logger {
		// Resolved on each use, not cached: ScriptPaths.Directory is finalized only after
		// the Script constructor calls ScriptPaths.Init, which runs after this type is first
		// touched. Caching here would freeze the pre-Init fallback path.
		static string LogFilePath => ScriptPaths.For("FreemodeIdentity.log");

		// Lowest level that gets written. Info by default; the ini's [Logging] Level can
		// drop it to Debug to include the verbose diagnostics (and the shim's STAT trace).
		public static LogLevel Threshold { get; set; } = LogLevel.Info;

		public static void ClearLog() {
			try {
				File.WriteAllText(LogFilePath, string.Empty);
			} catch {
				// Logging must never crash the script.
			}
		}

		public static void LogDebug(object message) => Write(LogLevel.Debug, message);
		public static void Log(object message) => Write(LogLevel.Info, message);
		public static void LogError(object message) => Write(LogLevel.Error, message);

		// Logged at INFO but forced past Threshold — for once-per-session triage lines
		// (version, resolved config) that must appear even when the level is raised to
		// Error, without masquerading as errors.
		public static void LogBanner(object message) => Write(LogLevel.Info, message, force: true);

		static void Write(LogLevel level, object message, bool force = false) {
			if (!force && level < Threshold) return;
			try {
				File.AppendAllText(LogFilePath, DateTime.Now + " [" + level.ToString().ToUpperInvariant() + "] " + message + Environment.NewLine);
			} catch {
				// Logging must never crash the script.
			}
		}
	}
}
