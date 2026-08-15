using NoaChess.Core;

namespace NoaChess.Engine.Evaluation.Nnue;

// Threat feature indexing, ported from the reference engine's source.
//
// WHAT A FEATURE IS. HalfKA answers "what is where". This answers "what attacks
// what": one binary feature per (attacker piece, from, to, attacked piece).
// Both colours of attacker and both colours of attacked are recorded, so
// DEFENDING one of your own pieces is as much a feature as attacking an enemy
// one - that second colour is where half the space comes from. 60,720
// dimensions, at most 128 active, and unlike HalfKA the king does not multiply
// the space: it only decides which way the board faces.
//
// THE PACKING. A literal (piece, from, to, attacked) index would need 64x64
// square pairs per piece, and almost all of them are geometrically impossible -
// a knight on a1 will never attack h8. So 'to' is stored as its RANK WITHIN the
// pseudo-attack set of 'from', and Offsets holds the running total of those set
// sizes over every earlier 'from'. That is what collapses 4,096 square pairs
// into 336 for a knight, 1,456 for a queen and 132 for a pawn.
//
// THE DIRECTION BIT IS NOT A DIMENSION. PairBase is indexed by [attacker]
// [attacked][from < to], but BOTH entries hold the same base. The second exists
// only to be discarded when attacker and attacked share a piece type: a knight
// attacking a knight is also that knight attacking this one, so without the
// discard the same relation would be counted from both ends. Friendly pawns are
// the exception, because two pawns of one colour can never attack each other -
// and that free entry is what carries the blocked pawn push, which the
// reference encodes as a threat of its own.
//
// SCHEMA: this is a SECOND feature set alongside HalfKAv2_hm, not a replacement.
// It has its own transformer and its own weights. Any change to the numbering
// here invalidates every net trained against it.
public static class ThreatFeatureIndex
{
    public const int InputSize = 60720;

    // The reference's own bound. Measured maximum on real positions is about 52,
    // so this is head-room and not a target, but a caller that writes past it
    // would corrupt whatever follows.
    public const int MaxActiveFeatures = 128;

    private const int PieceCount = 12;          // 6 types x 2 colours.

    // Which (attacker, attacked) type pairs are recorded, and the slot each gets
    // within that attacker's block. -1 means the pair is not a feature at all.
    // A pawn records nothing against a bishop or a queen, the sliders record
    // nothing against a queen, and a king records nothing whatsoever.
    private static readonly int[] Map =
    [
        // attacked:  P   N   B   R   Q   K
        /* pawn   */  0,  1, -1,  2, -1, -1,
        /* knight */  0,  1,  2,  3,  4, -1,
        /* bishop */  0,  1,  2,  3, -1, -1,
        /* rook   */  0,  1,  2,  3, -1, -1,
        /* queen  */  0,  1,  2,  3,  4, -1,
        /* king   */ -1, -1, -1, -1, -1, -1,
    ];

    // Twice the number of recorded targets per attacker type: once for an
    // attacked white piece, once for a black one.
    private static readonly int[] ValidTargets = BuildValidTargets();

    // Flattened deliberately. Multidimensional arrays cost measurably in this
    // engine's hot paths (flattening the history and piece tables was worth
    // +6.1% NPS at identical node counts), and this table is read once per
    // feature.
    private static readonly int[] Offsets = new int[PieceCount * 64];          // [piece][from]
    private static readonly sbyte[] Slot = new sbyte[PieceCount * 64 * 64];    // [piece][from][to]
    private static readonly int[] PairBase = new int[PieceCount * PieceCount * 2];
    private static readonly int[] BlockStart = new int[PieceCount];
    private static readonly int[] PieceSpan = new int[PieceCount];

    static ThreatFeatureIndex()
    {
        BuildTables();
    }

    private static int[] BuildValidTargets()
    {
        int[] v = new int[6];
        for (int a = 0; a < 6; a++)
        {
            int n = 0;
            for (int d = 0; d < 6; d++)
                if (Map[a * 6 + d] >= 0)
                    n++;
            v[a] = 2 * n;
        }
        return v;
    }

    private static int Piece(Color colour, PieceType type) => (int)colour * 6 + (int)type;

