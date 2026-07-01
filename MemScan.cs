using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace FreemodeIdentity {
	// Crash-proof process-memory primitives. Union of the two source mods' helpers:
	// the appearance side needs the pointer-graph walk / region sweep / snapshot reads;
	// the spoof + shim side needs the gated u32/i32 reads and the VirtualProtect-flipping
	// write. Kept as one class so there is a single memory-safety gate for the whole mod.
	//
	// CRITICAL: this code dereferences pointers pulled out of game memory, many of which
	// are garbage. An access violation on an unmapped address CANNOT be caught by a C#
	// try/catch — it kills the whole game process. So every read first asks the OS, via
	// VirtualQuery, whether the address range is committed and readable; an unreadable
	// address becomes a skip/zero, never a fault. Treat that VirtualQuery gate as
	// mandatory: never Marshal.Read* a game pointer without it.
	static class MemScan {
		[StructLayout(LayoutKind.Sequential)]
		struct MEMORY_BASIC_INFORMATION {
			public IntPtr BaseAddress;
			public IntPtr AllocationBase;
			public uint AllocationProtect;
			public IntPtr RegionSize;
			public uint State;
			public uint Protect;
			public uint Type;
		}

		[DllImport("kernel32.dll")]
		static extern IntPtr VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, IntPtr dwLength);

		[DllImport("kernel32.dll")]
		static extern bool VirtualProtect(IntPtr lpAddress, IntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

		const uint MEM_COMMIT = 0x1000;
		// Any protection flag that permits reading. PAGE_NOACCESS(0x01)/PAGE_GUARD(0x100)
		// must be excluded — touching a guard page also faults.
		const uint PAGE_READABLE = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80; // R, RW, WC, EX-R, EX-RW, EX-WC
		const uint PAGE_WRITABLE = 0x04 | 0x08 | 0x40 | 0x80;              // RW, WC, EX-RW, EX-WC
		const uint PAGE_READWRITE = 0x04;
		const uint PAGE_GUARD = 0x100;

		static readonly IntPtr MbiSize = (IntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION));

		// True only if [addr, addr+size) is entirely committed and readable per the OS.
		public static bool IsReadable(IntPtr addr, int size) {
			long start = addr.ToInt64();
			if (start <= 0x10000 || start >= 0x7FFFFFFFFFFF) {
				return false;
			}
			long end = start + size;
			long cur = start;
			while (cur < end) {
				MEMORY_BASIC_INFORMATION mbi;
				if (VirtualQuery((IntPtr)cur, out mbi, MbiSize) == IntPtr.Zero) {
					return false;
				}
				if (mbi.State != MEM_COMMIT) {
					return false;
				}
				if ((mbi.Protect & PAGE_GUARD) != 0 || (mbi.Protect & PAGE_READABLE) == 0) {
					return false;
				}
				long regionEnd = mbi.BaseAddress.ToInt64() + mbi.RegionSize.ToInt64();
				if (regionEnd <= cur) {
					return false; // no forward progress; bail rather than spin
				}
				cur = regionEnd;
			}
			return true;
		}

		// A pointer worth dereferencing: canonical user range AND actually readable.
		public static bool LooksLikeHeapPtr(IntPtr p) {
			return IsReadable(p, 8);
		}

		public static IntPtr SafeReadPtr(IntPtr addr) {
			return IsReadable(addr, 8) ? Marshal.ReadIntPtr(addr) : IntPtr.Zero;
		}

		public static ushort ReadUInt16(IntPtr addr) {
			return IsReadable(addr, 2) ? unchecked((ushort)Marshal.ReadInt16(addr)) : (ushort)0;
		}

		public static uint ReadUInt32(IntPtr addr) {
			return IsReadable(addr, 4) ? unchecked((uint)Marshal.ReadInt32(addr)) : 0u;
		}

		public static int ReadInt32(IntPtr addr) {
			return IsReadable(addr, 4) ? Marshal.ReadInt32(addr) : 0;
		}

		// Ungated raw reads — NO VirtualQuery. ONLY for an address a caller already proved committed
		// this session and that can't have moved since (e.g. the spoof's held slot, re-validated
		// whenever the ped handle changes). The gated versions cost a syscall each; on a per-frame
		// re-assert of a known-good address that syscall was the dominant cost. Never call these on a
		// pointer freshly pulled out of game memory — an unmapped read here is an uncatchable access
		// violation that kills the process.
		public static uint ReadUInt32Raw(IntPtr addr) => unchecked((uint)Marshal.ReadInt32(addr));
		public static int ReadInt32Raw(IntPtr addr) => Marshal.ReadInt32(addr);

		// Write a u32, temporarily flipping the page to PAGE_READWRITE if needed and
		// restoring the original protection after. VirtualQuery-gated like the reads.
		// Returns false (writing nothing) if the page is unreadable or the flip fails.
		public static bool WriteUInt32(IntPtr addr, uint value) {
			if (!IsReadable(addr, 4)) {
				return false;
			}
			uint oldProtect;
			bool flipped = false;
			if (!IsWritable(addr, 4)) {
				if (!VirtualProtect(addr, (IntPtr)4, PAGE_READWRITE, out oldProtect)) {
					return false;
				}
				flipped = true;
			} else {
				oldProtect = 0;
			}
			Marshal.WriteInt32(addr, unchecked((int)value));
			if (flipped) {
				uint ignore;
				VirtualProtect(addr, (IntPtr)4, oldProtect, out ignore);
			}
			return true;
		}

		static bool IsWritable(IntPtr addr, int size) {
			long start = addr.ToInt64();
			long end = start + size;
			long cur = start;
			while (cur < end) {
				MEMORY_BASIC_INFORMATION mbi;
				if (VirtualQuery((IntPtr)cur, out mbi, MbiSize) == IntPtr.Zero) {
					return false;
				}
				if (mbi.State != MEM_COMMIT) {
					return false;
				}
				if ((mbi.Protect & PAGE_GUARD) != 0 || (mbi.Protect & PAGE_WRITABLE) == 0) {
					return false;
				}
				long regionEnd = mbi.BaseAddress.ToInt64() + mbi.RegionSize.ToInt64();
				if (regionEnd <= cur) {
					return false;
				}
				cur = regionEnd;
			}
			return true;
		}

		// Copy as much of [addr, addr+len) as is readable into a managed array, stopping at
		// the first unreadable page so a partially-mapped block still yields its head.
		//
		// Copies ONE page at a time, re-checking IsReadable immediately before each page's
		// copy. The game runs concurrently and can unmap a page between the check and the
		// copy; doing it per-page keeps that check→copy window as small as possible (a
		// whole-span check-then-copy leaves a wide TOCTOU window where a freed page faults
		// the Marshal.Copy with an UNCATCHABLE access violation that kills the process).
		public static byte[] Snapshot(IntPtr addr, int len) {
			const int Page = 0x1000;
			var buf = new byte[len];
			int done = 0;
			while (done < len) {
				int chunk = Math.Min(Page, len - done);
				if (!IsReadable(addr + done, chunk)) {
					break;
				}
				Marshal.Copy(addr + done, buf, done, chunk);
				done += chunk;
			}
			if (done == len) {
				return buf;
			}
			// Trim to what we actually read so callers see a short buffer, not zero padding.
			var trimmed = new byte[done];
			Array.Copy(buf, trimmed, done);
			return trimmed;
		}

		// One committed, readable memory region: its base and size. Used by content scans
		// that must sweep process memory (e.g. the decoration-array probe) rather than walk
		// a pointer graph from the ped.
		public struct Region {
			public IntPtr Base;
			public long Size;
		}

		// Enumerate committed, readable memory regions across the user address space, each no
		// larger than maxRegionSize (huge regions are chunked so a caller can time-budget its
		// scan). Skips guard/no-access pages. Heap data (where the decoration array lives) is
		// in PAGE_READWRITE regions; pass writableOnly to skip read-only/image/code regions and
		// keep the sweep small.
		public static IEnumerable<Region> EnumerateRegions(long maxRegionSize = 0x100000, bool writableOnly = true, bool privateOnly = false) {
			const uint PAGE_WRITABLE_REGION = 0x04 | 0x08 | 0x40 | 0x80; // RW, WC, EX-RW, EX-WC
			const uint MEM_PRIVATE = 0x20000; // not a mapped file/image — the heap, where the decoration array lives
			long cur = 0x10000;
			const long userMax = 0x7FFFFFFFFFFF;
			while (cur < userMax) {
				MEMORY_BASIC_INFORMATION mbi;
				if (VirtualQuery((IntPtr)cur, out mbi, MbiSize) == IntPtr.Zero) {
					break;
				}
				long regionSize = mbi.RegionSize.ToInt64();
				if (regionSize <= 0) {
					break;
				}
				bool committed = mbi.State == MEM_COMMIT;
				bool guarded = (mbi.Protect & PAGE_GUARD) != 0;
				bool readable = (mbi.Protect & PAGE_READABLE) != 0;
				bool writable = (mbi.Protect & PAGE_WRITABLE_REGION) != 0;
				bool isPrivate = mbi.Type == MEM_PRIVATE;
				if (committed && readable && !guarded && (!writableOnly || writable) && (!privateOnly || isPrivate)) {
					long off = 0;
					while (off < regionSize) {
						long chunk = Math.Min(maxRegionSize, regionSize - off);
						yield return new Region { Base = (IntPtr)(mbi.BaseAddress.ToInt64() + off), Size = chunk };
						off += chunk;
					}
				}
				cur = mbi.BaseAddress.ToInt64() + regionSize;
			}
		}

		// Breadth-first walk of the pointer graph rooted at the ped object, up to a few
		// hops deep, yielding every unique readable block. The CPedHeadBlendData struct
		// hangs off the ped via the extension list (ped+16 → list → array → entry →
		// struct), so a few hops reach it without knowing the exact list layout. Bounded
		// by a visited-set and a hard cap so it always terminates.
		public static IEnumerable<IntPtr> WalkPointerGraph(IntPtr root, int maxHops = 4, int maxBlocks = 4000) {
			const int BlockScanBytes = 0x80 * 8; // bytes read per block to follow further pointers
			var seen = new HashSet<long>();
			var frontier = new List<IntPtr> { root };
			seen.Add(root.ToInt64());
			int yielded = 0;

			for (int hop = 0; hop <= maxHops; hop++) {
				var next = new List<IntPtr>();
				foreach (IntPtr block in frontier) {
					yield return block;
					if (++yielded >= maxBlocks) {
						yield break;
					}
					// Snapshot the block ONCE, then read child pointers from the managed buffer.
					// The old code did a VirtualQuery (SafeReadPtr→IsReadable) per qword — 128 syscalls
					// per block × thousands of blocks = the scan's dominant cost and FPS hit. One
					// page-gated copy + a cheap range pre-filter cuts the syscalls by ~100x; only
					// plausible pointers pay a VirtualQuery (in LooksLikeHeapPtr) before being followed.
					byte[] buf = Snapshot(block, BlockScanBytes);
					for (int off = 0; off + 8 <= buf.Length; off += 8) {
						long raw = BitConverter.ToInt64(buf, off);
						if ((raw & 7) != 0 || raw <= 0x10000 || raw >= 0x7FFFFFFFFFFF) {
							continue; // not an 8-aligned user-range pointer — skip without a syscall
						}
						if (seen.Add(raw) && LooksLikeHeapPtr((IntPtr)raw)) {
							next.Add((IntPtr)raw);
						}
					}
				}
				if (next.Count == 0) {
					yield break;
				}
				frontier = next;
			}
		}
	}
}
