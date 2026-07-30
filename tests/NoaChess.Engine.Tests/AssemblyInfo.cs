using Xunit;

// The Syzygy tablebase registry (NoaChess.Engine.Tablebases.Syzygy) is a
// PROCESS-GLOBAL static: Init() disposes the memory-mapped view accessors and
// reloads them. SyzygyIntegrationTests reinitialises that registry, while other
// engine-search tests (e.g. SearchTests on a five-man position) probe whatever
// is currently loaded. Under xUnit's default cross-class parallelism the two
// race — a DTZ probe reads a view accessor a concurrent Init() has just
// disposed, throwing ObjectDisposedException. The race is invisible until real
// tablebases exist at the tests' hardcoded path, then it flakes at random.
//
// Serialising the assembly's tests removes it at no practical cost (the suite
// runs in a few seconds) and mirrors production, where a single engine owns one
// Syzygy.Init and search threads only ever READ the shared tables, never
// dispose them. This is a test-isolation fix; the engine itself is unchanged.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
