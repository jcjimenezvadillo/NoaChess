using NoaChess.Core;

namespace NoaChess.Engine.Evaluation.Nnue;

// HalfKP feature indexing — the frozen contract between the C# runtime and
// the Python training pipeline (feature_schema_id = 1).
//
// NNUE ("Efficiently Updatable Neural Network") evaluates a position through
// a sparse input layer: instead of feeding the raw board, each (piece,
// square) is one binary feature, made KING-RELATIVE so the same material
// means different things depending on where the king stands. There are two
// "perspectives", one per side; each sees the board from its own king.
//
// Feature index for one perspective:
//
//   index = kingSquare * 640 + pieceIndex * 64 + pieceSquare
//   pieceIndex = pieceType (P,N,B,R,Q = 0..4) * 2
//              + (0 if the piece belongs to the perspective owner, else 1)
//
// Kings are NOT features themselves (their position is the "bucket").
// Feature space per perspective: 64 * 10 * 64 = 40,960.
//
// Black's perspective mirrors the board vertically (rank flip, a1 <-> a8) so
// the network learns color-agnostic patterns: from Black's point of view its
// own king on e8 looks exactly like White's king on e1.
//
// SCHEMA IS FROZEN: any change here requires a new feature_schema_id, new
// datasets and new models. Tests pin the exact indices.
public static class NnueFeatureIndex
{
    public const int FeatureSchemaId = 1;

    public const int PieceKinds = 10;                       // 5 types x 2 colors, no kings.
    public const int FeaturesPerKingSquare = PieceKinds * 64;
    public const int InputSize = 64 * FeaturesPerKingSquare; // 40,960 per perspective.

    // Maximum simultaneously active features per perspective: 32 pieces minus
    // the two kings = 30.
    public const int MaxActiveFeatures = 30;

    // Vertical mirror (a1 <-> a8) used by the black perspective.
    public static int Flip(int square) => square ^ 56;

    // Feature index of one piece as seen by 'perspective'.
    // 'pieceColor'/'pieceType' describe the piece (never a king);
    // 'pieceSquare' is its board square; 'kingSquare' is the square of the
    // PERSPECTIVE OWNER's king. Squares are pre-flipped for Black inside.
    public static int Index(Color perspective, int kingSquare,
                            Color pieceColor, PieceType pieceType, int pieceSquare)
    {
        // Orient the board so the perspective owner always "plays up".
        int ksq = perspective == Color.White ? kingSquare : Flip(kingSquare);
        int psq = perspective == Color.White ? pieceSquare : Flip(pieceSquare);

        // 0 = own piece, 1 = enemy piece (relative to the perspective).
        int side = pieceColor == perspective ? 0 : 1;
        int pieceIndex = (int)pieceType * 2 + side;

        return ksq * FeaturesPerKingSquare + pieceIndex * 64 + psq;
    }

    // Writes the active feature indices of 'board' for 'perspective' into
    // 'destination' (allocation-free). Returns the count.
    public static int ActiveFeatures(Board board, Color perspective, Span<int> destination)
    {
        int kingSquare = board.KingSquare(perspective);
        int count = 0;

        for (int c = 0; c < 2; c++)
        {
            Color pieceColor = (Color)c;
            for (int t = 0; t < 5; t++) // Pawn..Queen — kings are not features.
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
