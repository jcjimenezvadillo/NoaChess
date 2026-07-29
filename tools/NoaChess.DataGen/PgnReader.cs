using System.Text;

namespace NoaChess.DataGen;

// Minimal PGN reader for opening-book seeding: yields each game's main-line
// move list as SAN tokens. It deliberately ignores tag pairs, comments {...}
// (the clock annotations), recursive variations (...) and NAGs ($n) — all we
// need are the moves actually played, in order.
public static class PgnReader
{
    public static IEnumerable<List<string>> ReadGames(TextReader reader)
    {
        // A game is a tag section followed by movetext. We accumulate movetext
        // lines and flush the game when the next tag section begins (or at EOF).
        var moveText = new StringBuilder();
        bool inMoves = false;

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith('['))
            {
                if (inMoves)
                {
                    List<string> game = ParseMoveText(moveText.ToString());
                    if (game.Count > 0)
                        yield return game;
                    moveText.Clear();
                    inMoves = false;
                }
                continue; // tag pair: ignored
            }
            if (trimmed.Length == 0)
                continue;
            inMoves = true;
            moveText.Append(' ').Append(line);
        }

        if (inMoves)
        {
            List<string> game = ParseMoveText(moveText.ToString());
            if (game.Count > 0)
                yield return game;
        }
    }

    // Strips comments, variations and NAGs, then keeps the main-line SAN tokens,
    // dropping move numbers and the game result.
    private static List<string> ParseMoveText(string text)
    {
        var moves = new List<string>();
        var token = new StringBuilder();
        int brace = 0, paren = 0;

        foreach (char c in text)
        {
            if (brace > 0) { if (c == '}') brace--; continue; }
            if (paren > 0) { if (c == '(') paren++; else if (c == ')') paren--; continue; }

            switch (c)
            {
                case '{': brace++; Flush(token, moves); break;
                case '(': paren++; Flush(token, moves); break;
                case ' ' or '\t' or '\r' or '\n': Flush(token, moves); break;
                default: token.Append(c); break;
            }
        }
        Flush(token, moves);
        return moves;
    }

    private static void Flush(StringBuilder token, List<string> moves)
    {
        if (token.Length == 0)
            return;
        string s = token.ToString();
        token.Clear();

        if (s is "1-0" or "0-1" or "1/2-1/2" or "*")
            return; // game result
        if (s[0] == '$')
            return; // NAG

        // Strip a leading move-number prefix (digits and dots): "12.", "12...",
        // "12.Nf3", "1...Nf6". What remains is the move (empty if it was a pure
        // number token).
        int i = 0;
        while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '.'))
            i++;
        string move = s[i..];
        if (move.Length > 0)
            moves.Add(move);
    }
}
