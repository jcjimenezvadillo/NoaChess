using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using NoaChess.Core;
using NoaChess.Engine;
using NoaChess.Engine.Search;
using NoaChess.Engine.TimeManagement;
using NoaChess.GUI.Wpf.Models;
using NoaChess.GUI.Wpf.Services;
using NoaChess.GUI.Wpf.Theme;

namespace NoaChess.GUI.Wpf.ViewModels;

// Who the engine is playing for.
public enum GameMode
{
    PlayAsWhite, // the user has white, the engine answers with black
    PlayAsBlack,
    Analysis     // the user plays both sides and the engine only comments
}

// The window's ViewModel: it owns the game, the engine and every panel, and it
// is the only place where the three meet.
//
// The rule that keeps this class honest: the position on screen is always
// GameModel's, the legality of anything is always the Core's answer, and the
// engine is only ever asked about a COPY. Everything else here is presentation.
public sealed class MainViewModel : ViewModelBase, IBoardHost, IDisposable
{
    private const int MaxAnalysisLines = 60;

    private readonly GameModel _game = new();
    private readonly EngineService _engine;
    private readonly AppSettings _settings;
    private readonly IPromotionPieceSelector _promotionSelector;
    private readonly Stopwatch _clock = new();

    // Times how long the side to move takes. Restarted whenever a move lands
    // AND whenever a game begins, so what it reads is the thinking time of the
    // move about to be played. Without the second restart the first move of a
    // game would be charged with however long the window had been sitting open
    // before anyone touched it.
    private readonly Stopwatch _moveClock = Stopwatch.StartNew();

    // The UI dispatcher, captured on construction. Search progress arrives on a
    // worker thread and is marshalled through this instead of relying on the
    // ambient SynchronizationContext: that context is not installed everywhere
    // the ViewModel can be built, and without it an engine report would mutate
    // the bound collections from the wrong thread.
    private readonly Dispatcher _dispatcher =
        Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    // Bumped on every position change. A search result carrying an old token
    // belongs to a position that is no longer on the board and is discarded:
    // cancellation is cooperative, so a stopped search still returns something.
    private int _positionToken;

    private GameMode _mode = GameMode.PlayAsWhite;
    private bool _isEngineThinking;
    private string _statusText = "";
    private string _evaluationText = "0.00";
    private string _depthText = "-";
    private string _nodesText = "-";
    private string _npsText = "-";
    private string _timeText = "-";
    private double _evalFraction = 0.5;

    // Last evaluation received, kept so the bar can be redrawn when the board
    // is flipped without waiting for the engine to report again.
    private int _lastWhiteScore;

    private TimeControl _timeControl = TimeControl.Default;
    private EngineStrength _strength = EngineStrength.Full;

    // Openings are varied for this many plies. Long enough that games diverge,
    // short enough that the engine is still playing properly by the time it
    // matters.
    private const int VariedOpeningPlies = 8;

    // How far from the best a move may be and still be picked while varying the
    // opening. A third of a pawn: enough for a real choice of opening, not
    // enough to hand anything away.
    private const int OpeningVarietyMargin = 35;

    private readonly Random _variety = new();

    // What the game is called. Recomputed on every position change, so walking
    // back through a game shows the opening as it was at that point rather than
    // the name it ended up with.
    private OpeningName _opening;

    // ---- Who is playing ----
    // One setup per colour rather than a single "which side is the engine".
    // Human against engine, engine against engine and analysis then stop being
    // three modes and become the same arrangement with different values.
    // Set when an external engine answers with a move that is not legal. That
    // colour is not asked again: a program that has lost track of the position
    // will keep answering the same nonsense.
    private Color? _refusedByEngine;

    private PlayerSetup _whitePlayer = PlayerSetup.Human;
    private PlayerSetup _blackPlayer = PlayerSetup.Builtin;

    // External engines live as long as the game they are playing. They are
    // child processes, so they are started when a game needs them and disposed
    // when it ends - leaving them running would leave orphans behind.
    private UciEngine? _whiteEngine;
    private UciEngine? _blackEngine;

    // A match between two engines plays itself. This is the brake.
    private bool _matchPaused;

    // ---- Training on your own mistakes ----
    // The positions the review found a real improvement in, and where in that
    // list we are. Training replays the game up to one of them and stops,
    // waiting for the player to find what they missed the first time.
    private List<int> _trainingPlies = [];
    private int _trainingIndex = -1;
    private bool _trainingAwaitingMove;

    // The game as it was before practice started. An attempt replaces the
    // continuation from that point - that is what trying a different move
    // means - so without a snapshot the second exercise would be set in a game
    // the first one had already destroyed.
    private List<Move> _trainingSnapshot = [];
    private string _trainingStartFen = "";

    // The side that ran out of time, if any. Losing on time is not a rule the
    // Core knows about - it is the clock's verdict, not the position's - so it
    // is tracked here.
    private Color? _flagged;

    // What a game review found, keyed by ply. It outlives the move list, which
    // is rebuilt from scratch on every position change, so the annotations are
    // reapplied there rather than stored on the cells.
    private readonly Dictionary<int, ReviewedMove> _review = [];
    private CancellationTokenSource? _reviewCancellation;
    private bool _isReviewing;
    private string _reviewText = "";
    private bool _showCandidates;
    private bool _showDecisions;
    private string _decisionsHeadline = "";

    // Steps the game forward on its own, the way every chess program lets you
    // watch a game back.
    private DispatcherTimer? _replayTimer;
    private bool _isRankingCandidates;
    private string _candidatesStatus = "";

    // Position the automatic ranking has already been started for. Without it
    // the ranking would restart the engine work, which would start the ranking,
    // which would restart the engine work.
    private int _rankedToken = -1;

    // Set when a position change arrives while a ranking is still running. The
    // ranking that is finishing belongs to a position nobody is looking at any
    // more, so a fresh one is started as soon as it lets go - otherwise fast
    // navigation would leave the board with no arrows at all.
    private bool _rankAgain;

    // Whether the ranking that is queued behind the running one is the cheap
    // automatic pass or the deep one the button asks for. Without it, pressing
    // "All moves" while the automatic pass happens to be running would answer
    // with the automatic depth, which is not what was asked for.
    private bool _rankAgainAutomatic = true;

    // Set when the user closes the panel while a ranking is running. A deep
    // pass over fifty moves takes a minute and a half, and there has to be a
    // way to say "never mind" that is not navigating away.
    private bool _cancelRanking;

    public BoardViewModel Board { get; }

    // The external engines the user has added, for the dialogs to offer.
    public EngineCatalog Catalog { get; } = new();

    public ObservableCollection<MoveRowViewModel> MoveRows { get; } = [];
    public ObservableCollection<AnalysisLineViewModel> AnalysisLines { get; } = [];
    public ObservableCollection<CandidateMoveViewModel> Candidates { get; } = [];
    public ObservableCollection<DecisionPointViewModel> Decisions { get; } = [];

    public PlayerStripViewModel TopPlayer { get; } = new();
    public PlayerStripViewModel BottomPlayer { get; } = new();

    public GameClock Clock { get; } = new();

