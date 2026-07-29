namespace NoaChess.Core;

// Parser for SAN (Standard Algebraic Notation) chess moves — "Nf3", "exd5",
// "O-O", "e8=Q+", "Qh4xe1#". A SAN token is resolved against the position's
// LEGAL moves: matching a generated move is the only reliable way to attach the
// correct flags (capture, en passant, castle, promotion) to a bare notation
// string. Sibling of Fen. Used to replay PGN games (opening-book seeding for
// datagen); not on any hot path, so clarity beats micro-optimization.
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

    private static PieceType PieceFromChar(char c) => c switch
    {
        'N' => PieceType.Knight,
        'B' => PieceType.Bishop,
        'R' => PieceType.Rook,
        'Q' => PieceType.Queen,
        'K' => PieceType.King,
        _ => PieceType.Pawn,
    };
}
