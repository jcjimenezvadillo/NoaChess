using System.Numerics;
using NoaChess.UCI;

// Entry point of the UCI console host. All the logic lives in UciLoop so it
// is testable; here we print the startup banner and start the loop over
// stdin/stdout.
//
// The banner is a friendly identification for a human launching the exe by
// hand in a terminal. It is emitted ONLY when stdin is a real console: a GUI
// or bot (lichess-bot, Arena, cutechess) pipes stdin, and a strict UCI reader
// must see NOTHING before the protocol starts at "uci" - any text before the
// first "id"/"uciok" can desync or hang it. Console.IsInputRedirected is true
// exactly when the input is piped, so the guard keeps the console output clean
// for every automated driver while preserving the banner for interactive use.
if (!Console.IsInputRedirected)
{
    Console.WriteLine($"{UciLoop.EngineName} {UciLoop.EngineVersion} by {UciLoop.EngineAuthor}");
    Console.WriteLine($"UCI chess engine, C# on .NET {Environment.Version} " +
                      $"({(Vector.IsHardwareAccelerated ? $"SIMD x{Vector<short>.Count}" : "scalar")}, " +
                      $"{Environment.ProcessorCount} cores)");
    Console.WriteLine("Type 'uci' for GUI mode, 'quit' to exit.");
    Console.WriteLine();
}

new UciLoop(Console.In, Console.Out).Run();
