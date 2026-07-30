using System;
using System.Collections.Generic;
using System.Text;

namespace DetPS2.Core;

/// <summary>
/// C# HLE for BIOS IOP <b>SYSCLIB</b> (standard C library export table) and <b>HEAPLIB</b>
/// (heap helpers layered on SYSMEM-shaped page allocation).
///
/// <para><b>Authority:</b> IOPBTCONF order (SYSCLIB then HEAPLIB after TIMEMAN*, before
/// THREADMAN — <c>docs/BIOS_DISSECTION.md</c> §2); ps2sdk <c>iop/system/sysclib</c> and
/// <c>iop/system/heaplib</c> export tables / contracts (SCE SDK 1.3.4-based recreation).
/// No Ghidra dumps of retail SYSCLIB/HEAPLIB are in-tree yet.</para>
///
/// <para><b>Why this exists:</b> commercial IRX modules import <c>sysclib</c> /
/// <c>heaplib</c> via LOADCORE stubs. Without registered export tables, <see cref="IrxLoader.LinkImports"/>
/// patches every ordinal to <c>jr ra</c> (unresolved). This host plants non-null function
/// pointers and registers the libraries on <see cref="IopModuleHost"/> so linking succeeds
/// the same way a real IOPBTCONF bring-up would after SYSCLIB/HEAPLIB <c>RegisterLibraryEntries</c>.</para>
///
/// <para>Not cycle-accurate R3000 execution of the real IRX — export contracts + HEAPLIB
/// freelist HLE. SYSCLIB MIPS bodies are <c>jr ra; nop</c> stubs until IOP IRX execution
/// lands; HEAPLIB C# APIs implement Create/Alloc/Free for host-side use and future intercept.</para>
/// </summary>
public sealed class IopSysclibHeaplibHost
{
    // ---- Export library names / versions (ps2sdk DECLARE_EXPORT_TABLE) ----
    public const string SysclibLibName = "sysclib";
    public const string HeaplibLibName = "heaplib";
    public const byte LibVersionMajor = 1;
    public const byte LibVersionMinor = 1;

    // sysclib exports.tab: ordinals 0..44 (45 entries) then terminator.
    // heaplib exports.tab: ordinals 0..17 (18 entries) then terminator.
    public const int SysclibExportCount = 45;
    public const int HeaplibExportCount = 18;

    // Named high-value ordinals (ps2sdk sysclib.h / heaplib.h DECLARE_IMPORT).
    public const int OrdMemcpy = 12;
    public const int OrdMemset = 14;
    public const int OrdSprintf = 19;
    public const int OrdStrcmp = 22;
    public const int OrdStrlen = 27;
    public const int OrdCreateHeap = 4;
    public const int OrdDeleteHeap = 5;
    public const int OrdAllocHeapMemory = 6;
    public const int OrdFreeHeapMemory = 7;
    public const int OrdHeapTotalFreeSize = 8;
    public const int OrdHeapPrepare = 11;
    public const int OrdHeapChunkSize = 15;

    // HLE plant region — IOP physical, below IrxLoader.DefaultLoadBase (0x10000).
    // Holds jr-ra stubs + in-memory 0x41C00000 export tables for ScanExports parity.
    public const uint StubRegionPhys = 0x00004000;
    public const uint StubRegionSize = 0x00002000; // 8 KiB

    // HEAPLIB CreateHeap backends: SYSMEM-shaped 256-byte page freelist in a tight window
    // immediately below RealSifRpc's EE iopheap [0x180000, 0x1F0000). IRX HLE loads only a
    // handful of RPC-owning BIOS modules from 0x10000; 64 KiB here is enough for CreateHeap
    // smokes and driver-sized heaps without eating the main iopheap.
    public const uint HeapPoolBase = 0x00170000;
    public const uint HeapPoolLimit = 0x00180000; // abut RealSifRpc IopHeapBase
    private const uint SysmemPageSize = 256;

