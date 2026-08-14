using NoaChess.Core;
using NoaChess.Engine.Evaluation.Nnue;

namespace NoaChess.Engine.Tests;

// Pins the threat feature schema and its parity with the Python trainer.
//
// This project's recurring failure has never been the algorithm, it has been
// two implementations of it drifting apart: a stale feature cache trained one
// generation on the wrong data, and an exporter default silently changed the
// quantisation of another. So the C# indices are not compared against my own
// expectations here, they are compared against a fixture produced by the Python
// encoder, which is itself checked against python-chess. Three independent
// implementations have to agree before a net is trained on any of it.
public class ThreatFeatureIndexTests
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "threat_parity.txt");

    [Fact]
    public void SchemaDimensionsMatchTheReference()
    {
        Assert.Equal(60720, ThreatFeatureIndex.InputSize);
        Assert.Equal(128, ThreatFeatureIndex.MaxActiveFeatures);
    }

    [Fact]
    public void EveryRecordedRelationHasItsOwnIndex()
    {
        // Injectivity is the property that matters: two different threats
        // sharing an index would train one weight on two unrelated facts. The
        // sweep is exhaustive over all 12 x 12 x 64 x 64 combinations.
        var seen = new Dictionary<int, (int, int, int, int)>();
        int recorded = 0, outOfRange = 0, collisions = 0;

        for (int a = 0; a < 12; a++)
        for (int d = 0; d < 12; d++)
        for (int from = 0; from < 64; from++)
        for (int to = 0; to < 64; to++)
        {
            int index = ThreatFeatureIndex.Index(
                Color.White, 0,
                (Color)(a / 6), (PieceType)(a % 6), from,
                (Color)(d / 6), (PieceType)(d % 6), to);
            if (index < 0)
                continue;

            recorded++;
            if (index < 0 || index >= ThreatFeatureIndex.InputSize)
                outOfRange++;
            if (seen.TryGetValue(index, out var previous) && previous != (a, from, d, to))
                collisions++;
            seen[index] = (a, from, d, to);
        }

        Assert.Equal(0, outOfRange);
        Assert.Equal(0, collisions);
        Assert.Equal(recorded, seen.Count);

        // The exact figure is pinned so that a change to the packing fails here
        // rather than quietly invalidating every net trained before it. The gap
        // to 60,720 is the deduplication of symmetric pairs.
        Assert.Equal(54092, recorded);
    }

    [Fact]
    public void APairThatIsNotAnAttackHasNoIndex()
    {
        // A white pawn on e5 does not attack e4: pawns do not push backwards.
        // Before the geometry guard this returned a valid-looking index that
        // belonged to an unrelated relation.
        const int E4 = 28, E5 = 36;
        Assert.Equal(-1, ThreatFeatureIndex.Index(
            Color.White, 0, Color.White, PieceType.Pawn, E5,
            Color.White, PieceType.Pawn, E4));

        // The same pawn blocked from in front IS a feature.
        Assert.True(ThreatFeatureIndex.Index(
            Color.White, 0, Color.White, PieceType.Pawn, E4,
            Color.White, PieceType.Pawn, E5) >= 0);
    }

    [Fact]
    public void SymmetricRelationsAreCountedOnce()
    {
        // Two knights attacking each other is one fact, not two. Exactly one of
        // the two directions may produce an index.
        for (int from = 0; from < 64; from++)
        for (int to = 0; to < 64; to++)
        {
            bool forward = ThreatFeatureIndex.Index(
                Color.White, 0, Color.White, PieceType.Knight, from,
                Color.Black, PieceType.Knight, to) >= 0;
            bool backward = ThreatFeatureIndex.Index(
                Color.White, 0, Color.Black, PieceType.Knight, to,
                Color.White, PieceType.Knight, from) >= 0;

            if (Attacks.Knight(from) >> to is var reachable && (reachable & 1) != 0)
                Assert.True(forward ^ backward,
                    $"knight {from}->{to} produced {forward} and {backward}");
        }
    }

    [Fact]
    public void ActiveFeaturesMatchThePythonEncoder()
    {
        Assert.True(File.Exists(FixturePath),
            $"missing parity fixture at {FixturePath}; regenerate it from tools/training/nnue");

        Span<int> buffer = stackalloc int[ThreatFeatureIndex.MaxActiveFeatures];
        int positions = 0, mismatches = 0;
        var failures = new List<string>();

        foreach (string line in File.ReadLines(FixturePath))
        {
            if (line.Length == 0 || line[0] == '#')
                continue;

            string[] parts = line.Split('|');
            var board = new Board(parts[0]);
            var perspective = (Color)int.Parse(parts[1]);

            int[] expected = parts[2].Length == 0
                ? []
                : parts[2].Split(',').Select(int.Parse).ToArray();

            int count = ThreatFeatureIndex.ActiveFeatures(board, perspective, buffer);
            int[] actual = buffer[..count].ToArray();
            Array.Sort(actual);

            positions++;
            if (!expected.SequenceEqual(actual))
            {
                mismatches++;
                if (failures.Count < 3)
                    failures.Add($"{parts[0]} perspective {parts[1]}: "
                               + $"expected {expected.Length} features, got {actual.Length}; "
                               + $"missing [{string.Join(",", expected.Except(actual).Take(6))}] "
                               + $"extra [{string.Join(",", actual.Except(expected).Take(6))}]");
            }
        }

        Assert.True(positions >= 200, $"fixture only covered {positions} cases");
        Assert.True(mismatches == 0,
            $"{mismatches} of {positions} cases differ from Python:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void ActiveFeatureCountStaysWithinTheBuffer()
    {
        Span<int> buffer = stackalloc int[ThreatFeatureIndex.MaxActiveFeatures];
        foreach (string line in File.ReadLines(FixturePath))
        {
            if (line.Length == 0 || line[0] == '#')
                continue;
            string[] parts = line.Split('|');
            var board = new Board(parts[0]);
            int count = ThreatFeatureIndex.ActiveFeatures(board, (Color)int.Parse(parts[1]), buffer);
            Assert.InRange(count, 0, ThreatFeatureIndex.MaxActiveFeatures);
        }
    }

    [Fact]
    public void PackingShapeMatchesTheTrainer()
    {
        // Per-piece (block start, span), built independently by the Python
        // encoder. Pinned because when the two implementations disagree this
        // localises the drift to one piece instead of leaving 60,720 indices to
        // search - it is how the orientation bug was found in minutes: these
        // twelve pairs matched exactly, which ruled the packing out and left
        // only the mirror.
        int[] spans = [132, 336, 560, 896, 1456, 420];
        int[] starts = [0, 792, 4152, 8632, 15800, 30360];

        for (int c = 0; c < 2; c++)
            for (int t = 0; t < 6; t++)
            {
                var (start, span) = ThreatFeatureIndex.Packing((Color)c, (PieceType)t);
                Assert.Equal(spans[t], span);
                Assert.Equal(starts[t] + c * 30360, start);
            }
    }
}
