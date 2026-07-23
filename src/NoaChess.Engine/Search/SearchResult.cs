using NoaChess.Core;

namespace NoaChess.Engine.Search;

// Final result of a search: best move and its score.
public readonly record struct SearchResult(Move BestMove, int Score, long NodesSearched);

// Progress snapshot reported after each completed iterative-deepening
// iteration. Consumers (GUI status bar, UCI "info" lines) use it to show the
// evaluation and the depth being analyzed while the engine thinks.
// Pv is the principal variation — the expected sequence of best play for both
// sides — reconstructed from the transposition table (Pv[0] == BestMove).
public readonly record struct SearchProgress(int Depth, int Score, long NodesSearched, Move BestMove, Move[] Pv);