    private sealed class HeapBlock
    {
        public uint Phys;
        public uint Size;
        public bool Free = true;
    }

    private sealed class Heap
    {
        public uint Handle; // opaque EE-mapped or phys key returned to callers
        public uint BasePhys;
        public uint Size;
        public int Flag;
        public readonly List<HeapBlock> Blocks = new();
        public bool Alive = true;
    }

    private readonly List<Heap> _heaps = new();
    private readonly Dictionary<uint, uint> _poolLive = new(); // phys -> size (CreateHeap backends)
    private readonly List<(uint Phys, uint Size)> _poolHoles = new();
    private uint _poolNext = HeapPoolBase;
    private uint _nextHandleCookie = 1;
    private bool _installed;
    private uint[] _sysclibExports = Array.Empty<uint>();
    private uint[] _heaplibExports = Array.Empty<uint>();

    public bool Installed => _installed;
    public int HeapCount
    {
        get
        {
            int n = 0;
            foreach (var h in _heaps)
                if (h.Alive) n++;
            return n;
        }
    }
    public ulong CreateHeapOps { get; private set; }
    public ulong AllocHeapOps { get; private set; }
    public ulong FreeHeapOps { get; private set; }
    public IReadOnlyList<uint> SysclibExports => _sysclibExports;
    public IReadOnlyList<uint> HeaplibExports => _heaplibExports;

    public void Reset()
    {
        _heaps.Clear();
        _poolLive.Clear();
        _poolHoles.Clear();
        _poolNext = HeapPoolBase;
        _nextHandleCookie = 1;
        _installed = false;
        CreateHeapOps = AllocHeapOps = FreeHeapOps = 0;
        _sysclibExports = Array.Empty<uint>();
        _heaplibExports = Array.Empty<uint>();
    }

    /// <summary>
    /// Plant jr-ra stubs + export tables in IOP RAM and register <c>sysclib</c>/<c>heaplib</c>
    /// on the module host export registry. Idempotent for a given boot; call from
    /// <see cref="BiosBootHost"/> commercial IOP finish path.
    /// </summary>
    public void Install(SystemMemory mem, IopModuleHost modules)
    {
        if (mem == null || modules == null) return;
        Reset();

        uint phys = StubRegionPhys;
        uint eeBase = SystemMemory.IOP_RAM_BASE + phys;

        // Shared retonly body: jr ra ; nop
        uint retonly = eeBase;
        mem.Write32(retonly, 0x03E00008);     // jr ra
        mem.Write32(retonly + 4, 0x00000000); // nop
        // ret-negative-one: addiu v0, zero, -1 ; jr ra ; nop
        uint retNeg1 = eeBase + 0x10;
        mem.Write32(retNeg1, 0x2402FFFF);     // addiu v0, zero, -1
        mem.Write32(retNeg1 + 4, 0x03E00008); // jr ra
        mem.Write32(retNeg1 + 8, 0x00000000); // nop

        uint stubCursor = eeBase + 0x30;

        _sysclibExports = new uint[SysclibExportCount];
        for (int i = 0; i < SysclibExportCount; i++)
        {
            // Ordinal 44 is _retnegativeone in ps2sdk exports.tab; rest retonly-shaped stubs.
            uint body = (i == 44) ? retNeg1 : PlantRetonly(mem, ref stubCursor, retonly);
            _sysclibExports[i] = body;
        }

        _heaplibExports = new uint[HeaplibExportCount];
        for (int i = 0; i < HeaplibExportCount; i++)
        {
            uint body = PlantRetonly(mem, ref stubCursor, retonly);
            _heaplibExports[i] = body;
        }

        // Plant real 0x41C00000 export tables so ScanExports over the stub region finds them.
        uint sysclibTable = stubCursor;
        stubCursor = PlantExportTable(mem, stubCursor, SysclibLibName, LibVersionMajor, LibVersionMinor, _sysclibExports);
        uint heaplibTable = stubCursor;
        stubCursor = PlantExportTable(mem, stubCursor, HeaplibLibName, LibVersionMajor, LibVersionMinor, _heaplibExports);

        if (stubCursor > eeBase + StubRegionSize)
            throw new InvalidOperationException(
                $"SYSCLIB/HEAPLIB HLE plant overflowed stub region (end=0x{stubCursor:X8})");

        modules.RegisterExportLibrary(new IrxLoader.ExportTable
        {
            Name = SysclibLibName,
            VersionMajor = LibVersionMajor,
            VersionMinor = LibVersionMinor,
            Exports = (uint[])_sysclibExports.Clone(),
        });
        modules.RegisterExportLibrary(new IrxLoader.ExportTable
        {
            Name = HeaplibLibName,
            VersionMajor = LibVersionMajor,
            VersionMinor = LibVersionMinor,
            Exports = (uint[])_heaplibExports.Clone(),
        });

        // Name-only module table entries (IOPBTCONF residents).
        modules.RegisterModule("SYSCLIB", systemResident: true);
        modules.RegisterModule("HEAPLIB", systemResident: true);

        _installed = true;
        // Silence unused locals when tables are only for memory parity / future ScanExports.
        _ = sysclibTable;
        _ = heaplibTable;
    }

