using System.Collections.ObjectModel;
using NoaChess.Core;
using NoaChess.Engine;
using NoaChess.Engine.Search;
using NoaChess.GUI.Wpf.Services;

namespace NoaChess.GUI.Wpf.ViewModels;

// Main board ViewModel. It orchestrates the full v0.1.3 cycle:
//
//   user click -> validate against the Core's legal moves
//   -> Board.MakeMove -> refresh view -> launch the engine search on a
//   background thread -> apply bestmove on THE SAME Board -> refresh view.
//
// Important design rules:
// - The only game state is the NoaChess.Core Board. This ViewModel only keeps
//   VISUAL state (selection, highlights).
// - No chess rule is implemented here: legality is always answered by the
//   Core's MoveGenerator.
// - The search runs in Task.Run so the UI stays responsive, and it can be
//   cancelled (e.g. when starting a new game).
public sealed class BoardViewModel : ViewModelBase
{
    // Depth of the background analysis that runs while the HUMAN is thinking.
    // It is deeper than the engine's move depth because it has all the user's
    // thinking time available and it is cancelled the moment the user moves.
    private const int AnalysisDepth = 8;

    private readonly Board _board = new();
    private readonly ChessEngine _engine = new();
    private readonly IPromotionPieceSelector _promotionSelector;

    // Square selected as origin and the legal moves of that piece, cached to
    // highlight destinations and validate the second click.
    private SquareViewModel? _selectedSquare;
    private List<Move> _legalMovesFromSelection = [];

    private CancellationTokenSource? _searchCancellation;
    private string _statusText = "White to move. Click a piece.";
    private string _evaluationText = "Eval: -";
    private string _depthText = "Depth: -";
    private bool _isEngineThinking;
    private bool _isBoardFlipped;

    // The 64 squares, in the visual order the view paints them
    // (top-left to bottom-right). Reordered when the board is flipped.
    public ObservableCollection<SquareViewModel> DisplaySquares { get; } = [];

    // Access by Core square index (0..63), independent of the orientation.
    private readonly SquareViewModel[] _squares = new SquareViewModel[64];

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    // Evaluation shown in the status bar, updated live after every completed
    // search depth. Positive = white advantage, in pawns ("+0.35").
    public string EvaluationText
    {
        get => _evaluationText;
        private set => SetProperty(ref _evaluationText, value);
    }

    // Search depth (plies) currently analyzed, e.g. "Depth: 3/4 ply".
    public string DepthText
    {
        get => _depthText;
        private set => SetProperty(ref _depthText, value);
    }

    // True while the engine is searching; the view blocks user input.
    public bool IsEngineThinking
    {
        get => _isEngineThinking;
        private set => SetProperty(ref _isEngineThinking, value);
    }

    public BoardViewModel(IPromotionPieceSelector promotionSelector)
    {
        _promotionSelector = promotionSelector;
        for (int sq = 0; sq < 64; sq++)
            _squares[sq] = new SquareViewModel(sq, this);
        RebuildDisplayOrder();
        RefreshFromBoard();
        StartBackgroundAnalysis(); // Evaluate the starting position right away.
    }

    // Starts a new game (human = white, engine = black).
    public void NewGame()
    {
        _searchCancellation?.Cancel(); // Abort any ongoing search.
        Fen.Load(_board, Board.StartFen);
        ClearSelection();
        ClearLastMoveHighlight();
        IsEngineThinking = false;
        StatusText = "New game. White to move.";
        EvaluationText = "Eval: -";
        DepthText = "Depth: -";
        RefreshFromBoard();
        StartBackgroundAnalysis();
    }

    // Flips the board orientation (white at the bottom / black at the bottom).
    public void FlipBoard()
    {
        _isBoardFlipped = !_isBoardFlipped;
        RebuildDisplayOrder();
    }

