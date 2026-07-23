using BenchmarkDotNet.Running;

// Entry point: lets the command line pick which benchmark class to run,
// e.g. `dotnet run -c Release -- --filter *MoveGeneration*`, or shows an
// interactive menu when run without arguments.
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

public partial class Program;
