namespace NoaChess.GUI.Wpf.Models;

public enum PlayerKind
{
    Human,
    Builtin,   // NoaChess itself, in process
    External,  // a UCI engine started as a child process
}

// Who is playing one side of the board.
//
// Each colour carries its own, which is what makes human against engine, engine
// against engine and analysis the same arrangement with different values rather
// than three separate modes.
public sealed record PlayerSetup(PlayerKind Kind, string Name, string Path)
{
    public static PlayerSetup Human { get; } = new(PlayerKind.Human, "You", "");

    public static PlayerSetup Builtin { get; } = new(PlayerKind.Builtin, "NoaChess", "");

    public static PlayerSetup External(string name, string path) =>
        new(PlayerKind.External, name, path);

    public bool IsEngine => Kind != PlayerKind.Human;

    // How it is written into the settings file, and read back.
    public string Serialise() => Kind switch
    {
        PlayerKind.Human => "human",
        PlayerKind.Builtin => "builtin",
        _ => $"uci|{Name}|{Path}",
    };

    public static PlayerSetup Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Human;
        if (text == "builtin")
            return Builtin;

        string[] parts = text.Split('|');
        if (parts.Length == 3 && parts[0] == "uci")
            return External(parts[1], parts[2]);

        return Human;
    }
}
