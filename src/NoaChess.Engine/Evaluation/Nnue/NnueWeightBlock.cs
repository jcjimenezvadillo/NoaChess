using System.Runtime.InteropServices;
using NoaChess.Engine.Transposition;

namespace NoaChess.Engine.Evaluation.Nnue;

// The feature-transformer weights in native memory (6.5.0).
//
// The managed array that holds them is a single huge allocation, and the
// runtime places those at a fixed offset inside a region of their own:
// measured, 56 mod 64. Every transformer row is read as 32-byte vectors, so at
// that offset every other read straddled two cache lines. Here the block is
// 64-byte aligned, and where the OS grants 2 MB pages (Windows, with "Lock
// pages in memory", the same privilege the hash table uses) it is committed in
// them: at 512 wide the table is 22 MB, which in 4 KB pages spans ~5,600 pages
// - far past what the TLB holds, for reads that are random by construction -
// and in 2 MB pages spans 12. The values are a copy of the managed array, so
// the arithmetic is unchanged and the engine bit-identical.
internal sealed unsafe class NnueWeightBlock
{
    private void* _block;
    private readonly bool _largePages;
    private readonly long _bytes;

    public NnueWeightBlock(short[] source, bool tryLargePages)
    {
        nuint bytes = (nuint)source.Length * sizeof(short);
        if (tryLargePages && LargePageMemory.TryAllocate(bytes, out void* block, out nuint size))
        {
            _block = block;
            _largePages = true;
            _bytes = (long)size;
        }
        else
        {
            _block = NativeMemory.AlignedAlloc(bytes, 64);
            _bytes = (long)bytes;
        }
        source.AsSpan().CopyTo(new Span<short>(_block, source.Length));
        GC.AddMemoryPressure(_bytes);
    }

    public bool LargePages => _largePages;

    public ref short Ref => ref *(short*)_block;

    ~NnueWeightBlock()
    {
        if (_block == null)
            return;
        if (_largePages)
            LargePageMemory.Free(_block);
        else
            NativeMemory.AlignedFree(_block);
        GC.RemoveMemoryPressure(_bytes);
        _block = null;
    }
}
