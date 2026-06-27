using System;
using System.Globalization;
using System.IO;

namespace FreemodeIdentity {
	// The freemode wallet balance and its persistence. The balance can't live in any
	// in-game stat (MP0_WALLET_BALANCE is tied to an Online profile not loaded in story; the
	// SP cash global is reverted by the game scripts), so it's a plain int persisted to a
	// file in the mod's %APPDATA% data folder — the one place Enhanced doesn't lock.
	//
	// C# owns the balance (earning credits it, the menu/persistence read it). The native
	// spend-shim keeps a live mirror it reads inside the shop's STAT hook; PushToShim keeps
	// that mirror in sync (wired once the bridge lands).
	internal sealed class Wallet {
		const string StoreFileName = "wallet.dat";

		int balance;

		public int Balance => balance;

		static string StorePath => ScriptPaths.For(StoreFileName);

		// A missing file is the valid empty state ($0), not an error; a malformed file logs
		// and starts at $0. Never throws.
		public void Load() {
			try {
				if (File.Exists(StorePath)) {
					string text = File.ReadAllText(StorePath).Trim();
					if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= 0) {
						balance = v;
					} else {
						Logger.LogError("Wallet: store unreadable/invalid — starting at $0.");
					}
				}
			} catch (Exception ex) {
				Logger.LogError($"Wallet: load failed ({ex.GetType().Name}) — starting at $0.");
			}
			Logger.Log($"Wallet: loaded balance ${balance}.");
		}

		// Best-effort persist; a failed write must never crash the mod (the in-memory value
		// still works for the session).
		void Save() {
			try {
				File.WriteAllText(StorePath, balance.ToString(CultureInfo.InvariantCulture));
			} catch {
				// swallow — see Logger discipline
			}
		}

		// Credit earned cash (pickups). Persists.
		public void Add(int amount) {
			if (amount > 0) Apply(amount);
		}

		// Apply a signed change (the shim's per-event delta: a shop debit < 0, a script payout
		// > 0). Reported as a delta so it composes with a same-tick pickup credit instead of
		// overwriting it. Clamped to [0, int.MaxValue]; persists.
		public void Apply(int delta) {
			long next = (long)balance + delta;
			if (next < 0) next = 0;
			if (next > int.MaxValue) next = int.MaxValue;
			if ((int)next == balance) return;
			balance = (int)next;
			Save();
		}
	}
}