    // Click-click move logic. First click: select an own piece.
    // Second click: if it is a legal destination the move is played; if it is
    // another own piece the selection changes; otherwise it deselects.
    public void OnSquareClicked(SquareViewModel square)
    {
        // While the engine is thinking (or the game is over) input is ignored.
        if (IsEngineThinking || GameState.GetResult(_board) != GameResult.Ongoing)
            return;

        int sq = square.Square;

        if (_selectedSquare == null)
        {
            TrySelect(square);
            return;
        }

        // Is the click a legal destination of the selected piece? There may be
        // several moves to the same destination (the 4 promotions): resolved later.
        var candidateMoves = _legalMovesFromSelection.Where(m => m.To == sq).ToList();

        if (candidateMoves.Count > 0)
        {
            Move move = ResolvePromotion(candidateMoves);
            ClearSelection();
            PlayHumanMoveAndRespond(move);
        }
        else
        {
            // Not a legal destination: reinterpret the click as a new selection.
            ClearSelection();
            TrySelect(square);
        }
    }

    private void TrySelect(SquareViewModel square)
    {
        int sq = square.Square;
        // Only pieces of the side to move can be selected.
        if (_board.IsEmpty(sq) || _board.ColorAt(sq) != _board.SideToMove)
            return;

        _selectedSquare = square;
        square.IsSelected = true;

        // The Core answers which moves are legal; the GUI only paints them.
        _legalMovesFromSelection = MoveGenerator.GenerateLegalMoves(_board)
            .Where(m => m.From == sq)
            .ToList();
        foreach (Move m in _legalMovesFromSelection)
            _squares[m.To].IsLegalTarget = true;
    }

    private Move ResolvePromotion(List<Move> candidates)
    {
        // Normal move: there is only one candidate.
        if (!candidates[0].IsPromotion)
            return candidates[0];

        // Promotion: there are 4 moves to the same destination; ask the user.
        PieceType chosen = _promotionSelector.SelectPromotionPiece(_board.SideToMove);
        return candidates.First(m => m.PromotionPiece == chosen);
    }

    private async void PlayHumanMoveAndRespond(Move move)
    {
        // Stop the background analysis: the position it was studying is gone.
        _searchCancellation?.Cancel();

        ApplyMove(move);

        if (CheckGameOver())
            return;

        // --- Engine's turn ---
        IsEngineThinking = true;
        StatusText = "NoaChess is thinking...";
        _searchCancellation = new CancellationTokenSource();
        CancellationToken token = _searchCancellation.Token;

        // The search score is relative to the side to move at the root (the
        // engine's side); capture it now to convert to white-relative display.
        Color engineSide = _board.SideToMove;
        int targetDepth = _engine.DefaultDepth;

        // Progress<T> captures the UI SynchronizationContext here, so the
        // callback runs on the UI thread even though the search reports from
        // a background thread.
        var progress = new Progress<SearchProgress>(p =>
        {
            if (token.IsCancellationRequested)
                return; // Stale report from an aborted search.
            EvaluationText = $"Eval: {FormatScore(p.Score, engineSide)}";
            DepthText = $"Depth: {p.Depth}/{targetDepth} ply";
        });

        try
        {
            // The search is CPU intensive: it runs on the thread pool and the
            // await returns control to the UI thread, which keeps responding.
            var result = await Task.Run(() => _engine.FindBestMove(_board, cancellation: token, progress: progress), token);

            // If "New game" was pressed meanwhile, the result no longer applies.
            if (token.IsCancellationRequested || result.BestMove == Move.None)
                return;

            ApplyMove(result.BestMove);
            if (!CheckGameOver())
            {
                StatusText = "Your turn. White to move.";
                // Keep analyzing (deeper) while the human thinks.
                StartBackgroundAnalysis();
            }
        }
        catch (OperationCanceledException)
        {
            // Search deliberately cancelled: nothing to do.
        }
        finally
        {
            IsEngineThinking = false;
        }
    }

    // Applies a move to the Board (single source of truth) and refreshes the view.
    private void ApplyMove(Move move)
    {
        ClearLastMoveHighlight();
        _board.MakeMove(move);
        _squares[move.From].IsLastMove = true;
        _squares[move.To].IsLastMove = true;
        RefreshFromBoard();
    }

