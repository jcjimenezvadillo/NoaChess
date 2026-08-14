using System.Text;

namespace NoaChess.Core;

// One game read from a PGN file: its tag pairs and its main line, already
// separated from the annotations that surround them.
public sealed class PgnGame
{
    // Tag pairs in the order they were read, keyed by the tag name.
    public Dictionary<string, string> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);

    // The main line, as SAN tokens. Variations, comments and glyphs are gone.
    public List<string> Moves { get; } = [];

    public string Result => Tags.TryGetValue("Result", out string? r) ? r : "*";

    // Games that do not start from the initial array carry the position in a
    // FEN tag. Everything else starts where chess starts.
    public string StartFen =>
        Tags.TryGetValue("FEN", out string? fen) && !string.IsNullOrWhiteSpace(fen)
            ? fen.Trim()
            : Board.StartFen;

    public string Get(string tag, string fallback = "") =>
        Tags.TryGetValue(tag, out string? value) && value.Length > 0 ? value : fallback;
}

// Reader and writer of PGN, the format every chess program exchanges games in.
//
// The reader keeps the MAIN LINE only. Variations, comments and numeric glyphs
// are recognised so they can be skipped correctly - a variation can nest, and a
// comment can contain anything including parentheses - but they are not kept:
// the board this feeds shows one game at a time.
//
// Nothing here resolves a move. The tokens come out as text and are handed to
// San.TryParse against the position they belong to, which is the only thing
// that can tell "Nf3" from an illegal claim.
public static class Pgn
{
    // Splits a file into games and parses each one. A file with no tag pairs at
    // all is still read as a single game, since bare movetext is common in
    // pasted fragments.
    public static List<PgnGame> ParseAll(string text)
    {
        var games = new List<PgnGame>();
        foreach (string chunk in SplitGames(text))
        {
            PgnGame game = ParseOne(chunk);
            if (game.Moves.Count > 0 || game.Tags.Count > 0)
                games.Add(game);
        }
        return games;
    }

    // Reads the first game, which is what "open this file" means when the file
    // happens to hold a collection.
    public static bool TryParseFirst(string text, out PgnGame game)
    {
        List<PgnGame> games = ParseAll(text);
        game = games.Count > 0 ? games[0] : new PgnGame();
        return games.Count > 0;
    }

    // A new game starts at a tag section that follows movetext. Scanning line by
    // line is enough: the standard puts each tag pair on its own line.
    private static IEnumerable<string> SplitGames(string text)
    {
        var current = new StringBuilder();
        bool seenMovetext = false;

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            bool isTag = line.TrimStart().StartsWith('[');

            if (isTag && seenMovetext)
            {
                yield return current.ToString();
                current.Clear();
                seenMovetext = false;
            }

            if (!isTag && line.Trim().Length > 0)
                seenMovetext = true;

            current.Append(line).Append('\n');
        }

