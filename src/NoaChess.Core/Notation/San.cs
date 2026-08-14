using System.Text;

namespace NoaChess.Core;

// Reader and writer of SAN (Standard Algebraic Notation) chess moves - "Nf3",
// "exd5", "O-O", "e8=Q+", "Qh4xe1#". A SAN token is resolved against the
// position's LEGAL moves: matching a generated move is the only reliable way to
// attach the correct flags (capture, en passant, castle, promotion) to a bare
// notation string, and the same list is what tells the writer whether a move
// needs disambiguating. Sibling of Fen. Used to replay PGN games (opening-book
// seeding for datagen) and to print move lists in the GUI; not on any hot path,
// so clarity beats micro-optimization.
public static class San
{
    // Resolves 'san' to the one legal move it denotes in 'board'. Returns false
    // if the token is malformed or does not match exactly one legal move (an
    // ambiguous or illegal token is rejected rather than guessed).
    public static bool TryParse(Board board, string san, out Move move)
    {
        move = Move.None;
        if (string.IsNullOrWhiteSpace(san))
            return false;

        // Drop check/mate marks and annotation glyphs (+ # ! ?).
        string s = san.Trim().TrimEnd('+', '#', '!', '?');
        if (s.Length == 0)
            return false;

        List<Move> legal = MoveGenerator.GenerateLegalMoves(board);

        if (s is "O-O" or "0-0")
            return MatchCastle(board, legal, kingSide: true, out move);
        if (s is "O-O-O" or "0-0-0")
            return MatchCastle(board, legal, kingSide: false, out move);

        // A leading uppercase piece letter, otherwise a pawn move.
        PieceType piece = PieceType.Pawn;
        int start = 0;
        if ("NBRQK".IndexOf(s[0]) >= 0)
        {
            piece = PieceFromChar(s[0]);
            start = 1;
        }

        // Promotion: "=Q" or a bare trailing piece letter ("e8Q").
        PieceType promotion = PieceType.None;
        int eq = s.IndexOf('=');
        if (eq >= 0)
        {
            if (eq + 1 >= s.Length || "NBRQ".IndexOf(s[eq + 1]) < 0)
                return false;
            promotion = PieceFromChar(s[eq + 1]);
            s = s[..eq];
        }
        else if (piece == PieceType.Pawn && s.Length >= 3 && "NBRQ".IndexOf(s[^1]) >= 0)
        {
            promotion = PieceFromChar(s[^1]);
            s = s[..^1];
        }

        // The last two characters are the destination square; whatever remains
        // between the piece letter and it (minus 'x') is the disambiguation.
        if (s.Length < 2)
            return false;
        int destFile = s[^2] - 'a';
        int destRank = s[^1] - '1';
        if (destFile is < 0 or > 7 || destRank is < 0 or > 7)
            return false;
        int dest = Squares.FromFileRank(destFile, destRank);

        int fromFile = -1, fromRank = -1;
        for (int i = start; i < s.Length - 2; i++)
        {
            char c = s[i];
            if (c == 'x') continue;
            if (c is >= 'a' and <= 'h') fromFile = c - 'a';
            else if (c is >= '1' and <= '8') fromRank = c - '1';
            else return false;
        }

        Move found = Move.None;
        foreach (Move m in legal)
        {
            if (m.To != dest || board.PieceTypeAt(m.From) != piece)
                continue;
            if (promotion != PieceType.None)
            {
                if (!m.IsPromotion || m.PromotionPiece != promotion)
                    continue;
            }
            else if (m.IsPromotion)
            {
                continue; // SAN gave no promotion piece, so a promotion cannot match.
            }
            if (fromFile >= 0 && Squares.FileOf(m.From) != fromFile)
                continue;
            if (fromRank >= 0 && Squares.RankOf(m.From) != fromRank)
                continue;
            if (found != Move.None)
                return false; // Under-specified SAN matching two moves: reject.
            found = m;
        }

        move = found;
        return found != Move.None;
    }