    private static uint PlantRetonly(SystemMemory mem, ref uint cursor, uint sharedRetonly)
    {
        // Give each ordinal a unique address (distinct export pointers) while sharing
        // the same jr-ra body via a short branch, or just duplicate jr ra; nop for simplicity.
        uint addr = cursor;
        mem.Write32(addr, 0x03E00008);
        mem.Write32(addr + 4, 0x00000000);
        cursor = addr + 8;
        _ = sharedRetonly;
        return addr;
    }

    private static uint PlantExportTable(SystemMemory mem, uint at, string name,
        byte verMajor, byte verMinor, uint[] exports)
    {
        mem.Write32(at + 0x00, IrxLoader.ExportTableMagic);
        mem.Write32(at + 0x04, 0);
        mem.Write8(at + 0x08, verMinor);
        mem.Write8(at + 0x09, verMajor);
        mem.Write8(at + 0x0A, 0);
        mem.Write8(at + 0x0B, 0);
        byte[] nameBytes = new byte[8];
        byte[] raw = Encoding.ASCII.GetBytes(name);
        Array.Copy(raw, nameBytes, Math.Min(8, raw.Length));
        for (int i = 0; i < 8; i++)
            mem.Write8(at + 0x0C + (uint)i, nameBytes[i]);
        uint p = at + 0x14;
        for (int i = 0; i < exports.Length; i++, p += 4)
            mem.Write32(p, exports[i]);
        mem.Write32(p, 0); // terminator
        return p + 4;
    }

    // ---- HEAPLIB C# contracts (ps2sdk heaplib.c shapes) ----

    /// <summary>
    /// CreateHeap(heapblocksize, flag) — allocate a backend via SYSMEM-shaped pages and
    /// prepare an internal freelist. Returns opaque heap pointer (EE-mapped IOP) or 0.
    /// flag bit0: free-on-DeleteHeap size tracking; bit1: AllocSysMemory LAST-ish (ignored,
    /// HLE always FIRST-fit like EE iopheap).
    /// </summary>
    public uint CreateHeap(int heapblocksize, int flag)
    {
        CreateHeapOps++;
        if (heapblocksize <= 0) return 0;
        uint size = (uint)heapblocksize;
        // Match heaplib.c: calc_size = 4 * ((heapblocksize + 3) >> 2)
        size = 4u * ((size + 3) >> 2);
        if (size < 0x40) size = 0x40;

        uint phys = AllocPoolPages(size);
        if (phys == 0) return 0;

        uint handle = SystemMemory.IOP_RAM_BASE + phys;
        // Cookie-tag high nibble of a parallel id so double-free of raw pool ptr is caught —
        // handle is the pool base itself (real heaplib returns the heap struct pointer).
        var heap = new Heap
        {
            Handle = handle,
            BasePhys = phys,
            Size = size,
            Flag = flag,
        };
        heap.Blocks.Add(new HeapBlock { Phys = phys, Size = size, Free = true });
        _heaps.Add(heap);
        _nextHandleCookie++;
        return handle;
    }

