namespace NoaChess.GUI.Wpf.Models;

// How the engine's thinking is limited.
public enum TimeControlKind
{
    MoveTime, // a fixed budget for every move, the same all game
    Clock,    // a real chess clock: a starting time plus an increment per move
    Depth     // a fixed search depth, with no clock at all
}

// The time control of a game. A record because it is a value the New Game
// dialog hands over whole and nothing mutates afterwards.
public sealed record TimeControl(TimeControlKind Kind, int MoveTimeMs, int BaseMs, int IncrementMs, int Depth)
{
    public static TimeControl PerMove(int milliseconds) =>
        new(TimeControlKind.MoveTime, milliseconds, 0, 0, 0);

    // A clock game: 'baseMs' on the clock at the start, 'incrementMs' added
    // after every move played.
    public static TimeControl Game(int baseMs, int incrementMs) =>
        new(TimeControlKind.Clock, 0, baseMs, incrementMs, 0);

    public static TimeControl FixedDepth(int depth) =>
        new(TimeControlKind.Depth, 0, 0, 0, depth);

    public static TimeControl Default => PerMove(3000);

    // True when the window has to show and run two clocks.
    public bool HasClock => Kind == TimeControlKind.Clock;

    // How it reads in the status bar and in the New Game dialog. Clock games
    // use the notation every chess site uses: minutes + increment seconds.
    public string Describe() => Kind switch
    {
        TimeControlKind.MoveTime => MoveTimeMs >= 1000
            ? $"{MoveTimeMs / 1000.0:0.#} s per move"
            : $"{MoveTimeMs} ms per move",
        TimeControlKind.Clock => $"{BaseMs / 60000.0:0.#}+{IncrementMs / 1000}",
        _ => $"depth {Depth}",
    };
}
