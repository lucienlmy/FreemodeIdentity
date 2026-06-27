using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;

namespace FreemodeIdentity {
	// Multi-slot store: one AppearanceData persisted PER slot as its own XML file under
	// an Appearances\ subfolder of the data dir (Appearances\<name>.xml). Each save
	// rewrites only that one slot's small file, never the whole set. XmlSerializer is a
	// framework built-in (System.Xml.Serialization), so the mod ships as one DLL with no
	// third-party JSON dependency.
	//
	// Slots are keyed by AppearanceData.Name (case-insensitive). The filename is the
	// sanitized name; a name that collides on disk after sanitization gets a numeric
	// suffix so two distinct slots never share a file.
	//
	// Persistence tolerates first run and corruption: a missing folder is the valid empty
	// state, a corrupt per-slot file is logged and skipped (the rest still load). Reads
	// never throw. An explicit Loaded flag tracks load-state so a genuinely-empty store
	// doesn't re-scan the disk on every call.
	public static class XmlAppearanceStorage {
		static string BasePath = string.Empty;
		const string AppearancesFolder = "Appearances";
		static readonly XmlSerializer Serializer = new XmlSerializer(typeof(AppearanceData));

		// Keyed by slot Name (case-insensitive). A dictionary keeps Get(name) O(1) and
		// makes "rewrite just this one" the natural operation. Tracks the file each slot
		// loaded from so rename/delete can remove the old file even if the name's
		// sanitized stem would differ.
		static Dictionary<string, AppearanceData> Cache =
			new Dictionary<string, AppearanceData>(StringComparer.OrdinalIgnoreCase);
		static Dictionary<string, string> FileByName =
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		static bool Loaded;

		public static void Initialize(string path) {
			if (!Directory.Exists(path)) {
				Directory.CreateDirectory(path);
			}
			BasePath = path;
			// Reset load state so a re-initialization (e.g. script reload) re-reads from
			// the new path instead of serving a stale cache.
			Loaded = false;
			EnsureLoaded();
		}

		static void EnsureLoaded() {
			if (Loaded) {
				return;
			}

			Cache = new Dictionary<string, AppearanceData>(StringComparer.OrdinalIgnoreCase);
			FileByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

			string dir = AppearancesDir();
			if (Directory.Exists(dir)) {
				foreach (string file in Directory.GetFiles(dir, "*.xml")) {
					// Skip per-slot backups (<name>.bak.xml) — they're applied on demand, never
					// loaded as slots of their own (they'd show up as a phantom "<name>.bak" slot).
					if (file.EndsWith(".bak.xml", StringComparison.OrdinalIgnoreCase)) {
						continue;
					}
					AppearanceData ad = ReadFile(file);
					if (ad == null) {
						continue;
					}
					// A file with no Name (hand-edited, or a future-proofing miss) is named
					// from its filename so it is still addressable and not silently dropped.
					if (string.IsNullOrWhiteSpace(ad.Name)) {
						ad.Name = Path.GetFileNameWithoutExtension(file);
					}
					if (!Cache.ContainsKey(ad.Name)) {
						Cache[ad.Name] = ad;
						FileByName[ad.Name] = file;
					}
				}
			}

			Loaded = true;
		}

		// All saved slots, ordered by name for a stable menu.
		public static List<AppearanceData> GetAll() {
			EnsureLoaded();
			return Cache.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
		}

		public static AppearanceData Get(string name) {
			EnsureLoaded();
			return name != null && Cache.TryGetValue(name, out AppearanceData ad) ? ad : null;
		}

		public static bool Exists(string name) {
			EnsureLoaded();
			return name != null && Cache.ContainsKey(name);
		}

		public static int Count() {
			EnsureLoaded();
			return Cache.Count;
		}

		// Save a slot under ad.Name, overwriting an existing slot of the same name. The
		// caller sets ad.Name before calling.
		public static void Save(AppearanceData ad) {
			ThrowWhenUninitialized();
			EnsureLoaded();
			if (ad == null || string.IsNullOrWhiteSpace(ad.Name)) {
				throw new ArgumentException("Appearance slot needs a non-empty Name.");
			}
			Cache[ad.Name] = ad;
			// Reuse the slot's existing file if it already had one (overwrite in place);
			// otherwise pick a fresh, collision-free path from the name.
			if (!FileByName.TryGetValue(ad.Name, out string file)) {
				file = UniqueFilePathFor(ad.Name);
				FileByName[ad.Name] = file;
			}
			WriteFile(ad, file);
		}

		public static void Delete(string name) {
			ThrowWhenUninitialized();
			EnsureLoaded();
			if (name == null || !Cache.ContainsKey(name)) {
				return;
			}
			Cache.Remove(name);
			if (FileByName.TryGetValue(name, out string file)) {
				FileByName.Remove(name);
				if (File.Exists(file)) {
					File.Delete(file);
				}
				// Take the slot's backup with it — a stale .bak with no slot is just litter.
				string bak = BackupPathFor(file);
				if (File.Exists(bak)) {
					File.Delete(bak);
				}
			}
		}

