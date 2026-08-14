using System.IO;
using NoaChess.GUI.Wpf.Models;

namespace NoaChess.GUI.Wpf.Services;

// The external engines the user has added, remembered between runs.
//
// An engine is only added after it has actually answered "uciok": the catalogue
// holds programs that have proved they are engines, not paths that might be.
public sealed class EngineCatalog
{
    private readonly List<PlayerSetup> _engines = [];

    public IReadOnlyList<PlayerSetup> Engines => _engines;

    public void Load(IEnumerable<string> serialised)
    {
        _engines.Clear();
        foreach (string entry in serialised)
        {
            PlayerSetup setup = PlayerSetup.Parse(entry);
            if (setup.Kind == PlayerKind.External && setup.Path.Length > 0)
                _engines.Add(setup);
        }
    }

    public List<string> Save() => _engines.Select(e => e.Serialise()).ToList();

    // Adds an engine, replacing any earlier entry for the same file so the list
    // cannot fill up with the same program under different names.
    public void Add(PlayerSetup engine)
    {
        _engines.RemoveAll(e => string.Equals(e.Path, engine.Path,
                                              StringComparison.OrdinalIgnoreCase));
        _engines.Add(engine);
    }

    public void Remove(PlayerSetup engine) =>
        _engines.RemoveAll(e => string.Equals(e.Path, engine.Path,
                                              StringComparison.OrdinalIgnoreCase));

    // Engines whose file is still where it was. A path that has moved is worth
    // saying so about rather than failing at the start of a game.
    public bool Exists(PlayerSetup engine) => File.Exists(engine.Path);
}
