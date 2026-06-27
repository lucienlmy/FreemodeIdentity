using GTA;

namespace FreemodeIdentity {
	// The three story protagonists, by the game model that IS that character. The freemode
	// wallet's spend redirect targets one of these: the spoof makes a freemode ped read as
	// the chosen protagonist (so shops open and money resolves to that SPx), and the native
	// shim redirects that SPx's stat to our wallet.
	internal static class Identity {
		public const string Michael = "Michael";
		public const string Franklin = "Franklin";
		public const string Trevor = "Trevor";

		// Menu/scroll order.
		public static readonly string[] All = { Michael, Franklin, Trevor };

		public static string ModelName(string identity) {
			switch (identity) {
				case Michael: return "player_zero";
				case Franklin: return "player_one";
				case Trevor: return "player_two";
				default: return null;
			}
		}

		// The char-index (0/1/2) the SP cash/HUD systems key off — Michael=0, Franklin=1,
		// Trevor=2. The spoof writes this into the active-character global.
		public static int CharIndex(string identity) {
			switch (identity) {
				case Michael: return 0;
				case Franklin: return 1;
				case Trevor: return 2;
				default: return -1;
			}
		}

		// The SP{N}_TOTAL_CASH stat hash for this protagonist — the stat the shop charges
		// while spoofed to them, and the one the native shim redirects to our wallet.
		// 0 if not a protagonist (nothing to redirect).
		public static int WalletStat(string identity) {
			switch (identity) {
				case Michael: return unchecked((int)0x0324C31D); // SP0_TOTAL_CASH
				case Franklin: return unchecked((int)0x44BD6982); // SP1_TOTAL_CASH
				case Trevor: return unchecked((int)0x8D75047D); // SP2_TOTAL_CASH
				default: return 0;
			}
		}

		// Which identity the live player ped currently reads as, by model hash. A freemode
		// (or any non-protagonist) model is not one of the three.
		public static string Current() {
			Ped ped = Game.Player.Character;
			if (ped == null) return null;
			int h = ped.Model.Hash;
			if (h == new Model("player_zero").Hash) return Michael;
			if (h == new Model("player_one").Hash) return Franklin;
			if (h == new Model("player_two").Hash) return Trevor;
			return null; // freemode / other
		}
	}
}
