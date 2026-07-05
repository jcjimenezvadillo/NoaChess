namespace NoaChess.Core;

// Kind of move. It is encoded in 4 bits inside Move.
// The scheme is the classic one used by chess engines: bit 2 (value 4)
// means capture, and values 8..15 mean promotion (with or without capture).
// This allows checking "is it a capture" or "is it a promotion" with a single
// bit operation.
public enum MoveFlag
{
    Quiet = 0,            // Normal non-capturing move
    DoublePawnPush = 1,   // Double pawn advance (creates an en passant square)
    KingCastle = 2,       // Short castle
    QueenCastle = 3,      // Long castle
    Capture = 4,          // Normal capture
    EnPassant = 5,        // En passant capture (the captured pawn is NOT on the destination square)
    PromoKnight = 8,      // Promotion to knight
    PromoBishop = 9,
    PromoRook = 10,
    PromoQueen = 11,
    PromoKnightCapture = 12, // Promotion with capture
    PromoBishopCapture = 13,
    PromoRookCapture = 14,
    PromoQueenCapture = 15
}

// A move encoded in 16 bits: 6 bits origin + 6 bits destination + 4 bits flag.
// A compact struct is used (instead of a class) because the search creates
// millions of moves and we want to avoid heap allocations.
public readonly struct Move : IEquatable<Move>
{
    private readonly ushort _data;

    // "Null" move used as default value / "no move".
    public static readonly Move None = default;

    public Move(int from, int to, MoveFlag flag)
    {
        // Packing: bits 0-5 = origin, 6-11 = destination, 12-15 = flag.
        _data = (ushort)(from | (to << 6) | ((int)flag << 12));
    }

    public int From => _data & 0x3F;
    public int To => (_data >> 6) & 0x3F;
    public MoveFlag Flag => (MoveFlag)(_data >> 12);

    // True if the move captures something (includes en passant and capture promotions).
    public bool IsCapture => ((_data >> 12) & 4) != 0;

    // True if the move is a pawn promotion.
    public bool IsPromotion => ((_data >> 12) & 8) != 0;

    // Piece the pawn promotes to. Only valid when IsPromotion is true.
    // The two low bits of the promotion flag encode: 0=knight, 1=bishop,
    // 2=rook, 3=queen, which matches PieceType.Knight + those two bits.
    public PieceType PromotionPiece => (PieceType)((int)PieceType.Knight + ((_data >> 12) & 3));

    public bool Equals(Move other) => _data == other._data;
    public override bool Equals(object? obj) => obj is Move m && Equals(m);
    public override int GetHashCode() => _data;
    public static bool operator ==(Move a, Move b) => a.Equals(b);
    public static bool operator !=(Move a, Move b) => !a.Equals(b);

    // UCI notation of the move: "e2e4", "e7e8q" for promotions.
    // It is the standard format used by GUIs and the UCI protocol.
    public override string ToString()
    {
        string s = Squares.ToAlgebraic(From) + Squares.ToAlgebraic(To);
        if (IsPromotion)
        {
            s += PromotionPiece switch
            {
                PieceType.Knight => "n",
                PieceType.Bishop => "b",
                PieceType.Rook => "r",
                _ => "q"
            };
        }
        return s;
    }
}
