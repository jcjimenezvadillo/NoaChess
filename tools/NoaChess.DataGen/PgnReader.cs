using System.Text;

namespace NoaChess.DataGen;

// One game: its main-line SAN tokens plus the final result, from WHITE's point
// of view (+1 white won, 0 draw, -1 black won, NoResult for an unfinished "*").
// The result is what makes elite-game WDL anchoring possible — it is the one
// signal the engine's own search cannot manufacture for itself.
// HasBot is true when either side carried [WhiteTitle "BOT"] / [BlackTitle "BOT"],
// i.e. the game was played by an engine account. Those games must be excluded
// from WDL anchoring: the whole point of a real game's outcome is that it is
// EXTERNAL information, and an engine's result is just another engine's opinion
// played out — exactly the self-play signal that was already measured worthless.
public readonly record struct PgnGame(List<string> Moves, int Result, bool HasBot)
{
    public const int NoResult = int.MinValue;
    public bool HasResult => Result != NoResult;
}

// Minimal PGN reader for opening-book seeding: yields each game's main-line
// move list as SAN tokens plus its result. It deliberately ignores tag pairs,
// comments {...} (the clock annotations), recursive variations (...) and NAGs
// ($n) — all we need are the moves actually played, in order.
public static class PgnReader
{
    public static IEnumerable<PgnGame> ReadGames(TextReader reader)
    {
        // A game is a tag section followed by movetext. We accumulate movetext
        // lines and flush the game when the next tag section begins (or at EOF).
        var moveText = new StringBuilder();
        bool inMoves = false;
        // Tag pairs precede their own game's movetext, so this flag belongs to
        // the game currently being accumulated and is reset once that game is
        // emitted — not when the tag block is seen.
        bool botSeen = false;

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith('['))
            {
                if (inMoves)
                {
                    PgnGame game = ParseMoveText(moveText.ToString(), botSeen);
                    if (game.Moves.Count > 0)
                        yield return game;
                    moveText.Clear();
                    inMoves = false;
                    botSeen = false; // this tag opens the NEXT game
                }
                // Only the title tags are read; everything else stays ignored.
                if ((trimmed.StartsWith("[WhiteTitle ") || trimmed.StartsWith("[BlackTitle "))
                    && trimmed.Contains("\"BOT\"", StringComparison.Ordinal))
                    botSeen = true;
                continue;
            }
            if (trimmed.Length == 0)
                continue;
            inMoves = true;
            moveText.Append(' ').Append(line);
        }

        if (inMoves)
        {
            PgnGame game = ParseMoveText(moveText.ToString(), botSeen);
            if (game.Moves.Count > 0)
                yield return game;
        }
    }

    // Strips comments, variations and NAGs, then keeps the main-line SAN tokens,
    // dropping move numbers. The result token is not discarded: it is captured
    // and returned alongside the moves.
    private static PgnGame ParseMoveText(string text, bool hasBot)
    {
        var moves = new List<string>();
        var token = new StringBuilder();
        int brace = 0, paren = 0;
        int result = PgnGame.NoResult;

        foreach (char c in text)
        {
            if (brace > 0) { if (c == '}') brace--; continue; }
            if (paren > 0) { if (c == '(') paren++; else if (c == ')') paren--; continue; }

            switch (c)
            {
                case '{': brace++; Flush(token, moves, ref result); break;
                case '(': paren++; Flush(token, moves, ref result); break;
                case ' ' or '\t' or '\r' or '\n': Flush(token, moves, ref result); break;
                default: token.Append(c); break;
            }
        }
        Flush(token, moves, ref result);
        return new PgnGame(moves, result, hasBot);
    }

    private static void Flush(StringBuilder token, List<string> moves, ref int result)
    {
        if (token.Length == 0)
            return;
        string s = token.ToString();
        token.Clear();

        // Game result: captured (from White's point of view), not discarded.
        // "*" is an unfinished game and leaves the result unknown.
        switch (s)
        {
            case "1-0": result = 1; return;
            case "0-1": result = -1; return;
            case "1/2-1/2": result = 0; return;
            case "*": return;
        }
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
