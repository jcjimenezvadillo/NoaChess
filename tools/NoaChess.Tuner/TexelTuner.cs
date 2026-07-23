using NoaChess.Core;
using NoaChess.Engine.Evaluation.Classical;

namespace NoaChess.Tuner;

// The optimization core: error function (parallel over positions, one
// evaluator per worker because ClassicalEvaluator keeps per-call scratch
// state) and coordinate descent over the registered parameters.
public sealed class TexelTuner(List<Program.Position> positions)
{
    private readonly List<Program.Position> _positions = positions;

    // Mean squared error between the predicted win probability and the game
    // result over the whole position set, for a given sigmoid scale K.
    public double Error(double k)
    {
        double total = ParallelEnumerable
            .Range(0, _positions.Count)
            .GroupBy(i => i % Environment.ProcessorCount)
            .Select(group =>
            {
                var evaluator = new ClassicalEvaluator();
                double sum = 0;
                foreach (int i in group)
                {
                    var p = _positions[i];
                    int evalStm = evaluator.Evaluate(p.Board);
                    int evalWhite = p.Board.SideToMove == Color.White ? evalStm : -evalStm;
                    double predicted = 1.0 / (1.0 + Math.Pow(10.0, -k * evalWhite / 400.0));
                    double diff = predicted - p.ResultWhite;
                    sum += diff * diff;
                }
                return sum;
            })
            .Sum();
        return total / _positions.Count;
    }

    // Finds the sigmoid scale that best maps the CURRENT evaluation to the
    // results, by golden-section-ish bisection on the (convex) error curve.
    public double OptimizeK()
    {
        double lo = 0.5, hi = 2.5;
        for (int i = 0; i < 24; i++)
        {
            double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
            if (Error(m1) < Error(m2)) hi = m2; else lo = m1;
        }
        return (lo + hi) / 2;
    }

    // Classic texel coordinate descent: nudge each parameter by a step, keep
    // the change if the error drops, halving the step each pass.
    public void CoordinateDescent(List<TunableParam> parameters, double k, int passes)
    {
        double best = Error(k);
        int[] steps = passes >= 3 ? [8, 4, 2] : [4, 2];

        for (int pass = 0; pass < passes; pass++)
        {
            int step = steps[Math.Min(pass, steps.Length - 1)];
            int improved = 0;

            foreach (var param in parameters)
            {
                int original = param.Get();
                foreach (int delta in (ReadOnlySpan<int>)[step, -step])
                {
                    param.Set(original + delta);
                    double error = Error(k);
                    if (error < best - 1e-9)
                    {
                        best = error;
                        improved++;
                        original = param.Get();
                        break;
                    }
                    param.Set(original);
                }
            }
            Console.WriteLine($"pass {pass + 1}/{passes} (step {step}): {improved} params moved, error = {best:F6}");
        }
    }
}
