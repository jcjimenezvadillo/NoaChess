using System.Text;

namespace NoaChess.Core;

// Parser and serializer for FEN (Forsyth-Edwards Notation), the standard text
// format to describe a chess position. Example (starting position):
//
//   rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1
//
// The 6 space-separated fields are:
//   1. Pieces by rank, from rank 8 down to 1 (uppercase = white, digits = empty squares).
//   2. Side to move ('w' or 'b').
//   3. Castling rights ('KQkq' or '-').
//   4. En passant square ('e3' or '-').
//   5. Halfmove clock (fifty-move rule).
//   6. Full move number.
public static class Fen
{
    private const string PieceChars = "PNBRQK"; // Index = PieceType value.

    // Loads a FEN position into the given board, replacing its content.
    public static void Load(Board board, string fen)
    {
        string[] parts = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
            throw new ArgumentException($"Invalid FEN (at least 4 fields expected): '{fen}'");

        board.Clear();

        // Field 1: pieces. FEN starts at rank 8 and file 'a'.
        int rank = 7, file = 0;
        foreach (char c in parts[0])
        {
            if (c == '/')
            {
                rank--;
                file = 0;
            }
            else if (char.IsDigit(c))
            {
                file += c - '0'; // N empty squares.
            }
            else
            {
                int pieceIndex = PieceChars.IndexOf(char.ToUpperInvariant(c));
                if (pieceIndex < 0 || rank < 0 || file > 7)
                    throw new ArgumentException($"Invalid FEN: '{fen}'");
                Color color = char.IsUpper(c) ? Color.White : Color.Black;
                board.PlacePiece(color, (PieceType)pieceIndex, Squares.FromFileRank(file, rank));
                file++;
            }
        }

        // Field 2: side to move.
        Color sideToMove = parts[1] == "w" ? Color.White : Color.Black;

        // Field 3: castling.
        CastlingRights castling = CastlingRights.None;
        if (parts[2] != "-")
        {
            foreach (char c in parts[2])
            {
                castling |= c switch
                {
                    'K' => CastlingRights.WhiteKingSide,
                    'Q' => CastlingRights.WhiteQueenSide,
                    'k' => CastlingRights.BlackKingSide,
                    'q' => CastlingRights.BlackQueenSide,
                    _ => throw new ArgumentException($"Invalid castling in FEN: '{fen}'")
                };
            }
        }

        // Field 4: en passant.
        int enPassant = parts[3] == "-" ? Squares.None : Squares.Parse(parts[3]);

        // Fields 5 and 6 are optional in many informal FEN strings.
        int halfmove = parts.Length > 4 ? int.Parse(parts[4]) : 0;
        int fullmove = parts.Length > 5 ? int.Parse(parts[5]) : 1;

        board.SetState(sideToMove, castling, enPassant, halfmove, fullmove);
    }

    // Serializes the current board position as a FEN string.
    public static string Save(Board board)
    {
        var sb = new StringBuilder();

        for (int rank = 7; rank >= 0; rank--)
        {
            int emptyRun = 0; // Consecutive empty squares, emitted as a digit.
            for (int file = 0; file < 8; file++)
            {
                int sq = Squares.FromFileRank(file, rank);
                if (board.IsEmpty(sq))
                {
                    emptyRun++;
                    continue;
                }
                if (emptyRun > 0)
                {
                    sb.Append(emptyRun);
                    emptyRun = 0;
                }
                char pieceChar = PieceChars[(int)board.PieceTypeAt(sq)];
                sb.Append(board.ColorAt(sq) == Color.White ? pieceChar : char.ToLowerInvariant(pieceChar));
            }
            if (emptyRun > 0)
                sb.Append(emptyRun);
            if (rank > 0)
                sb.Append('/');
        }

        sb.Append(board.SideToMove == Color.White ? " w " : " b ");

        if (board.CastlingRights == CastlingRights.None)
        {
            sb.Append('-');
        }
        else
        {
            if (board.CastlingRights.HasFlag(CastlingRights.WhiteKingSide)) sb.Append('K');
            if (board.CastlingRights.HasFlag(CastlingRights.WhiteQueenSide)) sb.Append('Q');
            if (board.CastlingRights.HasFlag(CastlingRights.BlackKingSide)) sb.Append('k');
            if (board.CastlingRights.HasFlag(CastlingRights.BlackQueenSide)) sb.Append('q');
        }

        sb.Append(' ');
        sb.Append(board.EnPassantSquare == Squares.None ? "-" : Squares.ToAlgebraic(board.EnPassantSquare));
        sb.Append(' ').Append(board.HalfmoveClock);
        sb.Append(' ').Append(board.FullmoveNumber);

        return sb.ToString();
    }
}
