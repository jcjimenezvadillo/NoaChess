using NoaChess.Core;

namespace NoaChess.Engine.Search;

// Final result of a search: best move and its score.
// Depth is the last iteration this result is backed by. It exists so the Lazy
// SMP vote can tell a worker that searched 17 plies from one still at 1: their
// SCORES ARE NOT COMPARABLE, and comparing them anyway let a blind helper
// overrule the main worker. Defaulted so every existing construction site keeps
// compiling and reports "no depth claimed", which never wins a vote.
public readonly record struct SearchResult(Move BestMove, int Score, long NodesSearched,
                                           int Depth = 0);

// Progress snapshot reported after each completed iterative-deepening
// iteration. Consumers (GUI status bar, UCI "info" lines) use it to show the
// evaluation and the depth being analyzed while the engine thinks.
// Pv is the principal variation - the expected sequence of best play for both
// sides - reconstructed from the transposition table (Pv[0] == BestMove).
public readonly record struct SearchProgress(int Depth, int Score, long NodesSearched, Move BestMove, Move[] Pv);