    /// <summary>DeleteHeap — free backend pages; invalid/unknown → no-op.</summary>
    public void DeleteHeap(uint heapPtr)
    {
        var heap = FindHeap(heapPtr);
        if (heap == null || !heap.Alive) return;
        heap.Alive = false;
        heap.Blocks.Clear();
        FreePoolPages(heap.BasePhys);
    }

    /// <summary>AllocHeapMemory — first-fit within the heap; 0 on fail / bad heap.</summary>
    public uint AllocHeapMemory(uint heapPtr, uint nbytes)
    {
        AllocHeapOps++;
        var heap = FindHeap(heapPtr);
        if (heap == null || !heap.Alive) return 0;
        if (nbytes == 0) nbytes = 1;
        // 8-byte align like real chunk quanta (heaplib uses 8-byte blocks).
        uint need = (nbytes + 7u) & ~7u;

        for (int i = 0; i < heap.Blocks.Count; i++)
        {
            var b = heap.Blocks[i];
            if (!b.Free || b.Size < need) continue;
            uint allocPhys = b.Phys;
            uint rem = b.Size - need;
            if (rem >= 8)
            {
                b.Phys = allocPhys + need;
                b.Size = rem;
            }
            else
            {
                need = b.Size; // take whole block
                heap.Blocks.RemoveAt(i);
            }
            heap.Blocks.Insert(0, new HeapBlock { Phys = allocPhys, Size = need, Free = false });
            return SystemMemory.IOP_RAM_BASE + allocPhys;
        }
        return 0;
    }

    /// <summary>FreeHeapMemory — 0 ok, negative error codes matching heaplib.c shapes.</summary>
    public int FreeHeapMemory(uint heapPtr, uint ptr)
    {
        FreeHeapOps++;
        var heap = FindHeap(heapPtr);
        if (heap == null || !heap.Alive) return -4; // invalid heap
        if (ptr == 0) return -1;

        uint phys = ToPhys(ptr);
        for (int i = 0; i < heap.Blocks.Count; i++)
        {
            var b = heap.Blocks[i];
            if (b.Free || b.Phys != phys) continue;
            b.Free = true;
            Coalesce(heap);
            return 0;
        }
        return -1; // not found / not owned
    }

    /// <summary>HeapTotalFreeSize — sum of free bytes in heap; −4 if invalid.</summary>
    public int HeapTotalFreeSize(uint heapPtr)
    {
        var heap = FindHeap(heapPtr);
        if (heap == null || !heap.Alive) return -4;
        int sum = 0;
        foreach (var b in heap.Blocks)
            if (b.Free) sum += (int)b.Size;
        return sum;
    }

    /// <summary>
    /// HeapPrepare — mark a raw memory region as a chunk freelist owner (simplified:
    /// registers a transient heap over [mem, mem+size) without AllocSysMemory).
    /// </summary>
    public void HeapPrepare(uint memPtr, int size)
    {
        if (memPtr == 0 || size < 0x29) return;
        uint phys = ToPhys(memPtr);
        // Replace any prior prepare over the same base.
        for (int i = _heaps.Count - 1; i >= 0; i--)
        {
            if (_heaps[i].BasePhys == phys && _heaps[i].Alive)
            {
                _heaps[i].Alive = false;
            }
        }
        var heap = new Heap
        {
            Handle = memPtr,
            BasePhys = phys,
            Size = (uint)size,
            Flag = 0,
        };
        heap.Blocks.Add(new HeapBlock { Phys = phys, Size = (uint)size, Free = true });
        _heaps.Add(heap);
    }