        if (current.Length > 0)
            yield return current.ToString();
    }

    private static PgnGame ParseOne(string text)
    {
        var game = new PgnGame();
        var movetext = new StringBuilder();

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith('[') && TryReadTag(line, out string name, out string value))
            {
                game.Tags[name] = value;
                continue;
            }

            movetext.Append(line).Append(' ');
        }

        ReadMoves(movetext.ToString(), game);
        return game;
    }

    // [Event "Casual game"] -> ("Event", "Casual game")
    private static bool TryReadTag(string line, out string name, out string value)
    {
        name = value = "";

        int close = line.LastIndexOf(']');
        if (close < 0)
            return false;

        string body = line[1..close].Trim();
        int firstQuote = body.IndexOf('"');
        int lastQuote = body.LastIndexOf('"');
        if (firstQuote < 0 || lastQuote <= firstQuote)
            return false;

        name = body[..firstQuote].Trim();
        value = body[(firstQuote + 1)..lastQuote].Replace("\\\"", "\"").Replace("\\\\", "\\");
        return name.Length > 0;
    }

    // Walks the movetext once, skipping everything that is not a move of the
    // main line.
    private static void ReadMoves(string movetext, PgnGame game)
    {
        var token = new StringBuilder(12);
        int variationDepth = 0;

        for (int i = 0; i < movetext.Length; i++)
        {
            char c = movetext[i];

            switch (c)
            {
                case '{':
                    // Brace comment: runs to the matching brace and may contain
                    // anything, so nothing inside it is inspected.
                    Flush(token, game, variationDepth);
                    while (i < movetext.Length && movetext[i] != '}')
                        i++;
                    continue;

                case ';':
                    // Rest-of-line comment. The lines were joined with spaces, so
                    // there is no newline left to find: it ends the movetext.
                    Flush(token, game, variationDepth);
                    return;

                case '(':
                    Flush(token, game, variationDepth);
                    variationDepth++;
                    continue;

                case ')':
                    Flush(token, game, variationDepth);
                    if (variationDepth > 0)
                        variationDepth--;
                    continue;

                case '<':
                    // Reserved by the standard for future expansion; skipped whole.
                    Flush(token, game, variationDepth);
                    while (i < movetext.Length && movetext[i] != '>')
                        i++;
                    continue;
            }

            if (char.IsWhiteSpace(c))
            {
                Flush(token, game, variationDepth);
                continue;
            }

            token.Append(c);
        }

        Flush(token, game, variationDepth);
    }

    // Decides whether one whitespace-delimited token is a move of the main line.
    private static void Flush(StringBuilder token, PgnGame game, int variationDepth)
    {
        if (token.Length == 0)
            return;

        string text = token.ToString();
        token.Clear();

        if (variationDepth > 0)
            return; // inside a variation: recognised, not kept

        // Numeric annotation glyph.
        if (text[0] == '$')
            return;

        // Result token. It also ends the game, but the caller has already split
        // the games, so recording it is enough.
        if (text is "1-0" or "0-1" or "1/2-1/2" or "*")
        {
            game.Tags.TryAdd("Result", text);
            return;
        }

        // Move number: "12." or "12..." - and the form "12.e4" with no space,
        // which is legal and which a naive split would lose entirely.
        int digits = 0;
        while (digits < text.Length && char.IsAsciiDigit(text[digits]))
            digits++;
        if (digits > 0)
        {
            int rest = digits;
            while (rest < text.Length && text[rest] == '.')
                rest++;
            if (rest == text.Length)
                return;              // pure move number
            if (rest > digits)
                text = text[rest..];  // number glued to the move
            else
                return;               // a bare number that is not a move number
        }

        // "--" and "Z0" are null-move conventions; there is no such move here.
        if (text is "--" or "Z0")
            return;

        game.Moves.Add(text);
    }

    // Writes a game. 'moves' are SAN tokens in order, starting from the position
    // the FEN tag describes (or the initial array when there is none).
    public static string Write(IReadOnlyDictionary<string, string> tags,
                               IReadOnlyList<string> moves,
                               int firstMoveNumber, bool blackMovesFirst)
    {
        var pgn = new StringBuilder();

        // The seven tag roster, in the order the standard fixes, then the rest.
        string[] roster = ["Event", "Site", "Date", "Round", "White", "Black", "Result"];
        foreach (string name in roster)
            pgn.Append($"[{name} \"{Escape(Value(tags, name))}\"]\n");
        foreach (KeyValuePair<string, string> tag in tags)
        {
            if (!roster.Contains(tag.Key, StringComparer.OrdinalIgnoreCase))
                pgn.Append($"[{tag.Key} \"{Escape(tag.Value)}\"]\n");
        }
        pgn.Append('\n');

        int column = 0;
        int moveNumber = firstMoveNumber;
        bool blackToMove = blackMovesFirst;

        for (int i = 0; i < moves.Count; i++)
        {
            string token = blackToMove
                ? (i == 0 ? $"{moveNumber}... {moves[i]}" : moves[i])
                : $"{moveNumber}. {moves[i]}";

            if (column + token.Length + 1 > 80)
            {
                pgn.Append('\n');
                column = 0;
            }
            else if (column > 0)
            {
                pgn.Append(' ');
                column++;
            }

            pgn.Append(token);
            column += token.Length;

            if (blackToMove)
                moveNumber++;
            blackToMove = !blackToMove;
        }

        string result = Value(tags, "Result");
        if (column + result.Length + 1 > 80)
            pgn.Append('\n');
        else if (column > 0)
            pgn.Append(' ');
        pgn.Append(result).Append('\n');

        return pgn.ToString();
    }

    private static string Value(IReadOnlyDictionary<string, string> tags, string name)
    {
        if (tags.TryGetValue(name, out string? value) && value.Length > 0)
            return value;
        return name == "Result" ? "*" : "?";
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
