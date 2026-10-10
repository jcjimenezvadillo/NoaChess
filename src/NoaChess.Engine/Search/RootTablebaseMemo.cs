using NoaChess.Core;

namespace NoaChess.Engine.Search;

// The root tablebase ranking of one Lazy SMP search, computed by whichever
// worker gets there first and copied by the others (see
// AlphaBetaSearch.FilterRootMovesShared). Every worker used to rank the root
// by DTZ on its own: one probe of every root move each, so 28 identical
// rankings per move at 29 threads, all walking the same compressed files at
// once. Serial identifies the search the stored result belongs to.
internal sealed class RootTablebaseMemo
{
    public int Serial;
    public Move[] RootMoves = [];
    public bool LostInTb;
    public bool TbResolved;
    public bool InTb;
    public long TbHits;
}
