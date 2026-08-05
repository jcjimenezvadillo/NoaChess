using NoaChess.Core;

namespace NoaChess.Engine.Evaluation.Nnue;

// HalfKAv2_hm feature indexing (feature_schema_id = 2) - the frozen contract
// between the C# runtime and the Python training pipeline.
//
// NNUE evaluates a position through a sparse input layer: each (piece, square)
// is one binary feature, made KING-RELATIVE so the same material means
// different things depending on where the king stands. There are two
// "perspectives", one per side; each sees the board from its own king.
//
// HalfKA vs the older HalfKP: here the KING IS a feature too. Both kings share
// a single piece plane (plane 10), so the enemy king's position is encoded and
// the own king's is redundant-but-present (a per-bucket constant).
//
// Two orientations are applied to every square before indexing:
//   - Vertical flip (rank, a1<->a8) for the Black perspective, so the network
//     learns color-agnostic patterns (Black's own king on e8 looks like
//     White's on e1).
//   - Horizontal mirror (file, a<->h) when the perspective's king is on the
//     queenside (files a-d), so the king is always on files e-h. This halves
//     the king input space from 64 squares to 32 buckets ("hm").
//
// Feature index for one perspective:
//   index = (pieceSq ^ orient(ksq) ^ vflip) + plane*64 + kingBucket(ksq ^ vflip)
//     vflip      = 56 for Black, 0 for White
//     orient(k)  = 7 (flip file) if the perspective king is on files a-d, else 0
//     plane      = 0..9 for (P,N,B,R,Q x own/enemy); 10 for either king
//     kingBucket = 0..31 (files a-d and e-h share a bucket) times PS_NB
//
// Feature space per perspective: 32 king buckets * 704 = 22,528.
//
// SCHEMA IS FROZEN: any change here requires a new feature_schema_id, new
// datasets and new models. Tests pin the exact indices.
public static class NnueFeatureIndex
{
    public const int FeatureSchemaId = 2;

    // 5 piece types x 2 colors = 10 planes, plus one shared king plane.
    public const int PieceSquarePlanes = 11;
    public const int PsNb = PieceSquarePlanes * 64;          // 704.
    public const int KingBucketCount = 32;                   // 64 king squares mirrored to 32.
    public const int InputSize = KingBucketCount * PsNb;      // 22,528 per perspective.

    // Maximum simultaneously active features: all 32 pieces (both kings count).
    public const int MaxActiveFeatures = 32;

    // Vertical (rank) mirror used by the Black perspective.
    public static int Flip(int square) => square ^ 56;

    // Horizontal-mirror base: XOR 7 (flip file) when the perspective king is on
    // files a-d, else 0. Depends only on the king's file (rank-invariant).
    private static int Orient(int kingSquare) => (kingSquare & 7) < 4 ? 7 : 0;

    // KingBuckets[sq] gives the bucket offset (already scaled by PsNb) for a
    // king on 'sq' (A1 = index 0). The table is horizontally symmetric, so the
    // Orient mirror above keeps the piece squares consistent with it.
    private static readonly int[] KingBuckets = BuildKingBuckets();

    private static int[] BuildKingBuckets()
    {
        // Bucket ids 0..31, A1 first. Files a-d mirror e-h within each rank.
        int[] b =
        [
            28, 29, 30, 31, 31, 30, 29, 28,
            24, 25, 26, 27, 27, 26, 25, 24,
            20, 21, 22, 23, 23, 22, 21, 20,
            16, 17, 18, 19, 19, 18, 17, 16,
            12, 13, 14, 15, 15, 14, 13, 12,
             8,  9, 10, 11, 11, 10,  9,  8,
             4,  5,  6,  7,  7,  6,  5,  4,
             0,  1,  2,  3,  3,  2,  1,  0,
        ];
        for (int i = 0; i < 64; i++)
            b[i] *= PsNb;
        return b;
    }

    // Piece plane offset (already multiplied by 64) as seen from 'perspective'.
    // Either king maps to the shared plane 10; other pieces use an own (even)
    // or enemy (odd) plane.
    private static int PlaneOffset(Color perspective, Color pieceColor, PieceType pieceType)
    {
        if (pieceType == PieceType.King)
            return 10 * 64;
        int enemy = pieceColor == perspective ? 0 : 1;
        return ((int)pieceType * 2 + enemy) * 64;
    }

    // Feature index of one piece as seen by 'perspective'. 'pieceColor'/
    // 'pieceType' describe the piece (a king is allowed); 'pieceSquare' is its
    // board square; 'kingSquare' is the PERSPECTIVE OWNER's king square. Both
    // orientations are applied here from the raw squares.
    public static int Index(Color perspective, int kingSquare,
                            Color pieceColor, PieceType pieceType, int pieceSquare)
    {
        int vflip = perspective == Color.White ? 0 : 56;
        int orientedSquare = pieceSquare ^ Orient(kingSquare) ^ vflip;
        return orientedSquare
             + PlaneOffset(perspective, pieceColor, pieceType)
             + KingBuckets[kingSquare ^ vflip];
    }

    // Writes the active feature indices of 'board' for 'perspective' into
    // 'destination' (allocation-free). Returns the count. Kings are included.
    public static int ActiveFeatures(Board board, Color perspective, Span<int> destination)
    {
        int kingSquare = board.KingSquare(perspective);
        int count = 0;

        for (int c = 0; c < 2; c++)
        {
            Color pieceColor = (Color)c;
            for (int t = 0; t < 6; t++) // Pawn..King - the king IS a feature.
            {
                ulong pieces = board.Pieces(pieceColor, (PieceType)t);
                while (pieces != 0)
                {
                    int sq = Bitboard.PopLsb(ref pieces);
                    destination[count++] = Index(perspective, kingSquare, pieceColor, (PieceType)t, sq);
                }
            }
        }

        return count;
    }
}
