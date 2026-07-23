using System.Numerics;
using NoaChess.UCI;

// Entry point of the UCI console host. All the logic lives in UciLoop so it
// is testable; here we print the startup banner and start the loop over
// stdin/stdout.
//
// The banner is plain text emitted BEFORE any UCI handshake: GUIs ignore it
// (the protocol only starts at "uci") and humans launching the exe by hand
// get a friendly identification, like every classic engine does.
Console.WriteLine($"{UciLoop.EngineName} {UciLoop.EngineVersion} by {UciLoop.EngineAuthor}");
Console.WriteLine($"UCI chess engine, C# on .NET {Environment.Version} " +
                  $"({(Vector.IsHardwareAccelerated ? $"SIMD x{Vector<short>.Count}" : "scalar")}, " +
                  $"{Environment.ProcessorCount} cores)");
Console.WriteLine("Type 'uci' for GUI mode, 'quit' to exit.");
Console.WriteLine();

new UciLoop(Console.In, Console.Out).Run();
