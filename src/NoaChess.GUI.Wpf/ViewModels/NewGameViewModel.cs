using System.Windows.Input;
using NoaChess.GUI.Wpf.Models;

namespace NoaChess.GUI.Wpf.ViewModels;

// One offer in the time-control list: a label, and the control it stands for.
public sealed class TimePreset(string name, string detail, TimeControl control) : ViewModelBase
{
    private bool _isSelected;

    public string Name { get; } = name;
    public string Detail { get; } = detail;
    public TimeControl Control { get; } = control;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

// One option in a colour's player list.
public sealed class PlayerOption(PlayerSetup setup) : ViewModelBase
{
    private bool _isSelected;

    public PlayerSetup Setup { get; } = setup;
    public string Name { get; } = setup.Name;

    public string Detail { get; } = setup.Kind switch
    {
        PlayerKind.Human => "you play this side",
        PlayerKind.Builtin => "the built-in engine",
        _ => "UCI engine",
    };

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

// The New Game dialog: who plays each side, how hard the engine tries and how
// the clock works.
//
// Presets rather than a form. The named ones (bullet, blitz, rapid) are what
// almost everyone wants, and the custom fields are there for when they are not.
public sealed class NewGameViewModel : ViewModelBase
{
    private GameMode _mode;
    private TimeControl _control;
    private EngineStrength _strength;
    private int _customMinutes = 5;
    private int _customIncrement = 3;

    public IReadOnlyList<TimePreset> MovePresets { get; } =
    [
        new("1 second", "per move", TimeControl.PerMove(1000)),
        new("3 seconds", "per move", TimeControl.PerMove(3000)),
        new("10 seconds", "per move", TimeControl.PerMove(10_000)),
        new("30 seconds", "per move", TimeControl.PerMove(30_000)),
    ];

    public IReadOnlyList<TimePreset> ClockPresets { get; } =
    [
        new("1+0", "bullet", TimeControl.Game(60_000, 0)),
        new("3+2", "blitz", TimeControl.Game(180_000, 2000)),
        new("5+0", "blitz", TimeControl.Game(300_000, 0)),
        new("10+5", "rapid", TimeControl.Game(600_000, 5000)),
        new("15+10", "rapid", TimeControl.Game(900_000, 10_000)),
        new("30+0", "classical", TimeControl.Game(1_800_000, 0)),
    ];

    public IReadOnlyList<TimePreset> DepthPresets { get; } =
    [
        new("Depth 6", "very fast", TimeControl.FixedDepth(6)),
        new("Depth 10", "club level", TimeControl.FixedDepth(10)),
        new("Depth 14", "strong", TimeControl.FixedDepth(14)),
        new("Depth 20", "slow and strong", TimeControl.FixedDepth(20)),
    ];

    // One strength offered as a card, with a flag for the highlight.
    public sealed class StrengthOption(EngineStrength strength) : ViewModelBase
    {
        private bool _isSelected;

        public EngineStrength Strength { get; } = strength;
        public string Name => strength.Name;
        public string Detail => strength.Detail;

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }

    public IReadOnlyList<StrengthOption> Strengths { get; } =
        EngineStrength.All.Select(s => new StrengthOption(s)).ToList();

    public IReadOnlyList<PlayerOption> WhiteOptions { get; }
    public IReadOnlyList<PlayerOption> BlackOptions { get; }

    private PlayerSetup _white;
    private PlayerSetup _black;

    public PlayerSetup White
    {
        get => _white;
        set
        {
            if (!SetProperty(ref _white, value))
                return;
            RefreshPlayers();
            OnPropertyChanged(nameof(Summary));
        }
    }

    public PlayerSetup Black
    {
        get => _black;
        set
        {
            if (!SetProperty(ref _black, value))
                return;
            RefreshPlayers();
            OnPropertyChanged(nameof(Summary));
        }
    }

    private void RefreshPlayers()
    {
        foreach (PlayerOption option in WhiteOptions)
            option.IsSelected = option.Setup == _white;
        foreach (PlayerOption option in BlackOptions)
            option.IsSelected = option.Setup == _black;
    }

