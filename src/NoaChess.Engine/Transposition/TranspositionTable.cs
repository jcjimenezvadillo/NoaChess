using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using NoaChess.Core;

namespace NoaChess.Engine.Transposition;

// Transposition table: a big hash map from Zobrist key to search results.
//
// Chess move orders transpose constantly (1.d4 d5 2.c4 and 1.c4 d5 2.d4 reach
// the same position), so the same position is searched over and over. The TT
// caches "I already searched this position to depth D and got score S": when
// the position reappears the result is reused, either cutting the node
// immediately or at least providing a proven-good move to try first.
//
// 5F design (reference scheme at our entry size):
// - CLUSTERED: the low key bits select a 4-entry cluster (64 bytes, one cache
//   line - a probe reads the whole cluster for one memory access). Four
//   candidate slots per position instead of one means far fewer useful
//   entries destroyed by index collisions.
// - AGED: each new search bumps a 5-bit generation. Zero is reserved for an
//   empty slot, so the 31 live values form the ageing cycle. Replacement
//   treats an entry's depth minus 8x its age as its worth, so stale results from
//   previous searches yield their slots gracefully instead of squatting.
// - The full 64-bit key is split: low bits index the cluster, the high 32
//   bits are stored for verification (TT moves are pseudo-legality-vetted
//   before use, so the 2^-32 residual false-match rate is harmless).
public sealed unsafe class TranspositionTable
{
    private const int ClusterSize = 4;
    // GenBound == 0 is the empty marker. Keeping the live generation in 1..31
    // prevents a generation-wrap eval-only non-PV entry from encoding exactly
    // like an empty slot, while preserving TTEntry's 16-byte size.
    private const int GenerationCycle = 31;
    private const int EntryBytes = 16;

    // Process-wide: allocate new tables in large pages when the OS allows it
    // (see TableMemory). The UCI host turns it on ("LargePages" option); the
    // library default keeps ordinary pages for every other caller.
    public static bool LargePagesAllowed { get; set; }

    // The clusters must be 64-byte aligned. A managed array starts wherever
    // the GC puts it (8-aligned, nothing more), and since every cluster shares
    // the array's misalignment, one unlucky allocation makes EVERY probe
    // straddle two cache lines: two memory accesses single-threaded, and under
    // SMP two coherence units per probe on the hottest shared structure in the
    // engine. TableMemory owns the block and hands out an aligned base.
    // Assigned by Resize (called from the constructor); the initializer only
    // silences the compiler, which cannot see through the method call.
    private TableMemory _memory = null!;
    private byte* _base;
    private long _tableBytes;
    private ulong _clusterMask;
    private int _generation;

    // Blocks replaced while a worker might still hold the old base (a helper
    // quarantined for missing a stop, or a resize requested mid-search). They
    // are released with the table, never under a running reader.
    private readonly List<TableMemory> _retired = [];

    public TranspositionTable(int sizeMb)
    {
        Resize(sizeMb);
    }

    public bool UsesLargePages => _memory.LargePages;
    public int SizeMb => (int)(_tableBytes >> 20);

    // The entry at 'index', addressed through the aligned base (a single lea).
    private ref TTEntry EntryAt(int index)
        => ref Unsafe.AsRef<TTEntry>(_base + (nint)index * EntryBytes);

    // Allocates the table. The cluster count is rounded down to a power of
    // two so "key % clusters" becomes "key & mask". 'releaseOld' false keeps
    // the previous block alive until the table itself goes, for callers that
    // cannot rule out a worker still reading it.
    public void Resize(int sizeMb, bool releaseOld = true)
    {
        if (Unsafe.SizeOf<TTEntry>() != EntryBytes)
            throw new InvalidOperationException(
                $"TTEntry is {Unsafe.SizeOf<TTEntry>()} bytes, not {EntryBytes}: " +
                "the cluster-per-cache-line layout is broken.");

        long targetEntries = (long)sizeMb * 1024 * 1024 / EntryBytes;

        int clusters = 1;
        while ((long)clusters * 2 * ClusterSize <= targetEntries)
            clusters *= 2;

        long bytes = (long)clusters * ClusterSize * EntryBytes;
        TableMemory? old = _memory;
        _memory = new TableMemory(bytes, LargePagesAllowed);
        _base = _memory.Base;
        _tableBytes = bytes;
        _clusterMask = (ulong)(clusters - 1);
        _generation = 1;

        if (old is not null)
        {
            if (releaseOld)
                old.Dispose();
            else
                _retired.Add(old);
        }
    }

    // Wipes all entries (new game).
    public void Clear()
    {
        _memory.Clear(_tableBytes);
        _generation = 1;
    }