    // Standard castling: the king moves exactly two files (positive toward the
    // king side). Chess960 king-onto-rook encoding is out of scope (PGN from
    // human databases is standard chess).
    private static bool MatchCastle(Board board, List<Move> legal, bool kingSide, out Move move)
    {
        move = Move.None;
        foreach (Move m in legal)
        {
            if (board.PieceTypeAt(m.From) != PieceType.King)
                continue;
            if (Squares.FileOf(m.To) - Squares.FileOf(m.From) == (kingSide ? 2 : -2))
            {
                move = m;
                return true;
            }
        }
        return false;
    }

    // Writes 'move' as SAN in 'board', which must be the position BEFORE the
    // move. It is the exact inverse of TryParse: parsing the result back in the
    // same position returns the same move.
    //
    // The board is temporarily advanced and restored to find out whether the
    // move gives check or mate - that is a property of the position AFTER the
    // move and there is no way to know it without playing it.
    public static string Format(Board board, Move move)
    {
        if (move == Move.None)
            return "--";

        PieceType piece = board.PieceTypeAt(move.From);
        var text = new StringBuilder(8);

        switch (move.Flag)
        {
            case MoveFlag.KingCastle:
                text.Append("O-O");
                break;
            case MoveFlag.QueenCastle:
                text.Append("O-O-O");
                break;
            default:
                if (piece == PieceType.Pawn)
                {
                    // A pawn is named by its file, and only when it captures.
                    if (move.IsCapture)
                        text.Append(FileChar(move.From)).Append('x');
                    text.Append(Squares.ToAlgebraic(move.To));
                    if (move.IsPromotion)
                        text.Append('=').Append(CharFromPiece(move.PromotionPiece));
                }
                else
                {
                    text.Append(CharFromPiece(piece));
                    AppendDisambiguation(board, move, piece, text);
                    if (move.IsCapture)
                        text.Append('x');
                    text.Append(Squares.ToAlgebraic(move.To));
                }
                break;
        }

        board.MakeMove(move);
        if (board.IsInCheck())
            text.Append(MoveGenerator.HasLegalMove(board) ? '+' : '#');
        board.UnmakeMove();

        return text.ToString();
    }

    // Adds the minimum origin hint that makes the move unambiguous: nothing when
    // no other piece of the same type can legally reach the destination, the
    // file when that alone separates them, the rank when it does not, and both
    // when neither does (three queens on the same file and rank pattern).
    private static void AppendDisambiguation(Board board, Move move, PieceType piece, StringBuilder text)
    {
        bool ambiguous = false, sharesFile = false, sharesRank = false;

        // Only LEGAL moves count: a rival piece that is pinned cannot reach the
        // square, so it does not make the notation ambiguous.
        foreach (Move other in MoveGenerator.GenerateLegalMoves(board))
        {
            if (other.To != move.To || other.From == move.From)
                continue;
            if (board.PieceTypeAt(other.From) != piece)
                continue;

            ambiguous = true;
            if (Squares.FileOf(other.From) == Squares.FileOf(move.From))
                sharesFile = true;
            if (Squares.RankOf(other.From) == Squares.RankOf(move.From))
                sharesRank = true;
        }

        if (!ambiguous)
            return;
        if (!sharesFile)
            text.Append(FileChar(move.From));
        else if (!sharesRank)
            text.Append(RankChar(move.From));
        else
            text.Append(FileChar(move.From)).Append(RankChar(move.From));
    }

    private static char FileChar(int square) => (char)('a' + Squares.FileOf(square));

    private static char RankChar(int square) => (char)('1' + Squares.RankOf(square));

    private static PieceType PieceFromChar(char c) => c switch
    {
        'N' => PieceType.Knight,
        'B' => PieceType.Bishop,
        'R' => PieceType.Rook,
        'Q' => PieceType.Queen,
        'K' => PieceType.King,
        _ => PieceType.Pawn,
    };

    private static char CharFromPiece(PieceType type) => type switch
    {
        PieceType.Knight => 'N',
        PieceType.Bishop => 'B',
        PieceType.Rook => 'R',
        PieceType.Queen => 'Q',
        PieceType.King => 'K',
        _ => 'P',
    };
}
