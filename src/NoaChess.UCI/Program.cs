using NoaChess.UCI;

// Entry point of the UCI console host. All the logic lives in UciLoop so it is
// testable; here we just start the loop over stdin/stdout.
new UciLoop(Console.In, Console.Out).Run();
