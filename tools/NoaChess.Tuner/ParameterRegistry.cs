using System.Text;
using NoaChess.Engine.Evaluation.Classical;

namespace NoaChess.Tuner;

// One tunable integer (the Mg or Eg half of a Score, or a table entry) with
// accessors that mutate EvaluationParams / PieceSquareTables in place.
public sealed record TunableParam(string Name, Func<int> Get, Action<int> Set);

// Builds the list of everything the tuner may touch, and renders the current
// values back as a paste-ready C# snippet. Material values stay fixed: they
// are the anchor that pins the centipawn scale. The PSTs ARE tuned — PeSTO is
// only the starting point; adapting the tables to this engine's search is
// where most of the tuning Elo lives.
public static class ParameterRegistry
{
    private static readonly string[] PieceNames =
        ["Pawn", "Knight", "Bishop", "Rook", "Queen", "King"];

    public static List<TunableParam> Build()
    {
        var list = new List<TunableParam>();

        AddScore(list, "BishopPair",
            () => EvaluationParams.BishopPair, v => EvaluationParams.BishopPair = v);
        AddScore(list, "RookOpenFile",
            () => EvaluationParams.RookOpenFile, v => EvaluationParams.RookOpenFile = v);
        AddScore(list, "RookSemiOpenFile",
            () => EvaluationParams.RookSemiOpenFile, v => EvaluationParams.RookSemiOpenFile = v);
        AddScore(list, "RookOnSeventh",
            () => EvaluationParams.RookOnSeventh, v => EvaluationParams.RookOnSeventh = v);
        AddScore(list, "KnightOutpost",
            () => EvaluationParams.KnightOutpost, v => EvaluationParams.KnightOutpost = v);
        AddScore(list, "SpacePerSquare",
            () => EvaluationParams.SpacePerSquare, v => EvaluationParams.SpacePerSquare = v);
        AddScore(list, "DoubledPawn",
            () => EvaluationParams.DoubledPawn, v => EvaluationParams.DoubledPawn = v);
        AddScore(list, "IsolatedPawn",
            () => EvaluationParams.IsolatedPawn, v => EvaluationParams.IsolatedPawn = v);
        AddScore(list, "ConnectedPassers",
            () => EvaluationParams.ConnectedPassers, v => EvaluationParams.ConnectedPassers = v);
        AddScore(list, "RookBehindPasser",
            () => EvaluationParams.RookBehindPasser, v => EvaluationParams.RookBehindPasser = v);
        AddScore(list, "BackwardPawn",
            () => EvaluationParams.BackwardPawn, v => EvaluationParams.BackwardPawn = v);

        // MobilityBonus (the non-linear reference tables, v2.6.2) is deliberately
        // NOT tunable: every texel run converges to negative endgame mobility for
        // the minors (spurious correlation: the winning side simplifies),
        // which plays disastrously. Reference values, rescaled x0.48, stay fixed.
        // The threat terms (also reference x0.48) stay fixed for the same reason:
        // they are SPRT-validated as a package, not texel-derived.
        AddScoreArray(list, "PassedPawn", EvaluationParams.PassedPawn, 1, 6);
        AddScoreArray(list, "Phalanx", EvaluationParams.Phalanx, 1, 6);

        // Piece-square tables: every square of every piece, both phases.
        // Pawn ranks 1 and 8 (indices 0-7 and 56-63) hold no pawns and stay 0.
        for (int piece = 0; piece < 6; piece++)
        {
            int from = piece == 0 ? 8 : 0;
            int to = piece == 0 ? 55 : 63;
            AddTable(list, $"Pst{PieceNames[piece]}Mg", PieceSquareTables.MgByPiece[piece], from, to);
            AddTable(list, $"Pst{PieceNames[piece]}Eg", PieceSquareTables.EgByPiece[piece], from, to);
        }

        return list;
    }

    // Only the 4E piece terms (v2.6.5), everything else frozen. The reference
    // values are calibrated against the reference's own PSTs; tuning them on
    // NoaChess self-play data re-calibrates them against the PeSTO PSTs and
    // the rest of the existing evaluation, which is what x0.48 scaling cannot do.
    public static List<TunableParam> Build4E()
    {
        var list = new List<TunableParam>();

        AddScore(list, "TrappedRook",
            () => EvaluationParams.TrappedRook, v => EvaluationParams.TrappedRook = v);
        AddScore(list, "RookOnClosedFile",
            () => EvaluationParams.RookOnClosedFile, v => EvaluationParams.RookOnClosedFile = v);
        AddScore(list, "LongDiagonalBishop",
            () => EvaluationParams.LongDiagonalBishop, v => EvaluationParams.LongDiagonalBishop = v);
        AddScore(list, "MinorBehindPawn",
            () => EvaluationParams.MinorBehindPawn, v => EvaluationParams.MinorBehindPawn = v);
        AddScore(list, "BishopXRayPawns",
            () => EvaluationParams.BishopXRayPawns, v => EvaluationParams.BishopXRayPawns = v);
        AddScore(list, "BishopOutpost",
            () => EvaluationParams.BishopOutpost, v => EvaluationParams.BishopOutpost = v);
        AddScore(list, "ReachableOutpost",
            () => EvaluationParams.ReachableOutpost, v => EvaluationParams.ReachableOutpost = v);
        AddScore(list, "UncontestedOutpost",
            () => EvaluationParams.UncontestedOutpost, v => EvaluationParams.UncontestedOutpost = v);
        AddScore(list, "WeakQueen",
            () => EvaluationParams.WeakQueen, v => EvaluationParams.WeakQueen = v);
        AddScore(list, "KnightOutpost",
            () => EvaluationParams.KnightOutpost, v => EvaluationParams.KnightOutpost = v);
        AddScoreArray(list, "KingProtector", EvaluationParams.KingProtector, 0, 1);
        AddScoreArray(list, "BishopPawns", EvaluationParams.BishopPawns, 0, 3);

        return list;
    }

