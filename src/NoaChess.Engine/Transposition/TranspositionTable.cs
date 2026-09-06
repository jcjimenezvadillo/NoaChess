using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
public sealed class TranspositionTable
{
    private const int ClusterSize = 4;
    // GenBound == 0 is the empty marker. Keeping the live generation in 1..31
    // prevents a generation-wrap eval-only non-PV entry from encoding exactly
    // like an empty slot, while preserving TTEntry's 16-byte size.
    private const int GenerationCycle = 31;
    private const int EntryBytes = 16;
    private const int LineBytes = 64;

    // The table lives in a PINNED ulong buffer instead of a TTEntry[] so the
    // clusters can be 64-byte aligned. A managed array starts wherever the GC
    // puts it (8-aligned, nothing more), and since every cluster shares the
    // array's misalignment, one unlucky allocation makes EVERY probe straddle
    // two cache lines: two memory accesses single-threaded, and under SMP two
    // coherence units per probe on the hottest shared structure in the engine.
    // Pinning keeps the computed alignment valid for the buffer's lifetime;
    // one spare cache line of slack pays for sliding the base to a boundary.
    // Assigned by Resize (called from the constructor); the initializer only
    // silences the compiler, which cannot see through the method call.
    private ulong[] _buffer = [];
    private int _byteBase;
    private ulong _clusterMask;
    private int _generation;

    public TranspositionTable(int sizeMb)
    {
        Resize(sizeMb);
    }

    // The entry at 'index', addressed through the aligned base. The pattern
    // (data reference plus byte offset) compiles to a single lea; going
    // through a Span slice-and-cast per probe would not.
    private ref TTEntry EntryAt(int index)
        => ref Unsafe.As<byte, TTEntry>(ref Unsafe.AddByteOffset(
               ref Unsafe.As<ulong, byte>(ref MemoryMarshal.GetArrayDataReference(_buffer)),
               _byteBase + index * EntryBytes));

    // Allocates the table. The cluster count is rounded down to a power of
    // two so "key % clusters" becomes "key & mask".
    public void Resize(int sizeMb)
    {
        if (Unsafe.SizeOf<TTEntry>() != EntryBytes)
            throw new InvalidOperationException(
                $"TTEntry is {Unsafe.SizeOf<TTEntry>()} bytes, not {EntryBytes}: " +
                "the cluster-per-cache-line layout is broken.");

        long targetEntries = (long)sizeMb * 1024 * 1024 / EntryBytes;

        int clusters = 1;
        while ((long)clusters * 2 * ClusterSize <= targetEntries)
            clusters *= 2;

        // Cluster bytes, plus one line of slack for alignment, in ulongs.
        long bytes = (long)clusters * ClusterSize * EntryBytes + LineBytes;
        _buffer = GC.AllocateArray<ulong>((int)(bytes / sizeof(ulong)), pinned: true);
        nint addr = Marshal.UnsafeAddrOfPinnedArrayElement(_buffer, 0);
        _byteBase = (int)(-addr & (LineBytes - 1));
        _clusterMask = (ulong)(clusters - 1);
        _generation = 1;
    }

    // Wipes all entries (new game).
    public void Clear()
    {
        Array.Clear(_buffer);
        _generation = 1;
    }

    // Called once at the start of every search ("go"): ages every existing
    // entry by one generation step.
    public void NewSearch() => _generation = _generation % GenerationCycle + 1;

    // An entry's age in generations, respecting the 5-bit wrap-around.
    private int RelativeAge(in TTEntry entry)
        => (GenerationCycle + _generation - entry.Generation) % GenerationCycle;

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
