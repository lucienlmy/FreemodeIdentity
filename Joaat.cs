using System.Text;

namespace FreemodeIdentity {
	// Jenkins-one-at-a-time string hash, exactly as RAGE computes it (atStringHash):
	// lowercase ASCII + backslash→slash normalization, then the joaat rounds, then the
	// three-shift finalize. The same algorithm serves both call sites: clip-set /
	// animation-set names (the appearance side — fwClipSetManager keys fwClipSet by this
	// value, and the source string is NOT kept at runtime, so the only way back from a
	// stored hash to a usable name is to hash candidate names and match), and the
	// protagonist model / pickup-type / stat name hashes (the wallet + spoof side).
	// Mirrors SHVDN's StringHash.AtStringHash (verified against the cloned library source).
	static class Joaat {
		// Maps ASCII uppercase → lowercase and '\\' → '/'; identity elsewhere. Same table
		// the game uses, so our hashes match the engine's.
		static byte Normalize(byte c) {
			if (c >= 0x41 && c <= 0x5A) return (byte)(c + 0x20); // A-Z → a-z
			if (c == 0x5C) return 0x2F;                          // '\' → '/'
			return c;
		}

		public static uint Hash(string input) {
			if (string.IsNullOrEmpty(input)) {
				return 0;
			}
			uint hash = 0;
			foreach (byte b in Encoding.ASCII.GetBytes(input)) {
				hash += Normalize(b);
				hash += hash << 10;
				hash ^= hash >> 6;
			}
			hash += hash << 3;
			hash ^= hash >> 11;
			hash += hash << 15;
			return hash;
		}
	}
}