    // Called once at the start of every search ("go"): ages every existing
    // entry by one generation step.
    public void NewSearch() => _generation = _generation % GenerationCycle + 1;

    // An entry's age in generations, respecting the 5-bit wrap-around.
    private int RelativeAge(in TTEntry entry)
        => (GenerationCycle + _generation - entry.Generation) % GenerationCycle;

    // Brings the cluster for 'key' into L1 ahead of the probe that will read
    // it. The search calls this the instant a move is made, hundreds of cycles
    // before the child node actually probes.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Prefetch(ulong key)
    {
        if (!Sse.IsSupported)
            return;
        int baseIdx = (int)(key & _clusterMask) * ClusterSize;
        Sse.Prefetch0(_base + (nint)baseIdx * EntryBytes);
    }

    // Looks up a position. Returns true (and the entry) when any slot of the
    // position's cluster holds it. A hit also refreshes the entry's
    // generation so a position still in use does not age out.
    public bool Probe(ulong key, out TTEntry entry)
    {
        int baseIdx = (int)(key & _clusterMask) * ClusterSize;
        uint key32 = (uint)(key >> 32);

        for (int i = 0; i < ClusterSize; i++)
        {
            ref TTEntry slot = ref EntryAt(baseIdx + i);
            if (slot.Key32 == key32 && slot.GenBound != 0)
            {
                // Refresh the generation so an entry still in use does not
                // age out - but only when it actually changed. Most hits are
                // re-hits within the same search, where the unconditional
                // write dirtied the cache line for nothing; under SMP that
                // invalidated it in every other worker's cache on every hit.
                byte packed = TTEntry.PackGenBound(_generation, slot.IsPv, slot.Bound);
                if (slot.GenBound != packed)
                    slot.GenBound = packed;
                entry = slot;
                return true;
            }
        }

        entry = default;
        return false;
    }

    // Stores a search result (or an eval-only entry when bound is None).
    // Same-position slots are reused; otherwise the victim is the cluster
    // entry with the lowest depth-minus-age worth, so old shallow entries go
    // first and fresh deep ones survive.
    public void Store(ulong key, int depth, int score, int staticEval,
                      BoundType bound, Move bestMove, bool isPv)
    {
        int baseIdx = (int)(key & _clusterMask) * ClusterSize;
        uint key32 = (uint)(key >> 32);

        // Prefer the slot already holding this position; otherwise pick the
        // least worthy victim.
        int replaceIdx = baseIdx;
        bool sameKey = false;
        for (int i = 0; i < ClusterSize; i++)
        {
            ref TTEntry slot = ref EntryAt(baseIdx + i);
            if (slot.Key32 == key32 || slot.GenBound == 0)
            {
                replaceIdx = baseIdx + i;
                sameKey = slot.GenBound != 0 && slot.Key32 == key32;
                break;
            }

            ref TTEntry victim = ref EntryAt(replaceIdx);
            if (slot.Depth - 8 * RelativeAge(in slot)
                < victim.Depth - 8 * RelativeAge(in victim))
                replaceIdx = baseIdx + i;
        }

        ref TTEntry target = ref EntryAt(replaceIdx);

        // Do not throw away a known best move when the new result has none
        // (e.g. an all-node where nothing improved alpha, or an eval-only
        // store refreshing the same position).
        if (bestMove == Move.None && sameKey)
            bestMove = target.BestMove;

        // Keep the deeper result when re-storing the same position, unless
        // the new one is exact (a real full-window value always wins) -
        // the reference's overwrite rule. Eval-only refreshes (bound None)
        // never clobber a real same-position entry.
        if (sameKey)
        {
            if (bound == BoundType.None)
            {
                // Eval-only refresh of an existing entry: fill a missing
                // static eval, never clobber the search result.
                if (target.StaticEval == TTEntry.NoStaticEval)
                    target.StaticEval = staticEval;
                return;
            }
            // No TtMoveOnKeep (a refused store still writing its new move, as
            // the reference writes the move before this test): measured
            // 2026-10-04 at 100,000 fixed nodes, +2.3 +/- 6.7 over 4,000
            // games, LLR -2.32, undecided at the cap; removed.
            if (bound != BoundType.Exact && depth < target.Depth - 4)
                return;
        }

        // A same-position store keeps the stronger of the two PV marks: once
        // a position has been on the PV, it stays interesting.
        bool pv = isPv || (sameKey && target.IsPv);

        target.Key32 = key32;
        target.Score = score;
        target.StaticEval = staticEval;
        target.BestMove = bestMove;
        target.Depth = (byte)Math.Clamp(depth, 0, 255);
        target.GenBound = TTEntry.PackGenBound(_generation, pv, bound);
    }
}
