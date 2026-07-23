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
        if (parts.Length is < 4 or > 6)
            throw new ArgumentException($"Invalid FEN (4 to 6 fields expected): '{fen}'");

        // Parse and validate everything before touching the target board. FEN
        // loading is therefore strongly exception-safe: a rejected GUI/UCI
        // position cannot leave the live game half-cleared.
        var placements = new List<(Color Color, PieceType Type, int Square)>(32);
        var pieceAt = new PieceType[64];
        var colorAt = new Color[64];
        Array.Fill(pieceAt, PieceType.None);

        // Field 1: exactly eight ranks of exactly eight squares each.
        string[] ranks = parts[0].Split('/');
        if (ranks.Length != 8)
            throw new ArgumentException($"Invalid piece placement in FEN: '{fen}'");

        int whiteKings = 0, blackKings = 0;
        for (int fenRank = 0; fenRank < 8; fenRank++)
        {
            int rank = 7 - fenRank;
            int file = 0;
            foreach (char c in ranks[fenRank])
            {
                if (c is >= '1' and <= '8')
                {
                    file += c - '0';
                    if (file > 8)
                        throw new ArgumentException($"Invalid rank width in FEN: '{fen}'");
                    continue;
                }

                int pieceIndex = PieceChars.IndexOf(char.ToUpperInvariant(c));
                if (pieceIndex < 0 || file >= 8)
                    throw new ArgumentException($"Invalid piece placement in FEN: '{fen}'");

                var type = (PieceType)pieceIndex;
                if (type == PieceType.Pawn && rank is 0 or 7)
                    throw new ArgumentException($"Pawn on promotion rank in FEN: '{fen}'");

                Color color = char.IsUpper(c) ? Color.White : Color.Black;
                int square = Squares.FromFileRank(file++, rank);
                placements.Add((color, type, square));
                pieceAt[square] = type;
                colorAt[square] = color;
                if (type == PieceType.King)
                {
                    if (color == Color.White) whiteKings++;
                    else blackKings++;
                }
            }

            if (file != 8)
                throw new ArgumentException($"Invalid rank width in FEN: '{fen}'");
        }

        if (whiteKings != 1 || blackKings != 1)
            throw new ArgumentException($"FEN must contain exactly one king per side: '{fen}'");

        // Field 2: side to move.
        Color sideToMove = parts[1] switch
        {
            "w" => Color.White,
            "b" => Color.Black,
            _ => throw new ArgumentException($"Invalid side to move in FEN: '{fen}'")
        };

        // Field 3: castling.
        CastlingRights castling = CastlingRights.None;
        if (parts[2] != "-")
        {
            foreach (char c in parts[2])
            {
                CastlingRights right = c switch
                {
                    'K' => CastlingRights.WhiteKingSide,
                    'Q' => CastlingRights.WhiteQueenSide,
                    'k' => CastlingRights.BlackKingSide,
                    'q' => CastlingRights.BlackQueenSide,
                    _ => throw new ArgumentException($"Invalid castling in FEN: '{fen}'")
                };
                if ((castling & right) != 0)
                    throw new ArgumentException($"Duplicate castling right in FEN: '{fen}'");
                castling |= right;
            }
        }

        // Field 4: en passant.
        int enPassant = parts[3] == "-" ? Squares.None : Squares.Parse(parts[3]);

        // An EP target must describe the square crossed by the opponent's pawn.
        // FEN is allowed to publish it even when no pawn can capture, but the
        // target must be empty, on the right rank and backed by that pawn.
        if (enPassant != Squares.None)
        {
            int expectedRank = sideToMove == Color.White ? 5 : 2;
            int capturedPawn = sideToMove == Color.White ? enPassant - 8 : enPassant + 8;
            if (Squares.RankOf(enPassant) != expectedRank
                || pieceAt[enPassant] != PieceType.None
                || pieceAt[capturedPawn] != PieceType.Pawn
                || colorAt[capturedPawn] != Board.OppositeColor(sideToMove))
                throw new ArgumentException($"Invalid en passant target in FEN: '{fen}'");
        }

        // Fields 5 and 6 are optional in many informal FEN strings.
        int halfmove = 0;
        int fullmove = 1;
        if (parts.Length > 4 && (!int.TryParse(parts[4], out halfmove) || halfmove < 0))
            throw new ArgumentException($"Invalid halfmove clock in FEN: '{fen}'");
        if (parts.Length > 5 && (!int.TryParse(parts[5], out fullmove) || fullmove < 1))
            throw new ArgumentException($"Invalid fullmove number in FEN: '{fen}'");

        board.Clear();
        foreach (var piece in placements)
            board.PlacePiece(piece.Color, piece.Type, piece.Square);

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
