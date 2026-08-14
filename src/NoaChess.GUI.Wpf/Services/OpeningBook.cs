using System.IO;
using System.Windows;
using NoaChess.Core;
using NoaChess.GUI.Wpf.Models;

namespace NoaChess.GUI.Wpf.Services;

// One named opening.
public readonly record struct OpeningName(string? StoredEco, string? StoredName, int Ply)
{
    // The stored fields are nullable and the read ones are not, which is the
    // only arrangement that makes this struct safe. "No opening" is its DEFAULT
    // value - every game set up from a position, every first move outside the
    // table - and a default struct has null strings whatever the declaration
    // says. Handing those out and asking each caller to remember cost two
    // separate crashes: one in the opening line above the move list, one in
    // saving the PGN. Nobody reads a null from here now.
    public string Eco => StoredEco ?? "";
    public string Name => StoredName ?? "";

    public bool IsKnown => Name.Length > 0;

    // "B90  Sicilian, Najdorf Variation"
    public string Display => IsKnown ? $"{Eco}  {Name}" : "";
}

// Names the opening a game is in, the way a chess program shows it above the
// notation.
//
// Matching is by POSITION rather than by move order. Every line of the table is
// replayed once at startup and indexed by the Zobrist key of the position it
// reaches, so a line arrived at by transposition gets the same name as the
// direct route - which is the whole point of naming openings by position and
// the reason chess databases do it this way.
//
// The table is a compact list of main lines, not the full ECO index, and it is
// a plain text file on purpose: it ships with the program and anyone can add to
// it without a rebuild being anybody's problem.
public sealed class OpeningBook
{
    private readonly Dictionary<ulong, OpeningName> _byPosition = [];

    // Lines that failed to load, for the one place that wants to know: the
    // check that proves the shipped table is sound.
    public List<string> Problems { get; } = [];

    public int Count => _byPosition.Count;

    public static OpeningBook Shipped { get; } = LoadShipped();

    private static OpeningBook LoadShipped()
    {
        var book = new OpeningBook();
        try
        {
            var uri = new Uri("pack://application:,,,/NoaChess.GUI.Wpf;component/Resources/openings.tsv");
            using Stream stream = Application.GetResourceStream(uri)!.Stream;
            using var reader = new StreamReader(stream);
            book.Load(reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            // A missing table costs the opening name and nothing else. It must
            // never be the reason the board does not open.
            book.Problems.Add($"The opening table could not be read: {ex.Message}");
        }
        return book;
    }

    public void Load(string text)
    {
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            string[] fields = line.Split('\t', StringSplitOptions.TrimEntries);
            if (fields.Length < 3)
            {
                Problems.Add($"Not three tab-separated fields: '{line}'");
                continue;
            }

            var board = new Board();
            string[] moves = fields[2].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool ok = true;

            foreach (string san in moves)
            {
                if (!San.TryParse(board, san, out Move move))
                {
                    Problems.Add($"'{fields[1]}': '{san}' is not legal in {Fen.Save(board)}");
                    ok = false;
                    break;
                }
                board.MakeMove(move);
            }

            if (!ok)
                continue;

            // A position reached by two entries keeps the LONGER line, which is
            // always the more specific name.
            var entry = new OpeningName(fields[0], fields[1], moves.Length);
            if (!_byPosition.TryGetValue(board.ZobristKey, out OpeningName existing)
                || entry.Ply > existing.Ply)
            {
                _byPosition[board.ZobristKey] = entry;
            }
        }
    }

    // The name of the opening a game is in after 'ply' moves.
    //
    // The DEEPEST match along the line played wins, and it sticks: once a game
    // leaves the book it keeps the last name it earned, which is what a player
    // means by "this is a Najdorf" thirty moves later.
    public OpeningName Identify(string startFen, IReadOnlyList<PlayedMove> moves, int ply)
    {
        // A game set up from a position is not in any opening: the moves that
        // would have named it were never played.
        if (startFen != Board.StartFen)
            return default;

        var board = new Board();
        OpeningName found = default;

        if (_byPosition.TryGetValue(board.ZobristKey, out OpeningName atStart))
            found = atStart;

        for (int i = 0; i < ply && i < moves.Count; i++)
        {
            board.MakeMove(moves[i].Move);
            if (_byPosition.TryGetValue(board.ZobristKey, out OpeningName here))
                found = here;
        }

        return found;
    }
}
