using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NoaChess.Engine.Evaluation.Nnue;

// Arrays whose first element sits on a 64-byte boundary (6.5.0).
//
// WHY. The NNUE kernels read and write 32-byte AVX2 vectors, and a .NET array
// only guarantees 8-byte alignment of its data: measured on the shipping
// build, the transformer weights started at 24 mod 64, the accumulators at 0
// or 24, the coarse lane's scratch at 16 or 56. At such an offset every other
// 32-byte access straddles two cache lines, on every transformer row, every
// accumulator pass and every lane pass. Aligned, none does. Nothing about the
// arithmetic changes, so the engine is bit-identical either way.
//
// HOW. The arrays are allocated pinned, on the pinned-object heap, where they
// never move, so an alignment observed once holds for the array's life. The
// allocator hands out consecutive addresses, so a misaligned candidate is kept
// alive, a small pinned pad is placed after it, and the next candidate lands
// further on; a few tries reach a 64-byte boundary. If it never does (another
// thread allocating pinned memory at the same moment, say), the last
// candidate is returned anyway: misaligned is slower, never wrong.
internal static class NnueAlignedArrays
{
    public const int Alignment = 64;

    public static T[] Allocate<T>(int length) where T : unmanaged
    {
        var keepAlive = new List<object>();
        T[] candidate = GC.AllocateArray<T>(length, pinned: true);
        // A huge array gets a region of its own at a fixed offset, so retrying
        // would only burn memory: measured, every 23 MB candidate landed at
        // the same 56 mod 64. Those take one try.
        int attempts = (long)length * Unsafe.SizeOf<T>() > (8 << 20) ? 0 : 32;
        for (int attempt = 0; attempt < attempts && Misalignment(candidate) != 0; attempt++)
        {
            keepAlive.Add(candidate);
            keepAlive.Add(GC.AllocateArray<byte>(8 * (attempt % 8), pinned: true));
            candidate = GC.AllocateArray<T>(length, pinned: true);
        }
        GC.KeepAlive(keepAlive);
        return candidate;
    }

    // A copy of 'source' in an aligned array.
    public static T[] Copy<T>(T[] source) where T : unmanaged
    {
        T[] result = Allocate<T>(source.Length);
        source.AsSpan().CopyTo(result);
        return result;
    }

    public static unsafe long Misalignment<T>(T[] array) where T : unmanaged
        => (long)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(array)) & (Alignment - 1);
}
