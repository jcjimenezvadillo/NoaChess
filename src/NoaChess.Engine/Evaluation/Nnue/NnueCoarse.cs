using NoaChess.Core;

namespace NoaChess.Engine.Evaluation.Nnue;

// The coarse threat lane at evaluation time.
//
// Enumerates the SAME filtered relation multiset the training data carries -
// the DataGen coarse-encode logic ported from record decoding to live Board
// bitboards (pawns threaten P/N/R; minors and rooks P/N/B/R; knights and
// queens P/N/B/R/Q; kings neither attack nor are attacked; the pawn stopped
// dead by a pawn; NO symmetric deduplication, multiplicity is the signal) -
// and adds each relation's weight row into LOCAL copies of the two
// perspective accumulators. The incremental accumulator itself never learns
// about the lane: coarse content is paid per EVALUATION, never per node,
// which is the entire reason this lane can afford what the fine set could
// not. The C# encoder this mirrors has 3000/3000 parity with the trainer's
// own enumeration, and verify_export closes the loop on the exported file.
public static class NnueCoarse
{
    private const ulong NotFileA = 0xFEFEFEFEFEFEFEFE;
    private const ulong NotFileH = 0x7F7F7F7F7F7F7F7F;

    // outStm/outOpp receive accumulator + lane; they must be FtOutputs long.
    public static void AddLanes(NnueNetwork net, Board board,
                                short[] stmAcc, short[] oppAcc,
                                short[] outStm, short[] outOpp)
    {
        int ftOut = net.FtOutputs;
        Array.Copy(stmAcc, outStm, ftOut);
        Array.Copy(oppAcc, outOpp, ftOut);

        short[] weights = net.CoarseWeights!;
        bool stmIsBlack = board.SideToMove == Color.Black;
        ulong occupancy = board.AllOccupancy;

        ulong whitePawns = board.Pieces(Color.White, PieceType.Pawn);
        ulong blackPawns = board.Pieces(Color.Black, PieceType.Pawn);
        ulong pawns = whitePawns | blackPawns;
        ulong knights = board.Pieces(Color.White, PieceType.Knight)
                      | board.Pieces(Color.Black, PieceType.Knight);
        ulong bishops = board.Pieces(Color.White, PieceType.Bishop)
                      | board.Pieces(Color.Black, PieceType.Bishop);
        ulong rooks = board.Pieces(Color.White, PieceType.Rook)
                    | board.Pieces(Color.Black, PieceType.Rook);
        ulong queens = board.Pieces(Color.White, PieceType.Queen)
                     | board.Pieces(Color.Black, PieceType.Queen);
        ulong pawnTargets = pawns | knights | rooks;
        ulong minorSliderTargets = pawnTargets | bishops;
        ulong queenTargets = minorSliderTargets | queens;

        void AddPair(int attCode, int vicCode)
        {
            int white = attCode * 12 + vicCode;
            int black = ((attCode + 6) % 12) * 12 + (vicCode + 6) % 12;
            int stmRow = (stmIsBlack ? black : white) * ftOut;
            int oppRow = (stmIsBlack ? white : black) * ftOut;
            // Vectorized row adds: ~19 relations x 2 rows per evaluation is
            // the lane's whole arithmetic cost, so the 16-lane strides matter
            // for the clock SPRT. The scalar tail covers non-multiple widths.
            int vw = System.Numerics.Vector<short>.Count;
            int i = 0;
            for (; i + vw <= ftOut; i += vw)
            {
                (new System.Numerics.Vector<short>(outStm, i)
                 + new System.Numerics.Vector<short>(weights, stmRow + i)).CopyTo(outStm, i);
                (new System.Numerics.Vector<short>(outOpp, i)
                 + new System.Numerics.Vector<short>(weights, oppRow + i)).CopyTo(outOpp, i);
            }
            for (; i < ftOut; i++)
            {
                outStm[i] += weights[stmRow + i];
                outOpp[i] += weights[oppRow + i];
            }
        }

        void AddHits(int attCode, ulong hits, Board b)
        {
            while (hits != 0)
            {
                int to = System.Numerics.BitOperations.TrailingZeroCount(hits);
                hits &= hits - 1;
                AddPair(attCode, (int)b.ColorAt(to) * 6 + (int)b.PieceTypeAt(to));
            }
        }

        for (int c = 0; c < 2; c++)
        {
            int attacker = c * 6;
            ulong cPawns = c == 0 ? whitePawns : blackPawns;

            ulong capA = c == 0 ? (cPawns & NotFileH) << 9 : (cPawns & NotFileH) >> 7;
            ulong capB = c == 0 ? (cPawns & NotFileA) << 7 : (cPawns & NotFileA) >> 9;
            AddHits(attacker, capA & pawnTargets, board);
            AddHits(attacker, capB & pawnTargets, board);

            ulong pushers = (c == 0 ? pawns >> 8 : pawns << 8) & cPawns;
            ulong blocked = c == 0 ? pushers << 8 : pushers >> 8;
            AddHits(attacker, blocked, board);

            for (int pt = 1; pt <= 4; pt++)
            {
                attacker = c * 6 + pt;
                ulong targets = (pt == 1 || pt == 4) ? queenTargets : minorSliderTargets;
                ulong from = board.Pieces((Color)c, (PieceType)pt);
                while (from != 0)
                {
                    int sq = System.Numerics.BitOperations.TrailingZeroCount(from);
                    from &= from - 1;
                    ulong att = pt switch
                    {
                        1 => Attacks.Knight(sq),
                        2 => Attacks.Bishop(sq, occupancy),
                        3 => Attacks.Rook(sq, occupancy),
                        _ => Attacks.Queen(sq, occupancy),
                    };
                    AddHits(attacker, att & targets, board);
                }
            }
        }
    }
}