		// Copy the slot's CURRENT saved data into its single backup file (<slotfile>.bak.xml),
		// overwriting any previous backup. A manual safety net to take before overwriting a slot.
		// Returns false if the slot isn't on disk yet (nothing to back up).
		public static bool Backup(string name) {
			ThrowWhenUninitialized();
			EnsureLoaded();
			if (name == null || !FileByName.TryGetValue(name, out string file) || !File.Exists(file)) {
				return false;
			}
			File.Copy(file, BackupPathFor(file), overwrite: true);
			return true;
		}

		public static bool HasBackup(string name) {
			EnsureLoaded();
			return name != null && FileByName.TryGetValue(name, out string file) && File.Exists(BackupPathFor(file));
		}

		// The slot's backed-up appearance, or null if it has no backup. Read fresh from the
		// .bak file (it isn't held in the cache — only live slots are).
		public static AppearanceData GetBackup(string name) {
			EnsureLoaded();
			if (name == null || !FileByName.TryGetValue(name, out string file)) {
				return null;
			}
			string bak = BackupPathFor(file);
			return File.Exists(bak) ? ReadFile(bak) : null;
		}

		// A slot file's backup path: same path with .bak before the extension (Foo.xml -> Foo.bak.xml).
		// EnsureLoaded skips *.bak.xml so a backup never loads as a slot of its own.
		static string BackupPathFor(string slotFile) {
			string dir = Path.GetDirectoryName(slotFile);
			string stem = Path.GetFileNameWithoutExtension(slotFile);
			return Path.Combine(dir, stem + ".bak.xml");
		}

		// Rename a slot in place: writes the new file, drops the old one. No-op if the
		// slot doesn't exist or the new name already belongs to a DIFFERENT slot.
		public static bool Rename(string oldName, string newName) {
			ThrowWhenUninitialized();
			EnsureLoaded();
			if (oldName == null || newName == null || string.IsNullOrWhiteSpace(newName)) {
				return false;
			}
			if (!Cache.TryGetValue(oldName, out AppearanceData ad)) {
				return false;
			}
			if (Cache.ContainsKey(newName) && !string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase)) {
				return false; // refuse to clobber a different slot
			}
			string oldFile = FileByName.TryGetValue(oldName, out string f) ? f : null;
			Cache.Remove(oldName);
			FileByName.Remove(oldName);
			ad.Name = newName;
			Cache[newName] = ad;
			string newFile = UniqueFilePathFor(newName);
			FileByName[newName] = newFile;
			WriteFile(ad, newFile);
			if (oldFile != null && !string.Equals(oldFile, newFile, StringComparison.OrdinalIgnoreCase) && File.Exists(oldFile)) {
				// Carry the slot's backup across to the new name, then drop the old slot file.
				string oldBak = BackupPathFor(oldFile);
				if (File.Exists(oldBak)) {
					File.Copy(oldBak, BackupPathFor(newFile), overwrite: true);
					File.Delete(oldBak);
				}
				File.Delete(oldFile);
			}
			return true;
		}

		// Read one per-slot file, returning null on a missing/corrupt file so one bad
		// entry never aborts loading the rest.
		static AppearanceData ReadFile(string file) {
			try {
				using (var reader = new StreamReader(file)) {
					return (AppearanceData)Serializer.Deserialize(reader);
				}
			} catch (Exception e) {
				Logger.LogError($"Failed to read {file}: {e}");
				return null;
			}
		}

		static void WriteFile(AppearanceData ad, string file) {
			string dir = AppearancesDir();
			if (!Directory.Exists(dir)) {
				Directory.CreateDirectory(dir);
			}
			using (var writer = new StreamWriter(file)) {
				Serializer.Serialize(writer, ad);
			}
		}

		// A filesystem-safe stem from a user-chosen slot name. Strips path-invalid chars;
		// an all-invalid name falls back to "slot".
		static string Sanitize(string name) {
			var sb = new StringBuilder(name.Length);
			foreach (char c in name) {
				sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
			}
			string s = sb.ToString().Trim().Trim('.');
			return string.IsNullOrEmpty(s) ? "slot" : s;
		}

		// A path under Appearances\ that no current slot file already uses. Two slots whose
		// names sanitize to the same stem (e.g. "A/B" and "A?B") get a numeric suffix so
		// they never collide on disk.
		static string UniqueFilePathFor(string name) {
			string stem = Sanitize(name);
			var taken = new HashSet<string>(FileByName.Values, StringComparer.OrdinalIgnoreCase);
			string candidate = Path.Combine(AppearancesDir(), stem + ".xml");
			int n = 2;
			while (taken.Contains(candidate) || File.Exists(candidate)) {
				candidate = Path.Combine(AppearancesDir(), $"{stem}_{n}.xml");
				n++;
			}
			return candidate;
		}

		static string AppearancesDir() => Path.Combine(BasePath, AppearancesFolder);

		static void ThrowWhenUninitialized() {
			if (BasePath == string.Empty) {
				throw new BasePathNotInitialized();
			}
		}

		public class BasePathNotInitialized : Exception { }
	}
}