    // Checks mate/draws and updates the status. Returns true if the game ended.
    private bool CheckGameOver()
    {
        GameResult result = GameState.GetResult(_board);
        StatusText = result switch
        {
            GameResult.Checkmate => _board.SideToMove == Color.White
                ? "Checkmate! Black wins."
                : "Checkmate! White wins.",
            GameResult.Stalemate => "Draw by stalemate.",
            GameResult.FiftyMoveRule => "Draw by the fifty-move rule.",
            _ when _board.IsInCheck() => _board.SideToMove == Color.White
                ? "Check! White to move."
                : "Check! Black to move.",
            _ => _board.SideToMove == Color.White ? "White to move." : "Black to move."
        };
        return result != GameResult.Ongoing;
    }

    private void ClearSelection()
    {
        if (_selectedSquare != null)
            _selectedSquare.IsSelected = false;
        _selectedSquare = null;
        foreach (Move m in _legalMovesFromSelection)
            _squares[m.To].IsLegalTarget = false;
        _legalMovesFromSelection = [];
    }

    private void ClearLastMoveHighlight()
    {
        foreach (SquareViewModel s in _squares)
            s.IsLastMove = false;
    }

    // Dumps the real Board state into the 64 square ViewModels.
    private void RefreshFromBoard()
    {
        foreach (SquareViewModel s in _squares)
            s.UpdateFromBoard(_board);
    }

    // Starts a background analysis of the current position while the human is
    // thinking, feeding the status bar live. It searches a COPY of the Board:
    // the search mutates the position it works on (make/unmake), and the user
    // keeps interacting with the real Board on the UI thread at the same time.
    // The analysis is cancelled as soon as the user moves.
    //
    // NOTE: without a transposition table (v0.2) this analysis cannot transfer
    // knowledge to the engine's next search, so it does not make the engine
    // stronger yet — it provides live evaluation for the user. Once the TT
    // exists, this same search will warm it up and become real "pondering".
    private void StartBackgroundAnalysis()
    {
        if (GameState.GetResult(_board) != GameResult.Ongoing)
            return;

        var cts = new CancellationTokenSource();
        _searchCancellation = cts;

        var analysisBoard = new Board(Fen.Save(_board));
        Color sideToMove = analysisBoard.SideToMove;

        var progress = new Progress<SearchProgress>(p =>
        {
            if (cts.IsCancellationRequested)
                return; // Stale report from an aborted analysis.
            EvaluationText = $"Eval: {FormatScore(p.Score, sideToMove)}";
            DepthText = $"Depth: {p.Depth}/{AnalysisDepth} ply";
        });

        // Fire-and-forget: the analysis has no result to apply, it only
        // reports progress. Cancellation makes FindBestMove return promptly.
        _ = Task.Run(() => _engine.FindBestMove(analysisBoard, AnalysisDepth, cts.Token, progress));
    }

    // Formats a search score for display. The engine reports scores relative
    // to the side to move at the search root; the status bar always shows them
    // from white's point of view (positive = white is better), in pawns.
    // Mate scores are shown as "#N" (mate in N moves).
    private static string FormatScore(int score, Color sideToMove)
    {
        int whiteScore = sideToMove == Color.White ? score : -score;

        // Mate scores are encoded as MateScore minus the ply distance.
        if (Math.Abs(whiteScore) > AlphaBetaSearch.MateScore - 1000)
        {
            int pliesToMate = AlphaBetaSearch.MateScore - Math.Abs(whiteScore);
            int movesToMate = (pliesToMate + 1) / 2;
            return whiteScore > 0 ? $"+#{movesToMate}" : $"-#{movesToMate}";
        }

        return (whiteScore / 100.0).ToString("+0.00;-0.00;0.00");
    }

    // Rebuilds the visual order of the squares. The view paints the collection
    // in reading order (left->right, top->bottom); with white at the bottom the
    // first visual square is a8, and flipped it is h1.
    private void RebuildDisplayOrder()
    {
        DisplaySquares.Clear();
        for (int row = 0; row < 8; row++)
        {
            for (int col = 0; col < 8; col++)
            {
                int rank = _isBoardFlipped ? row : 7 - row;
                int file = _isBoardFlipped ? 7 - col : col;
                DisplaySquares.Add(_squares[Squares.FromFileRank(file, rank)]);
            }
        }
    }
}