    public NewGameViewModel(GameMode mode, TimeControl current, EngineStrength strength,
                            PlayerSetup white, PlayerSetup black,
                            IReadOnlyList<PlayerSetup> externals)
    {
        _mode = mode;
        _control = current;
        _strength = strength;
        _white = white;
        _black = black;

        // Every colour can be a person, the built-in engine, or any engine that
        // has been added. Two lists rather than one shared one, because the
        // highlight belongs to a colour.
        List<PlayerSetup> choices = [PlayerSetup.Human, PlayerSetup.Builtin, .. externals];
        WhiteOptions = choices.Select(c => new PlayerOption(c)).ToList();
        BlackOptions = choices.Select(c => new PlayerOption(c)).ToList();

        if (current.Kind == TimeControlKind.Clock)
        {
            _customMinutes = Math.Max(1, current.BaseMs / 60_000);
            _customIncrement = current.IncrementMs / 1000;
        }

        ChooseWhiteCommand = new RelayCommand<PlayerOption>(o => White = o.Setup);
        ChooseBlackCommand = new RelayCommand<PlayerOption>(o => Black = o.Setup);
        ChooseModeCommand = new RelayCommand<GameMode>(m => Mode = m);
        ChooseStrengthCommand = new RelayCommand<StrengthOption>(o => Strength = o.Strength);
        ChoosePresetCommand = new RelayCommand<TimePreset>(p => Control = p.Control);
        UseCustomClockCommand = new RelayCommand(ApplyCustomClock);
        RefreshSelection();
        RefreshStrength();
        RefreshPlayers();
    }

    public ICommand ChooseWhiteCommand { get; }
    public ICommand ChooseBlackCommand { get; }
    public ICommand ChooseModeCommand { get; }
    public ICommand ChooseStrengthCommand { get; }
    public ICommand ChoosePresetCommand { get; }
    public ICommand UseCustomClockCommand { get; }

    public GameMode Mode
    {
        get => _mode;
        set
        {
            if (SetProperty(ref _mode, value))
            {
                OnPropertyChanged(nameof(IsWhite));
                OnPropertyChanged(nameof(IsBlack));
                OnPropertyChanged(nameof(IsAnalysis));
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public bool IsWhite => _mode == GameMode.PlayAsWhite;
    public bool IsBlack => _mode == GameMode.PlayAsBlack;
    public bool IsAnalysis => _mode == GameMode.Analysis;

    public TimeControl Control
    {
        get => _control;
        set
        {
            if (!SetProperty(ref _control, value))
                return;
            RefreshSelection();
            OnPropertyChanged(nameof(Summary));
        }
    }

    // Lights up whichever preset the chosen control corresponds to. The
    // comparison is the record's value equality, so a custom clock that happens
    // to equal a preset highlights it, which is the honest answer.
    private void RefreshSelection()
    {
        foreach (TimePreset preset in MovePresets.Concat(ClockPresets).Concat(DepthPresets))
            preset.IsSelected = preset.Control == _control;
    }

    public EngineStrength Strength
    {
        get => _strength;
        set
        {
            if (!SetProperty(ref _strength, value))
                return;
            RefreshStrength();
            OnPropertyChanged(nameof(Summary));
        }
    }

    private void RefreshStrength()
    {
        foreach (StrengthOption option in Strengths)
            option.IsSelected = option.Strength == _strength;
    }

    public int CustomMinutes
    {
        get => _customMinutes;
        set => SetProperty(ref _customMinutes, Math.Clamp(value, 1, 180));
    }

    public int CustomIncrement
    {
        get => _customIncrement;
        set => SetProperty(ref _customIncrement, Math.Clamp(value, 0, 60));
    }

    private void ApplyCustomClock() =>
        Control = TimeControl.Game(_customMinutes * 60_000, _customIncrement * 1000);

    // The single line at the bottom of the dialog saying what is about to start.
    public string Summary
    {
        get
        {
            bool whiteHuman = !_white.IsEngine;
            bool blackHuman = !_black.IsEngine;

            if (whiteHuman && blackHuman)
                return "You play both sides. The engine only comments.";

            string level = _strength.IsCapped
                ? $", {_strength.Name.ToLowerInvariant()} level"
                : "";

            if (!whiteHuman && !blackHuman)
                return $"{_white.Name} plays {_black.Name}, {_control.Describe()}{level}. "
                     + "The game plays itself; you can pause it at any point.";

            string opponent = whiteHuman ? _black.Name : _white.Name;
            string mine = whiteHuman ? "white" : "black";
            return $"You play {mine} against {opponent}, {_control.Describe()}{level}.";
        }
    }
}
