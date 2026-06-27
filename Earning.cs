using System;
using System.Collections.Generic;
using GTA;

namespace FreemodeIdentity {
	// Freemode earning. A freemode character earns NOTHING from cash pickups — the engine
	// credits the protagonist SP cash bucket, which a freemode char (even fully spoofed)
	// doesn't resolve to (verified in-game). So we credit the wallet ourselves: read each
	// money pickup's TRUE value straight from its object struct, and when a tracked money
	// pickup leaves the world pool (= collected), add that value to the wallet.
	//
	// Pickup object struct offsets (build VER_EN_1_0_1013_34, found by a sentinel
	// ground-truth probe): cash VALUE is an int at +0x480, pickup TYPE hash at +0x468.
	internal sealed class Earning {
		const int OffPickupType = 0x468;
		const int OffPickupValue = 0x480;

		readonly Wallet wallet;

		public Earning(Wallet wallet) {
			this.wallet = wallet;
		}

		// Money pickup TYPE hashes — only these credit the wallet, so weapon/health/etc.
		// pickups (which hold other data at +0x480) never count as cash.
		static readonly HashSet<uint> MoneyTypes = new HashSet<uint> {
			0xFE18F3AF, // PICKUP_MONEY_VARIABLE (street cash, ATM splash, ...)
			0xCE6FDD6B, // PICKUP_MONEY_CASE
			0x5DE0AD3E, // PICKUP_MONEY_WALLET
			0x1E9A99F8, // PICKUP_MONEY_PURSE
			0x20893292, // PICKUP_MONEY_DEP_BAG
			0x14568F28, // PICKUP_MONEY_MED_BAG
			0xA3435C38, // PICKUP_MONEY_PAPER_TRAIL
		};

		// What we knew last tick about a tracked money pickup: its value, and whether we
		// already credited it (so collection + disappearance on different frames can't
		// double-count).
		struct Tracked {
			public int Value;
			public bool Credited;
		}
		Dictionary<int, Tracked> tracked = new Dictionary<int, Tracked>();

		// Read a money pickup object's value from its struct. False if the object has no
		// readable struct or isn't a money-pickup type. `obj` is the world pickup object
		// (a Prop); its MemoryAddress is the same struct base the +0x468/+0x480 offsets
		// were measured against.
		bool ReadMoneyPickup(Prop obj, out int value) {
			value = 0;
			if (obj == null) {
				return false;
			}
			IntPtr addr = obj.MemoryAddress;
			if (addr == IntPtr.Zero || !MemScan.IsReadable(addr + OffPickupValue, 4)) {
				return false;
			}
			uint type = MemScan.ReadUInt32(addr + OffPickupType);
			if (!MoneyTypes.Contains(type)) {
				return false;
			}
			value = MemScan.ReadInt32(addr + OffPickupValue);
			return value > 0;
		}

		// Called each tick. `enabled` gates crediting (the master wallet toggle); we still
		// track pickups when disabled so the baseline is correct when it's re-enabled.
		public void Tick(bool enabled) {
			Prop[] pickups = World.GetAllPickupObjects();
			var now = new Dictionary<int, Tracked>(tracked.Count + 4);

			foreach (Prop p in pickups) {
				if (!ReadMoneyPickup(p, out int value)) {
					continue;
				}
				bool credited = tracked.TryGetValue(p.Handle, out Tracked prev) && prev.Credited;
				now[p.Handle] = new Tracked { Value = value, Credited = credited };
			}

			// A money pickup tracked last tick is gone now = collected (tiny street cash
			// vanishes on contact within a frame, too fast for a collected-flag check). Credit
			// its cached value once.
			foreach (KeyValuePair<int, Tracked> kv in tracked) {
				if (kv.Value.Credited) continue;
				if (!now.ContainsKey(kv.Key)) {
					if (enabled) {
						wallet.Add(kv.Value.Value);
						Logger.Log($"Earning: collected ${kv.Value.Value} -> wallet ${wallet.Balance}.");
					}
				}
			}

			tracked = now;
		}
	}
}
