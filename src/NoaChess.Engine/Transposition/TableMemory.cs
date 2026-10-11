using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NoaChess.Engine.Transposition;

// The storage behind the transposition table: one block, 64-byte aligned.
//
// With large pages allowed and available (Windows, the account holds "Lock
// pages in memory"), the block is committed in 2 MB pages. A 1 GB table in
// 4 KB pages spans 262,144 pages, far beyond what the TLB covers, so nearly
// every probe - a random access by construction - also misses the TLB and
// pays a page walk on top of the cache miss; in 2 MB pages it spans 512. The
// reference allocates its table the same way. Otherwise the block is a
// pinned managed array (the storage used before large pages), slid to a
// cache-line boundary.
internal sealed unsafe class TableMemory : IDisposable
{
    private const int LineBytes = 64;

    private ulong[]? _array;
    private void* _largeBlock;
    private nuint _largeBytes;

    public byte* Base { get; }
    public bool LargePages => _largeBlock != null;

    public TableMemory(long bytes, bool tryLargePages)
    {
        if (tryLargePages && LargePageMemory.TryAllocate((nuint)bytes, out void* block, out nuint size))
        {
            // VirtualAlloc returns zeroed memory aligned to the large page.
            _largeBlock = block;
            _largeBytes = size;
            Base = (byte*)block;
            GC.AddMemoryPressure((long)size);
            return;
        }

        // One spare cache line of slack pays for sliding the base to a
        // boundary; pinning keeps that alignment valid for the block's life.
        _array = GC.AllocateArray<ulong>((int)((bytes + LineBytes) / sizeof(ulong)), pinned: true);
        byte* start = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_array));
        Base = start + (-(nint)start & (LineBytes - 1));
    }

    public void Clear(long bytes) => NativeMemory.Clear(Base, (nuint)bytes);

    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    ~TableMemory() => Free();

    private void Free()
    {
        if (_largeBlock != null)
        {
            LargePageMemory.Free(_largeBlock);
            GC.RemoveMemoryPressure((long)_largeBytes);
            _largeBlock = null;
        }
        _array = null;
    }
}

// Windows large-page allocation. The privilege is held by the account but
// disabled in the process token, so it is enabled once per process before the
// first attempt; any failure (not Windows, privilege not granted, no
// contiguous physical memory left) returns false and the caller falls back to
// ordinary pages.
internal static unsafe class LargePageMemory
{
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemLargePages = 0x20000000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint TokenAdjustPrivileges = 0x20;
    private const uint TokenQuery = 0x08;
    private const uint SePrivilegeEnabled = 0x02;
    private const int ErrorNotAllAssigned = 1300;

    private static readonly object Gate = new();
    private static int _privilegeState; // 0 untried, 1 enabled, -1 unavailable

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public long Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void* VirtualAlloc(void* address, nuint size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int VirtualFree(void* address, nuint size, uint freeType);

    [DllImport("kernel32.dll")]
    private static extern nuint GetLargePageMinimum();

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern int CloseHandle(nint handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int LookupPrivilegeValueW(string? systemName, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int AdjustTokenPrivileges(nint token, int disableAll, ref TokenPrivileges newState,
                                                    uint bufferLength, nint previousState, nint returnLength);

    public static bool TryAllocate(nuint bytes, out void* block, out nuint size)
    {
        block = null;
        size = 0;
        if (!OperatingSystem.IsWindows() || !EnsurePrivilege())
            return false;

        nuint page = GetLargePageMinimum();
        if (page == 0)
            return false;

        size = (bytes + page - 1) & ~(page - 1);
        block = VirtualAlloc(null, size, MemReserve | MemCommit | MemLargePages, PageReadWrite);
        return block != null;
    }

    public static void Free(void* block) => VirtualFree(block, 0, MemRelease);

    private static bool EnsurePrivilege()
    {
        lock (Gate)
        {
            if (_privilegeState == 0)
                _privilegeState = EnablePrivilege() ? 1 : -1;
            return _privilegeState == 1;
        }
    }

    private static bool EnablePrivilege()
    {
        if (OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out nint token) == 0)
            return false;
        try
        {
            if (LookupPrivilegeValueW(null, "SeLockMemoryPrivilege", out long luid) == 0)
                return false;
            var state = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            // Succeeds even when the privilege is not held; that case is
            // reported only through the last error.
            if (AdjustTokenPrivileges(token, 0, ref state, 0, 0, 0) == 0)
                return false;
            return Marshal.GetLastPInvokeError() != ErrorNotAllAssigned;
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