    /// <summary>HeapChunkSize — free bytes remaining in a prepared chunk/heap.</summary>
    public int HeapChunkSize(uint chunkPtr) => HeapTotalFreeSize(chunkPtr);

    private Heap? FindHeap(uint heapPtr)
    {
        if (heapPtr == 0) return null;
        uint phys = ToPhys(heapPtr);
        foreach (var h in _heaps)
        {
            if (!h.Alive) continue;
            if (h.Handle == heapPtr || h.BasePhys == phys) return h;
        }
        return null;
    }

    private static void Coalesce(Heap heap)
    {
        heap.Blocks.Sort((a, b) => a.Phys.CompareTo(b.Phys));
        for (int i = 0; i < heap.Blocks.Count - 1;)
        {
            var a = heap.Blocks[i];
            var b = heap.Blocks[i + 1];
            if (a.Free && b.Free && a.Phys + a.Size == b.Phys)
            {
                a.Size += b.Size;
                heap.Blocks.RemoveAt(i + 1);
            }
            else i++;
        }
    }

    /// <summary>SYSMEM-shaped page alloc for CreateHeap backends (256-byte quanta, FIRST-fit).</summary>
    private uint AllocPoolPages(uint reqSize)
    {
        uint pages = (reqSize + (SysmemPageSize - 1)) / SysmemPageSize;
        if (pages == 0) return 0;
        uint aligned = pages * SysmemPageSize;

        for (int i = 0; i < _poolHoles.Count; i++)
        {
            var hole = _poolHoles[i];
            if (hole.Size < aligned) continue;
            uint phys = hole.Phys;
            uint rem = hole.Size - aligned;
            _poolHoles.RemoveAt(i);
            if (rem > 0)
                _poolHoles.Insert(i, (phys + aligned, rem));
            _poolLive[phys] = aligned;
            return phys;
        }

        uint addr = _poolNext;
        if (addr + aligned > HeapPoolLimit || addr + aligned < addr)
            return 0;
        _poolNext = addr + aligned;
        _poolLive[addr] = aligned;
        return addr;
    }

    private void FreePoolPages(uint phys)
    {
        if (!_poolLive.TryGetValue(phys, out uint size)) return;
        _poolLive.Remove(phys);
        // Coalesce holes (same algorithm as RealSifRpc.InsertHoleCoalesced).
        int i = 0;
        while (i < _poolHoles.Count && _poolHoles[i].Phys < phys) i++;
        _poolHoles.Insert(i, (phys, size));
        if (i > 0)
        {
            var prev = _poolHoles[i - 1];
            if (prev.Phys + prev.Size == phys)
            {
                _poolHoles[i - 1] = (prev.Phys, prev.Size + size);
                _poolHoles.RemoveAt(i);
                i--;
                phys = _poolHoles[i].Phys;
                size = _poolHoles[i].Size;
            }
        }
        if (i + 1 < _poolHoles.Count)
        {
            var next = _poolHoles[i + 1];
            if (phys + size == next.Phys)
            {
                _poolHoles[i] = (phys, size + next.Size);
                _poolHoles.RemoveAt(i + 1);
            }
        }
        if (_poolHoles.Count > 0)
        {
            var last = _poolHoles[^1];
            if (last.Phys + last.Size == _poolNext)
            {
                _poolNext = last.Phys;
                _poolHoles.RemoveAt(_poolHoles.Count - 1);
            }
        }
    }

    private static uint ToPhys(uint addr)
    {
        if (addr >= SystemMemory.IOP_RAM_BASE &&
            addr < SystemMemory.IOP_RAM_BASE + (uint)SystemMemory.IOP_RAM_SIZE)
            return addr - SystemMemory.IOP_RAM_BASE;
        return addr & 0x1FFFFFu;
    }
}