    // Renders only the 4E values as a paste-ready snippet.
    public static string ToSnippet4E()
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Tuned 4E values (texel, NoaChess.Tuner) — paste into EvaluationParams:");
        Append(sb, "TrappedRook", EvaluationParams.TrappedRook);
        Append(sb, "RookOnClosedFile", EvaluationParams.RookOnClosedFile);
        Append(sb, "LongDiagonalBishop", EvaluationParams.LongDiagonalBishop);
        Append(sb, "MinorBehindPawn", EvaluationParams.MinorBehindPawn);
        Append(sb, "BishopXRayPawns", EvaluationParams.BishopXRayPawns);
        Append(sb, "BishopOutpost", EvaluationParams.BishopOutpost);
        Append(sb, "ReachableOutpost", EvaluationParams.ReachableOutpost);
        Append(sb, "UncontestedOutpost", EvaluationParams.UncontestedOutpost);
        Append(sb, "WeakQueen", EvaluationParams.WeakQueen);
        Append(sb, "KnightOutpost", EvaluationParams.KnightOutpost);
        AppendArray(sb, "KingProtector", EvaluationParams.KingProtector);
        AppendArray(sb, "BishopPawns", EvaluationParams.BishopPawns);
        return sb.ToString();
    }

    private static void AddScore(List<TunableParam> list, string name,
        Func<Score> get, Action<Score> set)
    {
        list.Add(new($"{name}.Mg", () => get().Mg, v => set(new Score(v, get().Eg))));
        list.Add(new($"{name}.Eg", () => get().Eg, v => set(new Score(get().Mg, v))));
    }

    private static void AddScoreArray(List<TunableParam> list, string name,
        Score[] array, int from, int to)
    {
        for (int i = from; i <= to; i++)
        {
            int idx = i;
            list.Add(new($"{name}[{idx}].Mg",
                () => array[idx].Mg, v => array[idx] = new Score(v, array[idx].Eg)));
            list.Add(new($"{name}[{idx}].Eg",
                () => array[idx].Eg, v => array[idx] = new Score(array[idx].Mg, v)));
        }
    }

    private static void AddTable(List<TunableParam> list, string name,
        int[] table, int from, int to)
    {
        for (int i = from; i <= to; i++)
        {
            int idx = i;
            list.Add(new($"{name}[{idx}]", () => table[idx], v => table[idx] = v));
        }
    }

    // Renders the current values of every tunable as C# assignments (scores)
    // plus full PST array literals, ready to paste back into the source.
    public static string ToSnippet()
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Tuned values (texel, NoaChess.Tuner) — paste into EvaluationParams:");
        Append(sb, "BishopPair", EvaluationParams.BishopPair);
        Append(sb, "RookOpenFile", EvaluationParams.RookOpenFile);
        Append(sb, "RookSemiOpenFile", EvaluationParams.RookSemiOpenFile);
        Append(sb, "RookOnSeventh", EvaluationParams.RookOnSeventh);
        Append(sb, "KnightOutpost", EvaluationParams.KnightOutpost);
        Append(sb, "SpacePerSquare", EvaluationParams.SpacePerSquare);
        Append(sb, "DoubledPawn", EvaluationParams.DoubledPawn);
        Append(sb, "IsolatedPawn", EvaluationParams.IsolatedPawn);
        Append(sb, "ConnectedPassers", EvaluationParams.ConnectedPassers);
        Append(sb, "RookBehindPasser", EvaluationParams.RookBehindPasser);
        Append(sb, "BackwardPawn", EvaluationParams.BackwardPawn);
        AppendArray(sb, "PassedPawn", EvaluationParams.PassedPawn);
        AppendArray(sb, "Phalanx", EvaluationParams.Phalanx);

        sb.AppendLine();
        sb.AppendLine("// Tuned PSTs — paste into PieceSquareTables (white POV, first row = rank 8):");
        for (int piece = 0; piece < 6; piece++)
        {
            AppendTable(sb, $"{PieceNames[piece]}Mg", PieceSquareTables.MgByPiece[piece]);
            AppendTable(sb, $"{PieceNames[piece]}Eg", PieceSquareTables.EgByPiece[piece]);
        }
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string name, Score s)
        => sb.AppendLine($"{name} = new({s.Mg}, {s.Eg});");

    private static void AppendArray(StringBuilder sb, string name, Score[] a)
    {
        sb.Append($"{name} = [ ");
        foreach (Score s in a)
            sb.Append($"new({s.Mg}, {s.Eg}), ");
        sb.AppendLine("];");
    }

    private static void AppendTable(StringBuilder sb, string name, int[] table)
    {
        sb.AppendLine($"private static readonly int[] {name} =");
        sb.AppendLine("[");
        for (int rank = 0; rank < 8; rank++)
        {
            sb.Append("    ");
            for (int file = 0; file < 8; file++)
                sb.Append($"{table[rank * 8 + file],5},");
            sb.AppendLine();
        }
        sb.AppendLine("];");
    }
}