    public MainViewModel(IPromotionPieceSelector promotionSelector)
    {
        _promotionSelector = promotionSelector;
        _settings = AppSettings.Load();
        _engine = new EngineService(_settings.HashMb, _settings.Threads);

        Board = new BoardViewModel(this)
        {
            Palette = BoardPalette.ByName(_settings.BoardTheme),
            ShowCoordinates = _settings.ShowCoordinates,
            ShowLegalMoves = _settings.ShowLegalMoves,
        };

        _engine.AnalysisMaxDepth = _settings.AnalysisMaxDepth;
        _timeControl = _settings.ReadTimeControl();
        _strength = EngineStrength.ByName(_settings.EngineStrength);
        _whitePlayer = PlayerSetup.Parse(_settings.WhitePlayer);
        _blackPlayer = PlayerSetup.Parse(_settings.BlackPlayer);
        Catalog.Load(_settings.ExternalEngines);

        Clock.Reset(_timeControl);
        Clock.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(GameClock.WhiteMs) or nameof(GameClock.BlackMs)
                or nameof(GameClock.IsVisible))
            {
                UpdateClockDisplays();
            }
        };
        Clock.Flagged += OnFlagged;

        NewGameCommand = new RelayCommand(() => NewGameRequested?.Invoke());
        FlipCommand = new RelayCommand(FlipBoard);
        FirstCommand = new RelayCommand(() => Navigate(0), () => _game.CanGoBack);
        PreviousCommand = new RelayCommand(() => Navigate(_game.Ply - 1), () => _game.CanGoBack);
        NextCommand = new RelayCommand(() => Navigate(_game.Ply + 1), () => _game.CanGoForward);
        LastCommand = new RelayCommand(() => Navigate(int.MaxValue), () => _game.CanGoForward);
        GoToPlyCommand = new RelayCommand<MoveCellViewModel>(cell => Navigate(cell.Ply));
        TakeBackCommand = new RelayCommand(TakeBack, () => _game.Ply > 0);
        MoveNowCommand = new RelayCommand(MoveNow, CanMoveNow);
        CopyFenCommand = new RelayCommand(() => CopyToClipboard(_game.CurrentFen));
        CopyPgnCommand = new RelayCommand(() => CopyToClipboard(BuildPgn()));
        PasteFenCommand = new RelayCommand(PasteFen);
        SetModeCommand = new RelayCommand<GameMode>(SetMode);
        PauseMatchCommand = new RelayCommand(ToggleMatchPause, () => IsEngineMatch);
        OpenPgnCommand = new RelayCommand(() => OpenPgnRequested?.Invoke());
        SavePgnCommand = new RelayCommand(() => SavePgnRequested?.Invoke());
        PastePgnCommand = new RelayCommand(PastePgn);
        ReviewGameCommand = new RelayCommand(ToggleReview, () => _game.Moves.Count > 0);
        DecisionsCommand = new RelayCommand(() => ShowDecisions = !ShowDecisions,
                                            () => Decisions.Count > 0);
        GoToDecisionCommand = new RelayCommand<DecisionPointViewModel>(GoToDecision);
        HintCommand = new RelayCommand(ShowHint, () => IsInputEnabled);
        TrainCommand = new RelayCommand(StartTraining, () => _review.Count > 0);
        NextTrainingCommand = new RelayCommand(NextTrainingPosition, () => _trainingIndex >= 0);
        CandidatesCommand = new RelayCommand(ToggleCandidates);
        ReplayCommand = new RelayCommand(ToggleReplay, () => _game.Moves.Count > 0);
        PlayCandidateCommand = new RelayCommand<CandidateMoveViewModel>(PlayCandidate);

        Board.IsFlipped = _mode == GameMode.PlayAsBlack;
        RefreshPanels(rebuildMoveList: true);
    }

    // Raised when the user asks for a new game. The window answers by showing
    // the dialog and calling StartGame: choosing a side and a clock is a
    // conversation with the user, and the ViewModel does not open windows.
    public event Action? NewGameRequested;

    // Same arrangement for the file dialogs: the ViewModel decides WHAT to
    // load or save, the window decides where it comes from and goes to.
    public event Action? OpenPgnRequested;
    public event Action? SavePgnRequested;

    // Sets the engine going. Called once the window is loaded rather than from
    // the constructor: the engine reports from a worker thread and needs a live
    // dispatcher to come back through.
    public void Start()
    {
        if (_settings.SyzygyPath.Length > 0 && System.IO.Directory.Exists(_settings.SyzygyPath))
            LoadTablebases(_settings.SyzygyPath);
        else
            RestartEngineWork();
    }

    // ---- Commands ----

    public ICommand NewGameCommand { get; }
    public ICommand FlipCommand { get; }
    public ICommand FirstCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand LastCommand { get; }
    public ICommand GoToPlyCommand { get; }
    public ICommand TakeBackCommand { get; }
    public ICommand MoveNowCommand { get; }
    public ICommand CopyFenCommand { get; }
    public ICommand CopyPgnCommand { get; }
    public ICommand PasteFenCommand { get; }
    public ICommand SetModeCommand { get; }
    public ICommand PauseMatchCommand { get; }
    public ICommand OpenPgnCommand { get; }
    public ICommand SavePgnCommand { get; }
    public ICommand PastePgnCommand { get; }
    public ICommand ReviewGameCommand { get; }
    public ICommand DecisionsCommand { get; }
    public ICommand GoToDecisionCommand { get; }
    public ICommand HintCommand { get; }
    public ICommand TrainCommand { get; }
    public ICommand NextTrainingCommand { get; }
    public ICommand CandidatesCommand { get; }
    public ICommand ReplayCommand { get; }
    public ICommand PlayCandidateCommand { get; }

    // ---- State shown by the window ----

    public GameMode Mode
    {
        get => _mode;
        private set
        {
            if (SetProperty(ref _mode, value))
                OnPropertyChanged(nameof(ModeText));
        }
    }

    public string ModeText
    {
        get
        {
            string side = _mode switch
            {
                GameMode.PlayAsWhite => "Playing white",
                GameMode.PlayAsBlack => "Playing black",
                _ => "Analysis",
            };
            if (_mode == GameMode.Analysis)
                return side;
            string level = _strength.IsCapped ? $"  -  {_strength.Name}" : "";
            return $"{side}  -  {_timeControl.Describe()}{level}";
        }
    }

    public TimeControl TimeControl => _timeControl;

    // "B90  Sicilian, Najdorf Variation", or empty before the game has a name.
    public string OpeningText => _opening.Display;

    public bool HasOpening => _opening.IsKnown;

    public EngineStrength Strength => _strength;

    // True while a whole-game review is running. The board stays usable; what
    // stops is the idle analysis, which would otherwise fight the review for
    // the engine on every single move.
    public bool IsReviewing
    {
        get => _isReviewing;
        private set
        {
            if (SetProperty(ref _isReviewing, value))
                OnPropertyChanged(nameof(ReviewButtonText));
        }
    }

    public string ReviewButtonText => _isReviewing ? "Stop review" : "Review game";

    public bool IsReplaying => _replayTimer is not null;

    public string ReplayButtonText => IsReplaying ? "Stop" : "Replay";

    // Milliseconds between moves while replaying.
    public int ReplaySpeedMs
    {
        get => _settings.ReplaySpeedMs;
        set
        {
            if (_settings.ReplaySpeedMs == value)
                return;
            _settings.ReplaySpeedMs = value;
            _settings.Save();
            if (_replayTimer is not null)
                _replayTimer.Interval = TimeSpan.FromMilliseconds(value);
            OnPropertyChanged();
        }
    }

    // Progress while reviewing, then the verdict. Empty until a review runs.
    public string ReviewText
    {
        get => _reviewText;
        private set
        {
            if (SetProperty(ref _reviewText, value))
                OnPropertyChanged(nameof(HasReview));
        }
    }

    public bool HasReview => _reviewText.Length > 0;

    // Which of the two views the engine panel is showing: the search as it
    // deepens, or every legal move ranked.
    public bool ShowCandidates
    {
        get => _showCandidates;
        private set
        {
            if (!SetProperty(ref _showCandidates, value))
                return;
            OnPropertyChanged(nameof(ShowIterations));
            if (value)
                ShowDecisions = false;
        }
    }

    public bool ShowIterations => !_showCandidates && !_showDecisions;

    // The third view of the engine panel: where the game was actually decided.
    public bool ShowDecisions
    {
        get => _showDecisions;
        private set
        {
            if (!SetProperty(ref _showDecisions, value))
                return;
            OnPropertyChanged(nameof(ShowIterations));
            if (value)
                ShowCandidates = false;
        }
    }

    public bool HasDecisions => Decisions.Count > 0;

    // The one line the whole feature exists to print.
    public string DecisionsHeadline
    {
        get => _decisionsHeadline;
        private set => SetProperty(ref _decisionsHeadline, value);
    }

    public string CandidatesStatus
    {
        get => _candidatesStatus;
        private set => SetProperty(ref _candidatesStatus, value);
    }

    public bool IsEngineThinking
    {
        get => _isEngineThinking;
        private set => SetProperty(ref _isEngineThinking, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string EvaluationText
    {
        get => _evaluationText;
        private set => SetProperty(ref _evaluationText, value);
    }

    public string DepthText
    {
        get => _depthText;
        private set => SetProperty(ref _depthText, value);
    }

    public string NodesText
    {
        get => _nodesText;
        private set => SetProperty(ref _nodesText, value);
    }

    public string NpsText
    {
        get => _npsText;
        private set => SetProperty(ref _npsText, value);
    }

    public string TimeText
    {
        get => _timeText;
        private set => SetProperty(ref _timeText, value);
    }

    // Share of the evaluation bar taken by the side at the BOTTOM of the board,
    // 0..1. It is expressed that way, rather than as white's share, because the
    // bar belongs to the board: flip the board and the player at the bottom
    // changes, so the bar has to change with it.
    public double EvalFraction
    {
        get => _evalFraction;
        private set => SetProperty(ref _evalFraction, value);
    }

    public System.Windows.Media.Color EvalBottomColor =>
        Board.IsFlipped ? BlackBarColor : WhiteBarColor;

    public System.Windows.Media.Color EvalTopColor =>
        Board.IsFlipped ? WhiteBarColor : BlackBarColor;

    private static readonly System.Windows.Media.Color WhiteBarColor =
        System.Windows.Media.Color.FromRgb(0xF1, 0xEF, 0xE7);

    private static readonly System.Windows.Media.Color BlackBarColor =
        System.Windows.Media.Color.FromRgb(0x3A, 0x37, 0x33);

    // Shown at the far right of the menu bar. The name and version are already
    // on the left, so this says only what is not obvious: which evaluator is
    // deciding the moves, how many threads are looking, and whether the endgame
    // tables are loaded.
    public string EngineDescription
    {
        get
        {
            // When neither side is the built-in engine, naming its evaluator
            // would be describing an engine that is not playing.
            if (_whitePlayer.Kind == PlayerKind.External
                && _blackPlayer.Kind == PlayerKind.External)
            {
                return $"{_whitePlayer.Name}  vs  {_blackPlayer.Name}";
            }

            string text = $"{_engine.EvaluatorName}  -  {_engine.Threads} thread"
                        + (_engine.Threads == 1 ? "" : "s");
            string tables = _engine.TablebaseDescription;
            return tables.Length > 0 ? $"{text}  -  {tables}" : text;
        }
    }

    // Points the engine at a folder of Syzygy tables.
    public async void LoadTablebases(string path)
    {
        await _engine.LoadTablebasesAsync(path);

        _settings.SyzygyPath = _engine.TablebasesAvailable ? path : "";
        _settings.Save();
        OnPropertyChanged(nameof(EngineDescription));

        StatusText = _engine.TablebasesAvailable
            ? $"Endgame tables loaded: {_engine.TablebaseDescription}."
            : "No Syzygy tables were found in that folder.";

        RestartEngineWork();
    }

    public int ReviewDepth
    {
        get => _settings.ReviewDepth;
        set
        {
            if (_settings.ReviewDepth == value)
                return;
            _settings.ReviewDepth = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    // Both of these stop the engine before changing it and start it again
    // afterwards, so the menu can be used in the middle of a search.
    public int Threads
    {
        get => _engine.Threads;
        set => _ = ApplyThreads(value);
    }

    private async Task ApplyThreads(int threads)
    {
        await _engine.SetThreadsAsync(threads);
        _settings.Threads = _engine.Threads;
        _settings.Save();
        OnPropertyChanged(nameof(Threads));
        OnPropertyChanged(nameof(EngineDescription));
        RestartEngineWork();
    }

    public int HashMb
    {
        get => _engine.HashMb;
        set => _ = ApplyHash(value);
    }

    private async Task ApplyHash(int megabytes)
    {
        await _engine.SetHashAsync(megabytes);
        _settings.HashMb = _engine.HashMb;
        _settings.Save();
        OnPropertyChanged(nameof(HashMb));
        RestartEngineWork();
    }

    public bool ShowCoordinates
    {
        get => Board.ShowCoordinates;
        set
        {
            Board.ShowCoordinates = value;
            _settings.ShowCoordinates = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool ShowLegalMoves
    {
        get => Board.ShowLegalMoves;
        set
        {
            Board.ShowLegalMoves = value;
            _settings.ShowLegalMoves = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool VaryOpening
    {
        get => _settings.VaryOpening;
        set
        {
            if (_settings.VaryOpening == value)
                return;
            _settings.VaryOpening = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool FindDecisionPoints
    {
        get => _settings.FindDecisionPoints;
        set
        {
            if (_settings.FindDecisionPoints == value)
                return;
            _settings.FindDecisionPoints = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool AnalyseWhileIdle
    {
        get => _settings.AnalyseWhileIdle;
        set
        {
            _settings.AnalyseWhileIdle = value;
            _settings.Save();
            OnPropertyChanged();
            RestartEngineWork();
        }
    }

    // ---- IBoardHost ----

    // Qualified because this class also exposes a property called Board: the
    // 8x8 surface, which is a very different thing from the position.
    public Core.Board Position => _game.Board;

    public PlayerSetup WhitePlayer => _whitePlayer;
    public PlayerSetup BlackPlayer => _blackPlayer;

    public PlayerSetup PlayerOf(Color color) => color == Color.White ? _whitePlayer : _blackPlayer;

    public bool IsHuman(Color color) => !PlayerOf(color).IsEngine;

    // The colour the user is playing, or null when they play both sides or
    // neither. It is what the board consults to decide whose pieces can be
    // picked up, so a match between two engines correctly answers "nobody's".
    public Color? HumanColor
    {
        get
        {
            bool white = IsHuman(Color.White);
            bool black = IsHuman(Color.Black);
            if (white && !black) return Color.White;
            if (black && !white) return Color.Black;
            return null;
        }
    }

    // True when neither side is a person: the game plays itself.
    public bool IsEngineMatch => !IsHuman(Color.White) && !IsHuman(Color.Black);

    public bool IsMatchPaused
    {
        get => _matchPaused;
        private set
        {
            if (SetProperty(ref _matchPaused, value))
                OnPropertyChanged(nameof(PauseButtonText));
        }
    }

    public string PauseButtonText => _matchPaused ? "Resume" : "Pause";

    public bool IsInputEnabled =>
        !_isEngineThinking
        && _flagged is null
        && _game.IsAtLivePosition
        && _game.Result == GameResult.Ongoing
        && IsHuman(_game.Board.SideToMove);

    public bool IsPremoveEnabled =>
        _isEngineThinking && _game.IsAtLivePosition && HumanColor is not null;

    public void PlayUserMove(Move move)
    {
        // In a training position the move is an ATTEMPT: it replaces the game
        // from here, which is exactly what trying a different move means, and
        // it gets judged against what was played the first time.
        bool attempt = _trainingAwaitingMove;
        int ply = _game.Ply + 1;

        CommitMove(move);
        PositionChanged(rebuildMoveList: true);

        if (attempt)
            JudgeTrainingMove(ply);
    }

    // The only place a move is added to the game. Going through it is what
    // guarantees the clock is charged and the increment paid every single time.
    private void CommitMove(Move move)
    {
        Color mover = _game.Board.SideToMove;
        _game.Play(move, _moveClock.Elapsed.TotalSeconds);
        _moveClock.Restart();
        Clock.AddIncrement(mover);
    }

    // What the engine is allowed to spend on this move: the time control, and
    // then the strength cap on top of it. Whichever runs out first stops the
    // search, so a capped engine answers instantly instead of sitting on a
    // clock it is not allowed to use.
    private SearchLimits LimitsForEngineMove()
    {
        SearchLimits limits = TimeLimits();
        return _strength.IsCapped ? limits with { MaxNodes = _strength.MaxNodes } : limits;
    }

    private SearchLimits TimeLimits() => _timeControl.Kind switch
    {
        TimeControlKind.Depth => SearchLimits.Depth(_timeControl.Depth),

        // A real clock hands the whole remaining time to the engine's own time
        // manager, which is the same scheduler it uses under UCI. MoveOverhead
        // is small here: there is no process boundary and no GUI protocol
        // between the search and the board, only a method call.
        TimeControlKind.Clock => TimeManager.FromClock(
            Clock.RemainingMs(_game.Board.SideToMove), _timeControl.IncrementMs,
            moveOverheadMs: 20, movesToGo: null, gamePly: _game.Ply),

        _ => SearchLimits.Time(_timeControl.MoveTimeMs),
    };

    public PieceType AskPromotion(Color side) => _promotionSelector.SelectPromotionPiece(side);

    // ---- Game lifecycle ----

    // The quick mode switches in the menu are shorthands for a pair of players.
    private void SetMode(GameMode mode)
    {
        (_whitePlayer, _blackPlayer) = mode switch
        {
            GameMode.PlayAsWhite => (PlayerSetup.Human, PlayerSetup.Builtin),
            GameMode.PlayAsBlack => (PlayerSetup.Builtin, PlayerSetup.Human),
            _ => (PlayerSetup.Human, PlayerSetup.Human),
        };

        _settings.WhitePlayer = _whitePlayer.Serialise();
        _settings.BlackPlayer = _blackPlayer.Serialise();
        _settings.Save();

        OnPropertyChanged(nameof(WhitePlayer));
        OnPropertyChanged(nameof(BlackPlayer));
        OnPropertyChanged(nameof(IsEngineMatch));
        OnPropertyChanged(nameof(EngineDescription));
        NewGame(mode);
    }

    private void NewGame(GameMode mode)
    {
        Mode = mode;
        StartNewGame();
    }

    // Starts a game with a side and a time control chosen together, which is
    // what the New Game dialog hands over.
    public void StartGame(TimeControl control, EngineStrength strength,
                          PlayerSetup white, PlayerSetup black)
    {
        _timeControl = control;
        _strength = strength;
        _whitePlayer = white;
        _blackPlayer = black;

        _settings.WriteTimeControl(control);
        _settings.EngineStrength = strength.Name;
        _settings.WhitePlayer = white.Serialise();
        _settings.BlackPlayer = black.Serialise();
        _settings.ExternalEngines = Catalog.Save();
        _settings.Save();

        OnPropertyChanged(nameof(TimeControl));
        OnPropertyChanged(nameof(Strength));
        OnPropertyChanged(nameof(WhitePlayer));
        OnPropertyChanged(nameof(BlackPlayer));
        OnPropertyChanged(nameof(IsEngineMatch));
        OnPropertyChanged(nameof(EngineDescription));

        // The mode is now just a label for what the two players add up to.
        GameMode derived = !white.IsEngine && !black.IsEngine ? GameMode.Analysis
                         : !white.IsEngine ? GameMode.PlayAsWhite
                         : GameMode.PlayAsBlack;
        NewGame(derived);
    }

    // Saves the engine catalogue after the engines dialog has changed it.
    public void SaveCatalog()
    {
        _settings.ExternalEngines = Catalog.Save();
        _settings.Save();
    }

    // Stops and restarts a game that is playing itself.
    public void ToggleMatchPause()
    {
        IsMatchPaused = !IsMatchPaused;
        if (!IsMatchPaused)
        {
            RestartEngineWork();
            return;
        }

        // Every engine that could be thinking, not just the built-in one: in a
        // match between two external programs it is never the one working.
        _engine.RequestStop();
        _whiteEngine?.RequestStop();
        _blackEngine?.RequestStop();
    }

    private async void StartNewGame()
    {
        StopReplay();
        // NewGameAsync both stops the engine and resets it while holding the
        // gate, so no search can start between those two steps.
        ShutDownExternalEngines();
        IsMatchPaused = false;
        _refusedByEngine = null;

        await _engine.NewGameAsync();
        _game.Reset();
        _game.SetDefaultTags(NameOf(Color.White), NameOf(Color.Black));
        _moveClock.Restart();
        _flagged = null;
        Clock.Reset(_timeControl);
        OnPropertyChanged(nameof(ModeText));

        // Playing black means looking at the board from black's side.
        Board.IsFlipped = _mode == GameMode.PlayAsBlack;
        Board.ClearPremove();

        // The bar carries over between moves - the evaluation rarely jumps -
        // but a new game is a new game.
        SetEvaluation(0);
        OnPropertyChanged(nameof(EvalBottomColor));
        OnPropertyChanged(nameof(EvalTopColor));

        PositionChanged(rebuildMoveList: true);
    }

    private void FlipBoard()
    {
        Board.IsFlipped = !Board.IsFlipped;
        UpdatePlayerStrips();

        // Both ends of the board changed hands: the strips above and below it,
        // and the evaluation bar beside it.
        SetEvaluation(_lastWhiteScore);
        OnPropertyChanged(nameof(EvalBottomColor));
        OnPropertyChanged(nameof(EvalTopColor));
    }

    // Applies a board colour scheme by name, as the Board menu names them.
    public void SetPaletteByName(string name) => SetPalette(BoardPalette.ByName(name));

    // Name of the active board colour scheme, for the menu check marks.
    public string BoardThemeName => Board.Palette.Name;

    // Undoes the last full move: the engine's reply and the user's move, so the
    // user gets their own decision back rather than a position where it is the
    // engine's turn again.
    private void TakeBack()
    {
        // Against an engine a full move is two plies: undoing one would only
        // hand the position straight back to it. With no engine on the board
        // one ply is one decision.
        int target = _game.Ply;
        target -= HumanColor is null ? 1 : 2;
        _game.GoTo(Math.Max(0, target));
        _game.TruncateHere();
        Board.ClearPremove();
        PositionChanged(rebuildMoveList: true);
    }

    private void Navigate(int ply)
    {
        _game.GoTo(ply);
        Board.ClearPremove();
        PositionChanged(rebuildMoveList: false);
    }

    private void SetPalette(BoardPalette palette)
    {
        Board.Palette = palette;
        _settings.BoardTheme = palette.Name;
        _settings.Save();
        OnPropertyChanged(nameof(BoardThemeName));
    }

    // ---- The one place a position change is handled ----

    private void PositionChanged(bool rebuildMoveList)
    {
        // Marshalled explicitly. Most callers are already on the UI thread and
        // this runs inline for them, but some arrive as the continuation of an
        // await, and a continuation resumes wherever the runtime decides. The
        // bound collections rebuilt below cannot be touched from anywhere else.
        _dispatcher.Invoke(() =>
        {
            RefreshPanels(rebuildMoveList);
            RestartEngineWork();
        });
    }

    // Repaints everything the new position implies, without touching the
    // engine. Split out so the constructor can build a fully painted window
    // and leave STARTING the engine to Start(), once there is a dispatcher
    // running to marshal its reports back to.
    private void RefreshPanels(bool rebuildMoveList)
    {
        // Bumping the token first is what makes every in-flight search stale.
        // The thinking flag is cleared here rather than by the search that is
        // being abandoned: that search resumes LATER, and letting it clear a
        // flag a newer search had already set would report the engine as idle
        // while it is working.
        _positionToken++;
        IsEngineThinking = false;

        Board.Refresh(_game.Board, _game.LastMove?.Move);

        UpdateOpening();

        if (rebuildMoveList)
            RebuildMoveList();
        UpdateCurrentMoveHighlight();
        UpdatePlayerStrips();
        UpdateStatus();

        AnalysisLines.Clear();
        Candidates.Clear();
        CandidatesStatus = "";
        DepthText = NodesText = NpsText = TimeText = "-";

        bool live = _game.IsAtLivePosition
                 && _game.Result == GameResult.Ongoing
                 && _flagged is null
                 && _game.Ply > 0; // the clock starts with the first move
        Clock.SetRunning(live, _game.Board.SideToMove);
        UpdateClockDisplays();
    }

    // The opening is named for the position on screen, not for the whole game:
    // stepping back to move 3 of a Najdorf should say what it was called then.
    private void UpdateOpening()
    {
        OpeningName found = OpeningBook.Shipped.Identify(_game.StartFen, _game.Moves, _game.Ply);
        if (found == _opening)
            return;

        _opening = found;
        OnPropertyChanged(nameof(OpeningText));
        OnPropertyChanged(nameof(HasOpening));
    }

    // The opening as it stands at the END of the game, which is the one a saved
    // file should carry: a PGN tagged with the name the game had on move three
    // would be wrong about the game.
    private OpeningName FinalOpening()
        => OpeningBook.Shipped.Identify(_game.StartFen, _game.Moves, _game.Moves.Count);

    private void UpdateClockDisplays()
    {
        TopPlayer.SetClock(Clock.RemainingMs(TopPlayer.Color), Clock.IsVisible);
        BottomPlayer.SetClock(Clock.RemainingMs(BottomPlayer.Color), Clock.IsVisible);
    }

    // A flag falls. The game is over by the clock, which is not something the
    // position can tell us, so everything that asks "is the game still on" has
    // to consult _flagged as well.
    private void OnFlagged(Color loser)
    {
        _flagged = loser;
        Clock.SetRunning(false, _game.Board.SideToMove);
        _positionToken++; // whatever the engine is doing no longer matters
        UpdateStatus();
        _ = _engine.StopAsync();
    }

    private async void RestartEngineWork()
    {
        int token = _positionToken;

        // A review is walking the whole game through the engine one position at
        // a time. Letting the idle analysis restart between those searches would
        // have the two of them taking the engine from each other on every move.
        if (_isReviewing)
            return;

        if (_game.Result != GameResult.Ongoing || _flagged is not null)
        {
            await _engine.StopAsync();
            return;
        }

        bool engineToMove = !IsHuman(_game.Board.SideToMove)
                         && _game.IsAtLivePosition
                         && _refusedByEngine != _game.Board.SideToMove
                         && !(IsEngineMatch && _matchPaused);
        if (engineToMove)
        {
            await RunEngineMove(token);
            return;
        }

        // A ranking already has the engine. It calls back here when it lets go,
        // so the idle analysis picks up then rather than fighting it for the
        // gate on every one of its thirty searches. It is told that the answer
        // is wanted again, or a burst of moves would leave the last position
        // with no ranking at all.
        if (_isRankingCandidates)
        {
            _rankAgain = true;
            return;
        }

        // While analysing, the alternatives are ranked automatically so the
        // board can show them as arrows. Shallower than the pass the "All
        // moves" button runs: this one happens on every position and has to be
        // over before it becomes an annoyance.
        // The arrows are asked for LATER, deliberately. Ranking every legal move
        // is thirty to forty searches, and running it here meant the evaluation,
        // the depth and the variation did not appear until it had finished: the
        // panel sat empty for seconds after every move while the arrows were
        // being computed, and the user was left waiting on work they had not
        // asked for. The analysis starts now; the arrows follow it.
        if (_mode == GameMode.Analysis && _settings.AnalyseWhileIdle && _rankedToken != token)
            ScheduleCandidatePass();

        // Nothing is analysed before the game has begun. In a game the first
        // move is the player's to choose and there is nothing to be helped
        // with yet, so sitting on a core commenting the start position is pure
        // waste. Analysis mode is the exception, because there the engine's
        // opinion IS what the window is for.
        bool beforeFirstMove = _game.Ply == 0 && _mode != GameMode.Analysis;

        // A paused match is not analysed either: the point of pausing is to
        // stop the machine working.
        if (IsEngineMatch && _matchPaused)
        {
            await _engine.StopAsync();
            return;
        }

        if (_settings.AnalyseWhileIdle && !beforeFirstMove)
            await RunAnalysis(token);
        else
            await _engine.StopAsync();
    }

    // The space bar. Two jobs that are really one job, "play a move now":
    //
    //  - The engine is already thinking about its own move: cut the search
    //    short and take what it has, which is what "move now" means everywhere.
    //  - Nobody is on the clock: search this position and play the move for
    //    WHOEVER is to move, whichever colour that is. That is what makes a
    //    set-up endgame walkable one move at a time - press it again and again
    //    and the engine plays both sides of it.
    private bool CanMoveNow()
        => _isEngineThinking
        || (_flagged is null
            && _game.Result == GameResult.Ongoing
            && _game.IsAtLivePosition
            && !_isReviewing
            && !_isRankingCandidates);

    private async void MoveNow()
    {
        // A search that is running already has the answer coming; asking it to
        // finish is enough, and the move is committed where it always was.
        if (_isEngineThinking)
        {
            _engine.RequestStop();
            _whiteEngine?.RequestStop();
            _blackEngine?.RequestStop();
            return;
        }

        if (!CanMoveNow())
            return;

        int token = _positionToken;
        IsEngineThinking = true;
        UpdateStatus();

        SearchResult result = await _engine.PlayMoveAsync(
            _game.Board, LimitsForEngineMove(), MakeProgress(token));

        if (token != _positionToken)
            return; // The position moved on under the search.

        IsEngineThinking = false;
        if (result.BestMove == Move.None)
        {
            UpdateStatus();
            return;
        }

        CommitMove(result.BestMove);
        PositionChanged(rebuildMoveList: true);
    }

    private async Task RunEngineMove(int token)
    {
        IsEngineThinking = true;
        UpdateStatus();

        // An external engine owns this colour: ask it instead, and get out of
        // the way of the built-in one entirely.
        if (PlayerOf(_game.Board.SideToMove).Kind == PlayerKind.External)
        {
            await RunExternalMove(token);
            return;
        }

        // Early in the game the engine picks among the moves it considers
        // nearly equal instead of always the same one. Without this, every
        // game against it is the same game: an engine is deterministic, and a
        // deterministic opponent stops being interesting on the third night.
        if (_settings.VaryOpening && _game.Ply < VariedOpeningPlies
            && PlayerOf(_game.Board.SideToMove).Kind == PlayerKind.Builtin)
        {
            Move varied = await ChooseVariedOpeningMove(token);
            if (token != _positionToken)
                return;
            if (varied != Move.None)
            {
                IsEngineThinking = false;
                CommitMove(varied);
                PositionChanged(rebuildMoveList: true);
                return;
            }
        }

        SearchResult result = await _engine.PlayMoveAsync(
            _game.Board, LimitsForEngineMove(), MakeProgress(token));

        if (token != _positionToken)
            return; // The position moved on; a newer search owns the flag.

        IsEngineThinking = false;
        if (result.BestMove == Move.None)
        {
            UpdateStatus();
            return;
        }

        CommitMove(result.BestMove);

        // A premove entered while the engine was thinking is played straight
        // away if the position it arrived in allows it.
        Move premove = Board.ConsumePremove(_game.Board);
        if (premove != Move.None)
            CommitMove(premove);

        PositionChanged(rebuildMoveList: true);
    }

    // How long the position has to stand still before its alternatives are
    // ranked. Short enough not to be noticed, long enough that stepping through
    // a game never pays for a pass it is about to throw away.
    private const int CandidateDelayMs = 600;
    private DispatcherTimer? _candidateDelay;

    private void ScheduleCandidatePass()
    {
        if (_candidateDelay is null)
        {
            _candidateDelay = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(CandidateDelayMs),
            };

            _candidateDelay.Tick += (_, _) =>
            {
                _candidateDelay!.Stop();

                // Everything is re-checked here rather than captured when the
                // wait began: six hundred milliseconds is long enough for the
                // mode, the position and the setting to have all changed.
                if (_mode != GameMode.Analysis || !_settings.AnalyseWhileIdle
                    || _isReviewing || _isRankingCandidates
                    || _rankedToken == _positionToken)
                {
                    return;
                }

                _rankedToken = _positionToken;
                RankCandidates(automatic: true);
            };
        }

        // Restarting the wait is the point: a burst of moves ranks the position
        // it ends on, not each one it passed through.
        _candidateDelay.Stop();
        _candidateDelay.Start();
    }

    private async Task RunAnalysis(int token)
    {
        UpdateStatus();
        await _engine.AnalyseAsync(_game.Board, MakeProgress(token));
    }

    // Builds the progress sink for one search. Every report is put on the UI
    // dispatcher: from the UI thread that runs inline, and from the search
    // thread it marshals, which is what makes the panels safe to touch here.
    private IProgress<SearchProgress> MakeProgress(int token)
    {
        Core.Board snapshot = _game.Board.Clone();

        return new Progress<SearchProgress>(p => _dispatcher.Invoke(() => Report(p, token, snapshot)));
    }

    // One completed search iteration, applied to the panels.
    private void Report(SearchProgress p, int token, Core.Board snapshot)
    {
        if (token != _positionToken)
            return; // A report from a position that is no longer on screen.

        double seconds = _clock.Elapsed.TotalSeconds;
        var line = new AnalysisLineViewModel(p, snapshot, seconds);

        AnalysisLines.Insert(0, line);
        while (AnalysisLines.Count > MaxAnalysisLines)
            AnalysisLines.RemoveAt(AnalysisLines.Count - 1);

        DepthText = $"{p.Depth}";
        NodesText = line.NodesText;
        NpsText = line.NpsText;
        TimeText = line.TimeText;
        SetEvaluation(line.WhiteScore);
    }

    private void SetEvaluation(int whiteScore)
    {
        _lastWhiteScore = whiteScore;
        EvaluationText = Formatting.Score(whiteScore);

        // Centipawns compress into a bar through the same logistic curve online
        // boards use, so the bar tracks winning chances rather than material.
        double white = Formatting.IsMate(whiteScore)
            ? (whiteScore > 0 ? 1.0 : 0.0)
            : 1.0 / (1.0 + Math.Exp(-0.00368208 * whiteScore));

        double bottom = Board.IsFlipped ? 1.0 - white : white;

        // Never let it hit the edge: a sliver of the losing colour keeps the
        // bar readable as a bar.
        EvalFraction = Math.Clamp(bottom, 0.015, 0.985);
    }

    // ---- Panels ----

    private void RebuildMoveList()
    {
        // The rows are completed in a local list and only then published: a row
        // added to the bound collection while its black cell is still missing
        // would render half empty, because the row itself raises no
        // notifications.
        var rows = new List<MoveRowViewModel>();
        MoveRowViewModel? row = null;

        for (int i = 0; i < _game.Moves.Count; i++)
        {
            PlayedMove played = _game.Moves[i];
            var cell = new MoveCellViewModel(played, i + 1);

            if (played.Side == Color.White || row is null)
            {
                // A game that starts on a black move opens with an empty white
                // cell so the columns still line up.
                row = played.Side == Color.White
                    ? new MoveRowViewModel(played.MoveNumber, cell, null)
                    : new MoveRowViewModel(played.MoveNumber, null, cell);
                rows.Add(row);
            }
            else
            {
                row.Black = cell;
            }
        }

        MoveRows.Clear();
        foreach (MoveRowViewModel built in rows)
            MoveRows.Add(built);

        ApplyReviewAnnotations();
    }

    private void ApplyReviewAnnotations()
    {
        if (_review.Count == 0)
            return;

        foreach (MoveRowViewModel row in MoveRows)
        {
            Annotate(row.White);
            Annotate(row.Black);
        }

        void Annotate(MoveCellViewModel? cell)
        {
            if (cell is not null && _review.TryGetValue(cell.Ply, out ReviewedMove found))
                cell.SetQuality(found.Quality);
        }
    }

    // ---- Replay ----

    private void ToggleReplay()
    {
        if (_replayTimer is not null)
        {
            StopReplay();
            return;
        }

        // Starting a replay from the end would have nothing to show, so it
        // rewinds first: pressing play means "watch it again".
        if (!_game.CanGoForward)
            Navigate(0);

        _replayTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(_settings.ReplaySpeedMs),
        };
        _replayTimer.Tick += (_, _) =>
        {
            if (!_game.CanGoForward)
            {
                StopReplay();
                return;
            }
            Navigate(_game.Ply + 1);
        };
        _replayTimer.Start();

        OnPropertyChanged(nameof(IsReplaying));
        OnPropertyChanged(nameof(ReplayButtonText));
    }

    private void StopReplay()
    {
        _replayTimer?.Stop();
        _replayTimer = null;
        OnPropertyChanged(nameof(IsReplaying));
        OnPropertyChanged(nameof(ReplayButtonText));
    }

    // ---- Window geometry ----

    // The window remembers where it was. Off-screen positions are dropped: a
    // monitor that is no longer attached would otherwise open the window
    // somewhere the user cannot reach it.
    public (double Width, double Height, double Left, double Top, bool Maximised) SavedGeometry
        => (_settings.WindowWidth, _settings.WindowHeight,
            _settings.WindowLeft, _settings.WindowTop, _settings.WindowMaximised);

    public void SaveGeometry(double width, double height, double left, double top, bool maximised)
    {
        _settings.WindowWidth = width;
        _settings.WindowHeight = height;
        _settings.WindowLeft = left;
        _settings.WindowTop = top;
        _settings.WindowMaximised = maximised;
        _settings.Save();
    }

    // ---- External engines ----

    // Asks the UCI engine that owns the side to move.
    //
    // Its answer arrives as UCI text, which is resolved against the LEGAL moves
    // of the position rather than trusted: an engine that returns a move that
    // is not legal here has either lost sync or crashed, and playing it would
    // corrupt the game rather than report the fault.
    private async Task RunExternalMove(int token)
    {
        Color side = _game.Board.SideToMove;
        UciEngine? engine = await EngineFor(side);

        if (engine is null)
        {
            IsEngineThinking = false;
            StatusText = $"{PlayerOf(side).Name} could not be started. "
                       + "Choose another engine from Game, New game.";
            return;
        }

        Core.Board snapshot = _game.Board.Clone();
        _clock.Restart();

        var progress = new Progress<UciInfo>(info =>
        {
            if (token != _positionToken)
                return;
            ShowExternalInfo(info, snapshot);
        });

        List<string> moves = _game.Moves.Select(m => m.Move.ToString()).ToList();
        UciBestMove answer = await engine.SearchAsync(_game.StartFen, moves,
                                                      ExternalLimits(side), progress);

        if (token != _positionToken)
            return;

        IsEngineThinking = false;

        if (answer.Move.Length == 0)
        {
            StatusText = $"{engine.Name} returned no move.";
            return;
        }

        Move played = MoveGenerator.GenerateLegalMoves(_game.Board)
            .FirstOrDefault(m => m.ToString() == answer.Move, Move.None);

        if (played == Move.None)
        {
            // Refusing the move is not enough: the scheduler would come back
            // and ask the same engine the same question for ever. The colour is
            // marked as no longer answerable, which is what "stopped" means.
            _refusedByEngine = side;
            StatusText = $"{engine.Name} answered '{answer.Move}', which is not legal here. "
                       + "The game is stopped rather than corrupted.";
            UpdateStatus();
            return;
        }

        CommitMove(played);

        Move premove = Board.ConsumePremove(_game.Board);
        if (premove != Move.None)
            CommitMove(premove);

        PositionChanged(rebuildMoveList: true);
    }

    // What the external engine is told about the clock. It gets the same
    // budget the built-in one would, expressed the way UCI expects.
    private UciLimits ExternalLimits(Color side)
    {
        if (_strength.IsCapped)
            return UciLimits.ToNodes(_strength.MaxNodes);

        return _timeControl.Kind switch
        {
            TimeControlKind.Depth => UciLimits.ToDepth(_timeControl.Depth),
            TimeControlKind.Clock => UciLimits.Clock(
                Clock.RemainingMs(Color.White), Clock.RemainingMs(Color.Black),
                _timeControl.IncrementMs, _timeControl.IncrementMs),
            _ => UciLimits.Time(_timeControl.MoveTimeMs),
        };
    }

    // The engine panel shows whatever engine is actually thinking, so a match
    // between two external engines reads as one conversation rather than going
    // blank whenever it is not NoaChess's turn.
    private void ShowExternalInfo(UciInfo info, Core.Board position)
    {
        int white = Formatting.ToWhiteScore(
            info.IsMate
                ? (info.MateIn > 0
                    ? AlphaBetaSearch.MateScore - info.MateIn * 2
                    : -AlphaBetaSearch.MateScore - info.MateIn * 2)
                : info.ScoreCp,
            position.SideToMove);

        DepthText = info.Depth.ToString();
        NodesText = Formatting.Nodes(info.Nodes);
        NpsText = info.Nps > 0
            ? Formatting.Nps(info.Nodes, info.Nodes / (double)Math.Max(1, info.Nps))
            : "-";
        TimeText = Formatting.Time(info.TimeMs / 1000.0);
        SetEvaluation(white);
    }

    // Starts the engine for a colour on first use and keeps it for the game.
    private async Task<UciEngine?> EngineFor(Color side)
    {
        UciEngine? existing = side == Color.White ? _whiteEngine : _blackEngine;
        if (existing is not null)
            return existing;

        PlayerSetup setup = PlayerOf(side);
        if (setup.Kind != PlayerKind.External)
            return null;

        StatusText = $"Starting {setup.Name}...";
        (UciEngine? engine, string error) = await UciEngine.StartAsync(setup.Path);

        if (engine is null)
        {
            StatusText = $"{setup.Name}: {error}";
            return null;
        }

        // The same hash and thread count the built-in engine is given, for the
        // engines that have those options. A match where one side was handed
        // eight threads and the other took its own default would measure the
        // settings rather than the engines.
        if (engine.Supports("Threads"))
            engine.SetOption("Threads", _settings.Threads.ToString());
        if (engine.Supports("Hash"))
            engine.SetOption("Hash", _settings.HashMb.ToString());

        await engine.NewGameAsync();

        if (side == Color.White)
            _whiteEngine = engine;
        else
            _blackEngine = engine;

        OnPropertyChanged(nameof(EngineDescription));
        return engine;
    }

    // External engines are child processes and belong to the game being played.
    private void ShutDownExternalEngines()
    {
        _whiteEngine?.Dispose();
        _blackEngine?.Dispose();
        _whiteEngine = null;
        _blackEngine = null;
    }

    // Picks among the openings the engine rates within a third of a pawn of
    // its best. Returns Move.None when there is nothing to choose from, in
    // which case the caller searches normally.
    private async Task<Move> ChooseVariedOpeningMove(int token)
    {
        List<Move> legal = MoveGenerator.GenerateLegalMoves(_game.Board);
        if (legal.Count < 2)
            return Move.None;

        Core.Board board = _game.Board.Clone();
        var scored = new List<(Move Move, int Score)>(legal.Count);

        // Shallow on purpose. This is about which openings are playable, a
        // question that does not need depth, and it happens before the clock
        // has anything at stake.
        foreach (Move move in legal)
        {
            if (token != _positionToken)
                return Move.None;

            board.MakeMove(move);
            SearchResult result = await _engine.SearchAsync(board, SearchLimits.Depth(6), null);
            board.UnmakeMove();
            scored.Add((move, -result.Score));
        }

        if (token != _positionToken)
            return Move.None;

        int best = scored.Max(x => x.Score);
        List<Move> playable = scored
            .Where(x => best - x.Score <= OpeningVarietyMargin)
            .Select(x => x.Move)
            .ToList();

        return playable.Count > 1 ? playable[_variety.Next(playable.Count)] : Move.None;
    }

    // ---- Training on your own mistakes ----

    // The whole point of a review is to play those positions again. Every
    // program shows you where you went wrong; this puts you back in the chair.
    //
    // Only mistakes and blunders are used. An inaccuracy is not worth setting a
    // position up for, and there would be a dozen of them in every game.
    public bool IsTraining => _trainingIndex >= 0;

    public string TrainingText
    {
        get => _trainingText;
        private set
        {
            if (SetProperty(ref _trainingText, value))
                OnPropertyChanged(nameof(HasTraining));
        }
    }

    public bool HasTraining => _trainingText.Length > 0;

    private string _trainingText = "";

    private void StartTraining()
    {
        _trainingPlies = _review.Values
            .Where(r => r.Quality is MoveQuality.Mistake or MoveQuality.Blunder)
            .OrderBy(r => r.Ply)
            .Select(r => r.Ply)
            .ToList();

        if (_trainingPlies.Count == 0)
        {
            TrainingText = "Nothing to practise: the review found no mistake worth replaying.";
            return;
        }

        _trainingSnapshot = _game.Moves.Select(m => m.Move).ToList();
        _trainingStartFen = _game.StartFen;
        _trainingIndex = -1;
        NextTrainingPosition();
    }

    private void NextTrainingPosition()
    {
        _trainingIndex++;

        if (_trainingIndex >= _trainingPlies.Count)
        {
            StopTraining($"That was all {_trainingPlies.Count} of them.");
            return;
        }

        // The game is put back before every exercise, because the previous
        // attempt replaced everything after the position it was played in.
        _game.RestoreLine(_trainingStartFen, _trainingSnapshot);

        // The position BEFORE the mistake: the chair the player was sitting in.
        int ply = _trainingPlies[_trainingIndex];
        _game.GoTo(ply - 1);
        _trainingAwaitingMove = true;

        PositionChanged(rebuildMoveList: false);

        string side = _game.Board.SideToMove == Color.White ? "White" : "Black";
        TrainingText = $"Position {_trainingIndex + 1} of {_trainingPlies.Count}. "
                     + $"{side} to move, and there is something better here than what was played. "
                     + "Play a move.";
    }

    private void StopTraining(string closing)
    {
        // The game goes back to the one that was reviewed, not to whatever
        // the last attempt left behind.
        if (_trainingSnapshot.Count > 0)
        {
            _game.RestoreLine(_trainingStartFen, _trainingSnapshot);
            _game.GoToEnd();
            PositionChanged(rebuildMoveList: true);
        }

        _trainingIndex = -1;
        _trainingAwaitingMove = false;
        _trainingPlies = [];
        _trainingSnapshot = [];
        TrainingText = closing;
        OnPropertyChanged(nameof(IsTraining));
    }

    // Judges the move the player has just tried in a training position, by the
    // same measure the review used: how much it gives up against the best.
    private async void JudgeTrainingMove(int ply)
    {
        _trainingAwaitingMove = false;

        if (!_review.TryGetValue(ply, out ReviewedMove original))
            return;

        // The position after the attempt, scored at the depth the review used
        // so the two numbers are comparable.
        SearchResult after = await _engine.SearchAsync(
            _game.Board, SearchLimits.Depth(_settings.ReviewDepth), null);

        int scoreAfter = Formatting.ToWhiteScore(after.Score, _game.Board.SideToMove);
        Color mover = Core.Board.OppositeColor(_game.Board.SideToMove);
        int loss = mover == Color.White
            ? original.ScoreBefore - scoreAfter
            : scoreAfter - original.ScoreBefore;
        loss = Math.Max(0, loss);

        string played = _game.LastMove?.San ?? "";

        // Compared against the ORIGINAL mistake, not against perfection. The
        // question a player is really asking is "would I do better this time",
        // and a move that gives up less than the one played in the game is a
        // better move whether or not it is the engine's first choice.
        if (loss <= 20)
        {
            TrainingText = $"{played} - that is the move. "
                         + (original.BestSan.Length > 0 && original.BestSan != played
                            ? $"The engine prefers {original.BestSan}, but this gives up nothing."
                            : "");
        }
        else if (loss < original.CentipawnLoss)
        {
            TrainingText = $"{played} is better than the game move: it gives up "
                         + $"{loss / 100.0:0.00} instead of {original.CentipawnLoss / 100.0:0.00}. "
                         + $"Best was {original.BestSan}.";
        }
        else
        {
            TrainingText = $"{played} gives up {loss / 100.0:0.00}. "
                         + $"The move was {original.BestSan}.";
        }
    }

    // ---- Hint ----

    // Draws the move the engine would play, once, as a single arrow.
    //
    // This is the ONLY arrow that appears during a game, and it appears because
    // the player asked for it. The automatic candidate arrows stay out of a
    // game on purpose: an opponent that is permanently telling you what to play
    // is not an opponent.
    private async void ShowHint()
    {
        if (!IsInputEnabled)
            return;

        int token = _positionToken;
        StatusText = "Thinking about a hint...";

        // A hint is worth a proper look but not a long wait: a second and a
        // half is deeper than most players see and shorter than most will sit
        // through.
        SearchResult result = await _engine.SearchAsync(
            _game.Board, SearchLimits.Time(1500), null);

        if (token != _positionToken || result.BestMove == Move.None)
            return;

        Board.SetCandidateArrows([(result.BestMove.From, result.BestMove.To, 0)]);
        StatusText = $"Hint: {San.Format(_game.Board, result.BestMove)}. "
                   + "It disappears as soon as you move.";

        RestartEngineWork();
    }

    // ---- Where the game was decided ----

    // Depth of the decision scan. Shallow on purpose: the question is how far
    // apart the alternatives were, and that shape is visible long before the
    // exact numbers settle. Every position of the game gets a full scan of its
    // legal moves, so the cost is the move count times the game length.
    private const int DecisionScanDepth = 6;

    // A choice is only a decision when it was worth something. One pawn between
    // the best move and the second best is the line: below it the position was
    // playing itself whatever anyone chose.
    private const int DecisionSpread = 100;

    private void PublishDecisions(List<DecisionPoint> found)
    {
        Decisions.Clear();

        List<DecisionPoint> real = found
            .Where(p => p.Spread >= DecisionSpread)
            .OrderByDescending(p => p.Spread)
            .Take(15)
            .ToList();

        if (real.Count == 0)
        {
            DecisionsHeadline = "No position in this game was worth more than a pawn to get right: "
                              + "the moves that mattered were all forced or close to equal.";
            OnPropertyChanged(nameof(HasDecisions));
            return;
        }

        int widest = real[0].Spread;
        foreach (DecisionPoint point in real)
        {
            Decisions.Add(new DecisionPointViewModel(point)
            {
                BarFraction = Math.Clamp(point.Spread / (double)widest, 0.05, 1.0),
            });
        }

        int taken = real.Count(p => p.TookIt);

        // Time is only mentioned when the game was actually played here. A game
        // read from a PGN carries no clock, and a made-up percentage would be
        // worse than silence.
        double decisionTime = real.Sum(p => p.Seconds);
        double totalTime = found.Sum(p => p.Seconds);

        // Ten seconds over the whole game is the floor for saying anything
        // about time. Below it nobody was really thinking - a game replayed
        // from a file, or one clicked through - and a percentage of that is a
        // number pretending to be a fact.
        string timing = totalTime > 10
            ? $"  You spent {decisionTime / totalTime * 100:0}% of your thinking time on them."
            : "";

        DecisionsHeadline =
            $"{real.Count} position{(real.Count == 1 ? "" : "s")} decided this game. "
          + $"You found the best move in {taken} of them.{timing}";

        ShowDecisions = true;
        OnPropertyChanged(nameof(HasDecisions));
    }

    private void GoToDecision(DecisionPointViewModel decision) => Navigate(decision.Ply);

    // ---- Candidate moves ----

    private void ToggleCandidates()
    {
        ShowCandidates = !ShowCandidates;

        if (ShowCandidates)
        {
            _cancelRanking = false;
            RankCandidates(automatic: false);
            return;
        }

        // Closing the panel abandons the pass that was filling it.
        _cancelRanking = true;
    }

    // Scores EVERY legal move of the position on screen, so the panel can show
    // what the alternatives were worth rather than only the line the engine
    // settled on.
    //
    // Each move is searched in its own subtree at the same depth, which is what
    // makes the numbers comparable with each other. A search of the root would
    // prune most of these away long before it could price them: that is its
    // job, and it is the wrong tool for this question.
    private async void RankCandidates(bool automatic)
    {
        if (_isReviewing)
            return;

        // Already ranking: remember that the answer is wanted again rather than
        // dropping the request, or a burst of navigation ends with arrows for
        // no position at all. A deep request never loses to a shallow one that
        // happened to be queued first.
        if (_isRankingCandidates)
        {
            _rankAgain = true;
            _rankAgainAutomatic &= automatic;
            return;
        }

        List<Move> legal = MoveGenerator.GenerateLegalMoves(_game.Board);
        if (legal.Count == 0)
        {
            Candidates.Clear();
            CandidatesStatus = "There are no legal moves in this position.";
            return;
        }

        _isRankingCandidates = true;
        Candidates.Clear();
        CandidatesStatus = $"Scoring {legal.Count} moves...";

        int token = _positionToken;
        await _engine.StopAsync();

        // One ply is spent making the move itself, so the subtree gets one less
        // than the depth being claimed.
        //
        // The automatic pass is capped hard. It runs on EVERY position and the
        // idle analysis is queued behind it, so a slow pass does not just delay
        // the arrows: it delays the evaluation, the depth and the variation as
        // well, and the panel sits empty while it works. Shallow and immediate
        // beats deep and late here; the "All moves" button is where depth
        // belongs.
        int requested = automatic ? Math.Min(_settings.ReviewDepth, 6) : _settings.ReviewDepth;
        int depth = Math.Max(1, requested - 1);
        var scored = new List<CandidateMoveViewModel>(legal.Count);
        Core.Board board = _game.Board.Clone();
        bool stale = false;

        try
        {
            foreach (Move move in legal)
            {
                // The board moved on under us. BREAK rather than return: a
                // return from inside this try skips the code below that starts
                // a ranking for the position that is actually on screen, and
                // the panel is then left empty with nothing on its way.
                if (token != _positionToken || _cancelRanking)
                {
                    stale = token != _positionToken;
                    break;
                }

                string san = San.Format(board, move);
                Color mover = board.SideToMove;

                board.MakeMove(move);
                SearchResult result = await _engine.SearchAsync(board, SearchLimits.Depth(depth), null);
                board.UnmakeMove();

                // The child's score is the OPPONENT's; negate it to get the
                // mover's, then put it on the white-relative scale everything
                // else on screen uses.
                int moverScore = -result.Score;
                scored.Add(new CandidateMoveViewModel(move, san,
                                                      Formatting.ToWhiteScore(moverScore, mover)));

                // Published as they come in rather than all at the end. A deep
                // pass over fifty moves takes a minute and a half, and a panel
                // that stays empty for a minute and a half looks broken; this
                // way the good moves surface early and settle.
                PublishCandidates(scored, mover, depth + 1, final: false);
                CandidatesStatus = $"Scoring {scored.Count} of {legal.Count} moves"
                                 + $" at depth {depth + 1}...";
            }
        }
        finally
        {
            _isRankingCandidates = false;
        }

        if (stale || token != _positionToken || _rankAgain)
        {
            // Either the board moved on under us or another request arrived
            // while this one was running. Start again for what is on screen
            // now; the work just done was for a position nobody is looking at.
            _rankAgain = false;
            bool next = _rankAgainAutomatic && automatic;
            _rankAgainAutomatic = true;

            if (next)
            {
                // Automatic: queue it behind the wait so the position now on
                // screen gets its evaluation before its arrows.
                _rankedToken = -1;
                ScheduleCandidatePass();
                RestartEngineWork();
            }
            else
            {
                // Asked for by name from the button: no waiting.
                RankCandidates(next);
            }
            return;
        }

        PublishCandidates(scored, _game.Board.SideToMove, depth + 1, final: true);
        RestartEngineWork();
    }

    private void PublishCandidates(List<CandidateMoveViewModel> scored, Color sideToMove,
                                   int depth, bool final)
    {
        // Best first FOR THE SIDE TO MOVE: white wants the largest
        // white-relative score, black the smallest.
        List<CandidateMoveViewModel> ordered = sideToMove == Color.White
            ? scored.OrderByDescending(c => c.WhiteScore).ToList()
            : scored.OrderBy(c => c.WhiteScore).ToList();

        int sign = sideToMove == Color.White ? 1 : -1;
        int best = ordered.Count > 0 ? ordered[0].WhiteScore * sign : 0;

        Candidates.Clear();
        for (int i = 0; i < ordered.Count; i++)
        {
            CandidateMoveViewModel candidate = ordered[i];
            candidate.Rank = i + 1;
            candidate.IsBest = i == 0;

            int behind = best - candidate.WhiteScore * sign;
            candidate.BehindText = i == 0 ? "" : $"-{behind / 100.0:0.00}";

            // The bar shrinks with the loss and bottoms out at three pawns,
            // past which every move is simply bad and the exact figure adds
            // nothing to the picture.
            candidate.BarFraction = Math.Clamp(1.0 - behind / 300.0, 0.04, 1.0);

            Candidates.Add(candidate);
        }

        if (!final)
            return; // the status line is the caller's while the pass is running

        CandidatesStatus = $"{ordered.Count} legal moves, each searched to depth {depth}.";

        // The arrows are drawn once, at the end. Redrawing them on every move
        // scored would have them twitching around the board for a minute.
        DrawCandidateArrows(ordered, sign, best);
    }

    // Draws the best few moves on the board, green for the best and sliding
    // through yellow to orange as they get worse.
    //
    // ONLY while analysing. In a game these arrows would be telling the player
    // what to play, which is a different program from the one this is.
    private void DrawCandidateArrows(List<CandidateMoveViewModel> ordered, int sign, int best)
    {
        if (_mode != GameMode.Analysis)
        {
            Board.ClearCandidateArrows();
            return;
        }

        // Five is what fits before the board turns into a pile of arrows.
        var arrows = new List<(int, int, int)>();
        foreach (CandidateMoveViewModel candidate in ordered.Take(5))
        {
            int loss = best - candidate.WhiteScore * sign;
            arrows.Add((candidate.Move.From, candidate.Move.To, loss));
        }
        Board.SetCandidateArrows(arrows);
    }

    private void PlayCandidate(CandidateMoveViewModel candidate)
    {
        if (IsInputEnabled)
            PlayUserMove(candidate.Move);
    }

    // ---- Whole-game review ----

    private void ToggleReview()
    {
        if (_isReviewing)
        {
            _reviewCancellation?.Cancel();
            return;
        }
        RunReview();
    }

    private async void RunReview()
    {
        if (_game.Moves.Count == 0)
            return;

        _review.Clear();
        IsReviewing = true;
        ReviewText = "Reviewing...";

        // Take the engine off whatever it was doing first: the review owns it
        // for the duration.
        await _engine.StopAsync();

        var cancellation = new CancellationTokenSource();
        _reviewCancellation = cancellation;

        int total = _game.Moves.Count;
        var progress = new Progress<int>(done =>
            ReviewText = $"Reviewing move {done} of {total}...");

        try
        {
            var review = new GameReview(_engine);
            (List<ReviewedMove> moves, ReviewSummary white, ReviewSummary black) =
                await review.RunAsync(_game.StartFen, _game.Moves, _settings.ReviewDepth,
                                      progress, cancellation.Token);

            foreach (ReviewedMove move in moves)
                _review[move.Ply] = move;

            string verdict = cancellation.IsCancellationRequested
                ? $"Review stopped after {moves.Count} of {total} moves."
                : $"White {white.Accuracy:0}% accurate ({white.Blunders} blunders, "
                  + $"{white.Mistakes} mistakes, {white.Inaccuracies} inaccuracies)   -   "
                  + $"Black {black.Accuracy:0}% ({black.Blunders} blunders, "
                  + $"{black.Mistakes} mistakes, {black.Inaccuracies} inaccuracies)";
            ReviewText = verdict;

            // Second pass: not "which moves were wrong" but "where did the
            // choice matter". A shallow scan of every legal move of every
            // position, which is a different and much wider question.
            if (!cancellation.IsCancellationRequested && _settings.FindDecisionPoints)
            {
                var decisionProgress = new Progress<int>(done =>
                    ReviewText = $"Looking for the decisions: move {done} of {total}...");

                List<DecisionPoint> found = await review.FindDecisionPointsAsync(
                    _game.StartFen, _game.Moves, DecisionScanDepth,
                    decisionProgress, cancellation.Token);

                PublishDecisions(found);

                // The strip goes back to saying what the review found. It was
                // borrowed for the decision pass's progress and leaving
                // "move 12 of 12..." there for ever would read as unfinished.
                ReviewText = verdict;
            }
        }
        catch (Exception ex)
        {
            ReviewText = $"The review stopped: {ex.Message}";
        }
        finally
        {
            cancellation.Dispose();
            _reviewCancellation = null;
            IsReviewing = false;
            ApplyReviewAnnotations();
            RestartEngineWork();
        }
    }

    private void UpdateCurrentMoveHighlight()
    {
        foreach (MoveRowViewModel row in MoveRows)
        {
            if (row.White is not null)
                row.White.IsCurrent = row.White.Ply == _game.Ply;
            if (row.Black is not null)
                row.Black.IsCurrent = row.Black.Ply == _game.Ply;
        }
        OnPropertyChanged(nameof(CurrentPly));
    }

    // Ply the board is showing, used by the view to scroll the move list.
    public int CurrentPly => _game.Ply;

    // The position on screen, for the board editor to start from.
    public string CurrentFen => _game.CurrentFen;

    private void UpdatePlayerStrips()
    {
        Color bottom = Board.IsFlipped ? Color.Black : Color.White;
        Color top = Core.Board.OppositeColor(bottom);

        BottomPlayer.Update(_game.Board, bottom, NameOf(bottom), RoleOf(bottom));
        TopPlayer.Update(_game.Board, top, NameOf(top), RoleOf(top));
    }

    // Who is playing that colour. The game's own tag wins when it has one,
    // which is what makes a loaded game show the players it was played by
    // rather than "You" against the engine.
    private string NameOf(Color color)
    {
        string tag = color == Color.White ? "White" : "Black";
        if (_game.Tags.TryGetValue(tag, out string? name)
            && name.Length > 0 && name != "?")
        {
            return name;
        }

        PlayerSetup setup = PlayerOf(color);
        return setup.Kind switch
        {
            PlayerKind.Builtin => $"NoaChess {ChessEngine.Version}",
            PlayerKind.External => setup.Name,
            _ => "You",
        };
    }

    private string RoleOf(Color color)
    {
        string side = color == Color.White ? "White" : "Black";
        return PlayerOf(color).Kind switch
        {
            PlayerKind.Builtin => $"{side}  -  engine",
            PlayerKind.External => $"{side}  -  UCI engine",
            _ => _mode == GameMode.Analysis ? $"{side}  -  analysis" : $"{side}  -  human",
        };
    }

    private void UpdateStatus()
    {
        if (!_game.IsAtLivePosition)
        {
            StatusText = $"Reviewing move {_game.Ply} of {_game.Moves.Count}."
                       + "  Press End to return to the game.";
            return;
        }

        if (_flagged is { } loser)
        {
            StatusText = loser == Color.White
                ? "White is out of time. Black wins."
                : "Black is out of time. White wins.";
            return;
        }

        GameResult result = _game.Result;
        if (result != GameResult.Ongoing)
        {
            StatusText = result switch
            {
                GameResult.Checkmate => _game.Board.SideToMove == Color.White
                    ? "Checkmate. Black wins."
                    : "Checkmate. White wins.",
                GameResult.Stalemate => "Draw by stalemate.",
                GameResult.InsufficientMaterial => "Draw by insufficient material.",
                GameResult.FiftyMoveRule => "Draw by the fifty-move rule.",
                _ => "Draw by threefold repetition.",
            };
            return;
        }

        if (_isEngineThinking)
        {
            StatusText = "NoaChess is thinking. You can already enter your reply as a premove.";
            return;
        }

        string turn = _game.Board.SideToMove == Color.White ? "White to move." : "Black to move.";
        StatusText = _game.Board.IsInCheck() ? $"Check. {turn}" : turn;
    }

    // ---- Clipboard ----

    private static void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // The clipboard belongs to the whole desktop and another process
            // can be holding it. Losing a copy is not worth a dialog.
        }
    }

    private string BuildPgn()
    {
        string result = _flagged switch
        {
            Color.White => "0-1",
            Color.Black => "1-0",
            _ => _game.Result switch
            {
                GameResult.Checkmate => _game.Board.SideToMove == Color.White ? "0-1" : "1-0",
                GameResult.Ongoing => "*",
                _ => "1/2-1/2",
            },
        };
        OpeningName named = FinalOpening();
        return _game.ToPgn(result, named.Eco, named.Name);
    }

    // ---- PGN ----

    // The game as PGN, for the clipboard and for a file.
    public string CurrentPgn => BuildPgn();

    // What to call the file, before the user renames it.
    public string SuggestedPgnName =>
        $"{_game.Tags.GetValueOrDefault("White", "White")} vs "
        + $"{_game.Tags.GetValueOrDefault("Black", "Black")} {DateTime.Now:yyyy-MM-dd}";

    // The game's PGN tag pairs, for the details dialog to edit in place.
    public Dictionary<string, string> GameTags => _game.Tags;

    // Repaints the names after the details dialog has changed them.
    public void RefreshPlayers()
    {
        UpdatePlayerStrips();
        OnPropertyChanged(nameof(SuggestedPgnName));
    }

    // Confirms a save in the status bar rather than in a dialog: it worked,
    // and a modal to say so is a click the user did not ask for.
    public void ReportSaved(string path)
        => StatusText = $"Game saved to {System.IO.Path.GetFileName(path)}.";

    // Replaces the game with one read from PGN text.
    //
    // Loading a game switches to ANALYSIS mode on purpose. A loaded game is
    // being reviewed, not continued, and leaving a play mode on would have the
    // engine answer the last move of somebody else's game the moment it
    // appeared on the board.
    public void LoadPgnText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (!Pgn.TryParseFirst(text, out PgnGame parsed) || parsed.Moves.Count == 0)
        {
            MessageBox.Show("No game could be read from that text.",
                            "Open a game", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        LoadGame(parsed);
    }

    // Loads one already-parsed game. Split from the text entry point so the
    // window can show a picker between reading a collection and opening one of
    // its games.
    public async void LoadGame(PgnGame parsed)
    {
        await _engine.NewGameAsync();

        int loaded = _game.LoadPgn(parsed, out string problem);

        _moveClock.Restart();
        Mode = GameMode.Analysis;
        _flagged = null;
        Clock.Reset(_timeControl);
        Board.ClearPremove();
        Board.IsFlipped = false;
        SetEvaluation(0);
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(EvalBottomColor));
        OnPropertyChanged(nameof(EvalTopColor));

        PositionChanged(rebuildMoveList: true);

        if (problem.Length > 0)
        {
            MessageBox.Show($"{problem}\n\n{loaded} move(s) were loaded.",
                            "Open a game", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PastePgn()
    {
        try
        {
            LoadPgnText(Clipboard.GetText());
        }
        catch
        {
            // The clipboard belongs to the whole desktop and another process
            // can be holding it.
        }
    }

    private async void PasteFen()
    {
        string text;
        try
        {
            text = Clipboard.GetText();
        }
        catch
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            // Loading through the Core is also the validation: a FEN it refuses
            // throws here and the game on screen is left untouched.
            _ = new Board(text.Trim());
        }
        catch (Exception ex)
        {
            MessageBox.Show($"That is not a position NoaChess can read.\n\n{ex.Message}",
                            "Paste FEN", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetUpPosition(text.Trim());
    }

    // Restarts the game from a position the user has built or pasted. The
    // clock starts again with it: a new position is a new game, and carrying
    // the previous one's remaining time into it would be nonsense.
    public async void SetUpPosition(string fen)
    {
        await _engine.NewGameAsync();
        _game.Reset(fen);
        _game.SetDefaultTags(NameOf(Color.White), NameOf(Color.Black));
        _moveClock.Restart();
        _flagged = null;
        Clock.Reset(_timeControl);
        Board.ClearPremove();
        SetEvaluation(0);
        PositionChanged(rebuildMoveList: true);
    }

    // True the first time this build is launched. The release notes introduce
    // a new version once and then stay out of the way: a modal in front of the
    // board on every single launch is a toll, not a feature.
    public bool ConsumeFirstRunOfThisVersion()
    {
        if (_settings.LastSeenVersion == ChessEngine.Version)
            return false;

        _settings.LastSeenVersion = ChessEngine.Version;
        _settings.Save();
        return true;
    }

    // Swaps the evaluator for the network in 'path'. The running search is
    // stopped first: switching the evaluator replaces objects the search is
    // reading, which is the same concurrent-use hazard the search gate exists
    // to prevent.
    //
    // A failure is worth a dialog. The user asked for a specific network, and
    // carrying on quietly with the classical evaluator would misrepresent how
    // the engine is playing.
    public async void LoadNnue(string path)
    {
        (bool ok, string error) = await _engine.LoadNnueAsync(path);

        if (ok)
        {
            OnPropertyChanged(nameof(EngineDescription));
            RestartEngineWork();
            return;
        }

        MessageBox.Show($"That network could not be loaded.\n\n{error}",
                        "Load NNUE network", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public void Dispose()
    {
        _settings.Save();
        ShutDownExternalEngines();
        _engine.Dispose();
    }
}
