using System.Windows.Threading;
using NoaChess.GUI.Wpf.Models;
using Color = NoaChess.Core.Color;

namespace NoaChess.GUI.Wpf.ViewModels;

// Two chess clocks. One side's time drains at a time, and only while the game
// is really in progress.
//
// The remaining time is measured with a Stopwatch rather than by counting timer
// ticks. A DispatcherTimer is not a metronome - it fires late whenever the UI
// thread is busy, and the engine's own progress reports keep it busy - so
// subtracting the nominal interval on each tick would run the clocks slow, in
// the player's favour and against the budget the engine was given.
//
// Whether the clock should be running is decided in ONE place, by the window,
// on every position change. This class only obeys.
public sealed class GameClock : ViewModelBase
{
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly System.Diagnostics.Stopwatch _since = new();

    private TimeControl _control = TimeControl.Default;
    private long _whiteMs;
    private long _blackMs;
    private Color _draining = Color.White;
    private bool _isRunning;

    // Raised when a side runs out of time, with the colour that flagged.
    public event Action<Color>? Flagged;

    public GameClock() => _ticker.Tick += (_, _) => Drain();

    public bool IsVisible => _control.HasClock;

    public long WhiteMs => _whiteMs;
    public long BlackMs => _blackMs;

    // Puts fresh clocks on the board for a new game. They are not started here:
    // the window starts them once the game is genuinely under way, so an
    // application that is merely open burns nobody's time.
    public void Reset(TimeControl control)
    {
        _control = control;
        _isRunning = false;
        _ticker.Stop();
        _since.Reset();

        _whiteMs = control.HasClock ? control.BaseMs : 0;
        _blackMs = _whiteMs;
        _draining = Color.White;

        Notify();
        OnPropertyChanged(nameof(IsVisible));
    }

    // The single switch. Charges whatever has elapsed to whoever was thinking
    // BEFORE changing anything, so no time is lost or double counted when the
    // turn passes.
    public void SetRunning(bool run, Color sideToMove)
    {
        if (_isRunning)
            Drain();

        _draining = sideToMove;
        run &= _control.HasClock;

        if (run)
        {
            _since.Restart();
            if (!_isRunning)
                _ticker.Start();
        }
        else
        {
            _ticker.Stop();
            _since.Reset();
        }

        _isRunning = run;
    }

    // Credits the increment to the side that has just moved.
    public void AddIncrement(Color mover)
    {
        if (!_control.HasClock || _control.IncrementMs == 0)
            return;
        Add(mover, _control.IncrementMs);
        Notify();
    }

    // Remaining time of one side: what the engine is given to plan with.
    public long RemainingMs(Color color) => color == Color.White ? _whiteMs : _blackMs;

    private void Drain()
    {
        long elapsed = _since.ElapsedMilliseconds;
        if (elapsed <= 0)
            return;
        _since.Restart();

        Add(_draining, -elapsed);
        Notify();

        if (RemainingMs(_draining) <= 0)
        {
            _isRunning = false;
            _ticker.Stop();
            _since.Reset();
            Flagged?.Invoke(_draining);
        }
    }

    private void Add(Color color, long milliseconds)
    {
        if (color == Color.White)
            _whiteMs = Math.Max(0, _whiteMs + milliseconds);
        else
            _blackMs = Math.Max(0, _blackMs + milliseconds);
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(WhiteMs));
        OnPropertyChanged(nameof(BlackMs));
    }

    // Clock face. Tenths appear under twenty seconds, which is where they start
    // mattering and stop being noise.
    public static string Format(long milliseconds)
    {
        if (milliseconds <= 0)
            return "0:00";

        var span = TimeSpan.FromMilliseconds(milliseconds);
        if (milliseconds < 20_000)
            return $"{span.Seconds}.{span.Milliseconds / 100}";
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}";
        return $"{span.Minutes}:{span.Seconds:00}";
    }
}