    // The empty-board attack set the packing is built from. Pawns use captures
    // PLUS the single push, and only from ranks 2-7, because a pawn cannot
    // stand on the first or the last.
    private static ulong Pseudo(int piece, int square)
    {
        int colour = piece / 6, type = piece % 6;
        if (type == (int)PieceType.Pawn)
        {
            if (square < 8 || square >= 56)
                return 0UL;
            ulong bb = Attacks.Pawn((Color)colour, square);
            return bb | (colour == (int)Color.White ? 1UL << (square + 8) : 1UL << (square - 8));
        }
        return type switch
        {
            (int)PieceType.Knight => Attacks.Knight(square),
            (int)PieceType.Bishop => Attacks.Bishop(square, 0UL),
            (int)PieceType.Rook => Attacks.Rook(square, 0UL),
            (int)PieceType.Queen => Attacks.Queen(square, 0UL),
            _ => Attacks.King(square),
        };
    }

    private static void BuildTables()
    {
        int cumulative = 0;
        for (int p = 0; p < PieceCount; p++)
        {
            int run = 0;
            for (int sq = 0; sq < 64; sq++)
            {
                Offsets[p * 64 + sq] = run;
                ulong attacks = Pseudo(p, sq);

                // The slot of 'to' is how many attacked squares come before it,
                // which is exactly the popcount of the attack set below 'to'.
                for (int to = 0; to < 64; to++)
                {
                    Slot[(p * 64 + sq) * 64 + to] = (attacks >> to & 1UL) != 0
                        ? (sbyte)Bitboard.PopCount(attacks & ((1UL << to) - 1))
                        : (sbyte)-1;
                }

                run += Bitboard.PopCount(attacks);
            }

            BlockStart[p] = cumulative;
            PieceSpan[p] = run;
            cumulative += ValidTargets[p % 6] * run;
        }

        if (cumulative != InputSize)
            throw new InvalidOperationException(
                $"threat packing produced {cumulative} dimensions, expected {InputSize}");

        for (int attacker = 0; attacker < PieceCount; attacker++)
        {
            int aColour = attacker / 6, aType = attacker % 6;
            for (int attacked = 0; attacked < PieceCount; attacked++)
            {
                int dColour = attacked / 6, dType = attacked % 6;
                int at = (attacker * PieceCount + attacked) * 2;

                int slot = Map[aType * 6 + dType];
                if (slot < 0)
                {
                    PairBase[at] = -1;
                    PairBase[at + 1] = -1;
                    continue;
                }

                int band = dColour * (ValidTargets[aType] / 2) + slot;
                int position = BlockStart[attacker] + band * PieceSpan[attacker];

                bool enemy = aColour != dColour;
                bool symmetric = aType == dType && (enemy || aType != (int)PieceType.Pawn);

                PairBase[at] = position;
                PairBase[at + 1] = symmetric ? -1 : position;
            }
        }
    }

    // The packing shape for one piece: where its block starts and how many
    // (from, to) pairs one target band holds. Exposed because parity with the
    // trainer is checked table by table and not only index by index - when the
    // two disagree, knowing WHICH piece's block drifted turns a hunt into a
    // subtraction.
    public static (int BlockStart, int Span) Packing(Color colour, PieceType type)
    {
        int p = Piece(colour, type);
        return (BlockStart[p], PieceSpan[p]);
    }

    // Horizontal mirror when the perspective's king stands on files a-d, the
    // same rule HalfKAv2_hm uses, so the two feature sets agree about which way
    // the board faces.
    private static int Orient(int kingSquare) => (kingSquare & 7) < 4 ? 7 : 0;

    // Feature index of one threat as seen by 'perspective', or -1 when the
    // relation is not recorded. Squares and colours are absolute; the flip is
    // applied here, once.
    public static int Index(Color perspective, int kingSquare,
                            Color attackerColour, PieceType attackerType, int from,
                            Color attackedColour, PieceType attackedType, int to)
    {
        int orientation = Orient(kingSquare) ^ (perspective == Color.White ? 0 : 56);
        int fromOriented = from ^ orientation;
        int toOriented = to ^ orientation;

        // Flipping perspective swaps the colour of both pieces.
        int attacker = Piece(attackerColour, attackerType);
        int attacked = Piece(attackedColour, attackedType);
        if (perspective != Color.White)
        {
            attacker = (attacker + 6) % 12;
            attacked = (attacked + 6) % 12;
        }

        int at = (attacker * PieceCount + attacked) * 2 + (fromOriented < toOriented ? 1 : 0);
        int position = PairBase[at];
        if (position < 0)
            return -1;

        // The geometry check the reference does not need and this does. Over
        // there make_index is only ever reached from a bitboard of genuine
        // attacks, so 'to' is always inside 'from's attack set. This method is
        // also reachable from tests and from the parity harness, and without
        // the check a pair that is not an attack returns a valid-looking index
        // belonging to a different relation - a silent wrong feature.
        sbyte slot = Slot[(attacker * 64 + fromOriented) * 64 + toOriented];
        if (slot < 0)
            return -1;

        return position + Offsets[attacker * 64 + fromOriented] + slot;
    }

