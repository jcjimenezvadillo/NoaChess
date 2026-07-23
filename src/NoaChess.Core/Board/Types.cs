namespace NoaChess.Core;

// Side color. It is used as an index (0/1) into internal arrays,
// so the numeric values matter and must not be changed.
public enum Color
{
    White = 0,
    Black = 1
}

// Piece type without color. As with Color, the numeric values are used
// as array indices (bitboards, Zobrist tables...).
public enum PieceType
{
    Pawn = 0,
    Knight = 1,
    Bishop = 2,
    Rook = 3,
    Queen = 4,
    King = 5,
    None = 6
}

// Pending castling rights. It is a combinable flag: for example, at the start
// of the game the value is All, and bits are removed as the king or the rooks
// move (or a rook gets captured).
[Flags]
public enum CastlingRights
{
    None = 0,
    WhiteKingSide = 1,   // White short castle (O-O)
    WhiteQueenSide = 2,  // White long castle (O-O-O)
    BlackKingSide = 4,   // Black short castle
    BlackQueenSide = 8,  // Black long castle
    All = WhiteKingSide | WhiteQueenSide | BlackKingSide | BlackQueenSide
}

// Utilities for working with squares. A square is represented as an integer
// 0..63 where 0 = a1, 1 = b1, ..., 7 = h1, 8 = a2, ..., 63 = h8.
// This convention ("little-endian rank-file") is the usual one in
// bitboard-based engines: bit N of the bitboard maps to square N.
public static class Squares
{
    // Special value meaning "no square" (e.g. no en passant available).
    public const int None = -1;

    // File 0..7 (0 = file 'a').
    public static int FileOf(int square) => square & 7;

    // Rank 0..7 (0 = rank '1').
    public static int RankOf(int square) => square >> 3;

    // Builds the square index from file and rank.
    public static int FromFileRank(int file, int rank) => rank * 8 + file;

    // Converts "e4" into its 0..63 index. Throws if the text is not valid.
    public static int Parse(string algebraic)
    {
        if (algebraic.Length != 2)
            throw new ArgumentException($"Invalid square: '{algebraic}'");
        int file = algebraic[0] - 'a';
        int rank = algebraic[1] - '1';
        if (file is < 0 or > 7 || rank is < 0 or > 7)
            throw new ArgumentException($"Invalid square: '{algebraic}'");
        return FromFileRank(file, rank);
    }

    // Converts a 0..63 index into algebraic notation ("e4").
    public static string ToAlgebraic(int square) =>
        $"{(char)('a' + FileOf(square))}{(char)('1' + RankOf(square))}";
}