    // Writes the active threat features of 'board' for 'perspective' into
    // 'destination' (allocation-free) and returns the count. 'destination' must
    // hold at least MaxActiveFeatures.
    public static int ActiveFeatures(Board board, Color perspective, Span<int> destination)
    {
        int kingSquare = board.KingSquare(perspective);
        ulong occupied = board.AllOccupancy;
        int count = 0;

        // The target filters mirror the recorded pairs in Map, so a relation
        // generated here is never one the index would then reject. Built once
        // from four reads and shared by the three filters, instead of nine reads
        // across three calls.
        ulong pawns = Both(board, PieceType.Pawn);
        ulong allKnights = Both(board, PieceType.Knight);
        ulong allBishops = Both(board, PieceType.Bishop);
        ulong allRooks = Both(board, PieceType.Rook);

        ulong pawnTargets = pawns | allKnights | allRooks;
        ulong minorTargets = pawnTargets | allBishops;
        ulong queenTargets = minorTargets | Both(board, PieceType.Queen);

        for (int c = 0; c < 2; c++)
        {
            Color colour = (Color)c;
            ulong ownPawns = board.Pieces(colour, PieceType.Pawn);

            // Captures, both diagonals.
            ulong left = colour == Color.White
                ? (ownPawns & ~Bitboard.FileH) << 9
                : (ownPawns & ~Bitboard.FileA) >> 9;
            ulong right = colour == Color.White
                ? (ownPawns & ~Bitboard.FileA) << 7
                : (ownPawns & ~Bitboard.FileH) >> 7;
            int leftStep = colour == Color.White ? 9 : -9;
            int rightStep = colour == Color.White ? 7 : -7;

            AddPawns(board, perspective, kingSquare, colour, left & pawnTargets,
                     leftStep, destination, ref count);
            AddPawns(board, perspective, kingSquare, colour, right & pawnTargets,
                     rightStep, destination, ref count);

            // A pawn stopped by a pawn directly in front. What matters is that
            // the blocker is a pawn, not whose it is.
            ulong blocked = colour == Color.White
                ? ownPawns & (pawns >> 8)
                : ownPawns & (pawns << 8);
            int push = colour == Color.White ? 8 : -8;
            AddPawns(board, perspective, kingSquare, colour,
                     colour == Color.White ? blocked << 8 : blocked >> 8,
                     push, destination, ref count);

            for (int t = (int)PieceType.Knight; t <= (int)PieceType.Queen; t++)
            {
                PieceType type = (PieceType)t;
                ulong targets = type is PieceType.Knight or PieceType.Queen
                    ? queenTargets : minorTargets;
                ulong movers = board.Pieces(colour, type);

                while (movers != 0)
                {
                    int from = Bitboard.PopLsb(ref movers);
                    ulong attacks = type switch
                    {
                        PieceType.Knight => Attacks.Knight(from),
                        PieceType.Bishop => Attacks.Bishop(from, occupied),
                        PieceType.Rook => Attacks.Rook(from, occupied),
                        _ => Attacks.Queen(from, occupied),
                    } & targets;

                    while (attacks != 0)
                    {
                        int to = Bitboard.PopLsb(ref attacks);
                        int index = Index(perspective, kingSquare, colour, type, from,
                                          board.ColorAt(to), board.PieceTypeAt(to), to);
                        if (index >= 0)
                            destination[count++] = index;
                    }
                }
            }
        }

        return count;
    }

    private static void AddPawns(Board board, Color perspective, int kingSquare, Color colour,
                                 ulong destinations, int step, Span<int> output, ref int count)
    {
        while (destinations != 0)
        {
            int to = Bitboard.PopLsb(ref destinations);
            int index = Index(perspective, kingSquare, colour, PieceType.Pawn, to - step,
                              board.ColorAt(to), board.PieceTypeAt(to), to);
            if (index >= 0)
                output[count++] = index;
        }
    }

    // Both colours of one piece type. Replaced a `params PieceType[]` helper
    // that allocated a fresh array on EVERY call - three per refresh, two
    // refreshes per node - which is the kind of cost that never shows up in a
    // correctness test and shows up in every game.
    private static ulong Both(Board board, PieceType type)
        => board.Pieces(Color.White, type) | board.Pieces(Color.Black, type);
}
