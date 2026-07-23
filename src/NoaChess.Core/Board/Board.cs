namespace NoaChess.Core;

// Represents a complete chess position and is THE SINGLE SOURCE OF TRUTH for
// the game state across the whole application (GUI, engine and UCI share the
// same instance or copies of it; nobody keeps a parallel board).
//
// Dual internal representation:
// - Bitboards per color and piece type: fast for move generation.
// - "Mailbox" (array of 64 piece types): fast to answer "which piece is on
//   this square?" without scanning the 12 bitboards.
// Both structures are kept in sync at all times.
//
// MakeMove/UnmakeMove are incremental: UnmakeMove restores EXACTLY the
// previous state (including the Zobrist hash) using an undo stack. This is
// essential for the engine search, which makes and unmakes millions of moves
// on the same object without cloning it.
public sealed class Board
{
    // FEN of the standard starting position.
    public const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    // Bitboard per [color, pieceType]: where the white pawns are, etc.
    private readonly ulong[,] _pieces = new ulong[2, 6];

    // Bitboard with all the pieces of each color (union of the 6 above).
    private readonly ulong[] _occupancy = new ulong[2];

    // Mailbox: piece type on each square (None if empty).
    // The color is deduced by consulting _occupancy.
    private readonly PieceType[] _mailbox = new PieceType[64];

    // Undo stack for UnmakeMove. Each entry stores everything MakeMove cannot
    // reconstruct on its own (captured piece, previous rights...).
    private readonly Stack<UndoInfo> _history = new();

    public Color SideToMove { get; private set; }
    public CastlingRights CastlingRights { get; private set; }

    // Square where an en passant capture would be possible, or Squares.None.
    public int EnPassantSquare { get; private set; } = Squares.None;

    // Half-moves since the last capture or pawn move (fifty-move rule).
    public int HalfmoveClock { get; private set; }

    // Full move number; starts at 1 and increases after every black move.
    public int FullmoveNumber { get; private set; } = 1;

    // Zobrist hash of the position, maintained incrementally.
    public ulong ZobristKey { get; private set; }

    // Zobrist hash of the PAWNS only (both colors), maintained incrementally.
    // Pawn structure changes rarely compared to piece placement, so an
    // evaluation term that depends only on pawns can be cached under this key
    // and hit almost always (see the engine's pawn hash table).
    public ulong PawnZobristKey { get; private set; }

    // Data that MakeMove stores so UnmakeMove can restore the exact state.
    private readonly struct UndoInfo(Move move, PieceType captured, CastlingRights castling,
                                     int enPassant, int halfmove, ulong zobrist)
    {
        public readonly Move Move = move;
        public readonly PieceType CapturedPiece = captured;
        public readonly CastlingRights CastlingRights = castling;
        public readonly int EnPassantSquare = enPassant;
        public readonly int HalfmoveClock = halfmove;
        public readonly ulong ZobristKey = zobrist;
    }

    // Cuckoo-hash entry for a reversible non-pawn move. The key is the exact
    // Zobrist delta produced by moving one coloured piece between the two
    // squares and changing the side to move. The strict-between mask rejects
    // a slider whose return path is blocked in the current position.
    private readonly struct RepetitionMove(ulong key, Move move, ulong between)
    {
        public readonly ulong Key = key;
        public readonly Move Move = move;
        public readonly ulong Between = between;
    }

    private const int RepetitionCuckooSize = 8192;

    // Masks used to update castling rights after each move.
    // Idea: each square has a mask of the rights that SURVIVE if something
    // moves from/to it. E.g.: moving anything from e1 (white king) removes
    // both white castles; moving from / capturing on h1 removes white short castle.
    private static readonly CastlingRights[] CastlingMask = BuildCastlingMasks();

    // Every geometrically possible reversible move (all non-pawns, both
    // colours) indexed by either of two hashes of its Zobrist delta. Built once
    // at startup; the 3668 entries and table size are the reference layout.
    private static readonly RepetitionMove[] RepetitionCuckoo = BuildRepetitionCuckoo();

    private static CastlingRights[] BuildCastlingMasks()
    {
        var masks = new CastlingRights[64];
        Array.Fill(masks, CastlingRights.All);
        masks[Squares.Parse("a1")] &= ~CastlingRights.WhiteQueenSide;
        masks[Squares.Parse("h1")] &= ~CastlingRights.WhiteKingSide;
        masks[Squares.Parse("e1")] &= ~(CastlingRights.WhiteKingSide | CastlingRights.WhiteQueenSide);
        masks[Squares.Parse("a8")] &= ~CastlingRights.BlackQueenSide;
        masks[Squares.Parse("h8")] &= ~CastlingRights.BlackKingSide;
        masks[Squares.Parse("e8")] &= ~(CastlingRights.BlackKingSide | CastlingRights.BlackQueenSide);
        return masks;
    }

    private static RepetitionMove[] BuildRepetitionCuckoo()
    {
        var table = new RepetitionMove[RepetitionCuckooSize];
        int count = 0;

        for (int c = 0; c < 2; c++)
        {
            for (int p = (int)PieceType.Knight; p <= (int)PieceType.King; p++)
            {
                PieceType type = (PieceType)p;
                for (int from = 0; from < 64; from++)
                {
                    ulong attacks = type switch
                    {
                        PieceType.Knight => Attacks.Knight(from),
                        PieceType.Bishop => Attacks.Bishop(from, 0),
                        PieceType.Rook => Attacks.Rook(from, 0),
                        PieceType.Queen => Attacks.Queen(from, 0),
                        _ => Attacks.King(from)
                    };

                    // Store one direction only. The Zobrist delta is identical
                    // in reverse and the between-squares mask is symmetric.
                    for (int to = from + 1; to < 64; to++)
                    {
                        if (!Bitboard.IsSet(attacks, to))
                            continue;

                        ulong key = Zobrist.PieceKeys[c, p, from]
                                  ^ Zobrist.PieceKeys[c, p, to]
                                  ^ Zobrist.SideToMoveKey;
                        var displaced = new RepetitionMove(
                            key, new Move(from, to, MoveFlag.Quiet),
                            StrictBetweenMask(from, to));
                        int slot = RepetitionHash1(key);

                        while (true)
                        {
                            RepetitionMove victim = table[slot];
                            table[slot] = displaced;
                            displaced = victim;

                            if (displaced.Move == Move.None)
                                break;

                            int h1 = RepetitionHash1(displaced.Key);
                            slot = slot == h1 ? RepetitionHash2(displaced.Key) : h1;
                        }

                        count++;
                    }
                }
            }
        }

        // A changed attack table or piece enumeration would silently make the
        // lookup incomplete, so fail at startup instead of missing cycles.
        if (count != 3668)
            throw new InvalidOperationException($"Expected 3668 reversible moves, got {count}");

        return table;
    }

    private static int RepetitionHash1(ulong key) => (int)(key & 0x1FFF);
    private static int RepetitionHash2(ulong key) => (int)((key >> 16) & 0x1FFF);

    // Squares strictly between two aligned endpoints. Knights return an empty
    // mask; adjacent kings and adjacent sliders naturally do too.
    private static ulong StrictBetweenMask(int from, int to)
    {
        int fromFile = Squares.FileOf(from);
        int fromRank = Squares.RankOf(from);
        int toFile = Squares.FileOf(to);
        int toRank = Squares.RankOf(to);
        int fileDelta = toFile - fromFile;
        int rankDelta = toRank - fromRank;

        if (fileDelta != 0 && rankDelta != 0
            && Math.Abs(fileDelta) != Math.Abs(rankDelta))
            return 0;

        int df = Math.Sign(fileDelta);
        int dr = Math.Sign(rankDelta);
        ulong mask = 0;

        for (int file = fromFile + df, rank = fromRank + dr;
             file != toFile || rank != toRank;
             file += df, rank += dr)
        {
            mask |= Bitboard.SquareBB(Squares.FromFileRank(file, rank));
        }

        return mask;
    }

    // Creates a board with the standard starting position.
    public Board() => Fen.Load(this, StartFen);

    // Creates a board from a FEN string.
    public Board(string fen) => Fen.Load(this, fen);

    // ---------- Queries ----------

    public PieceType PieceTypeAt(int square) => _mailbox[square];

    // Color of the piece on the square. Only valid if the square is not empty.
    public Color ColorAt(int square) =>
        Bitboard.IsSet(_occupancy[(int)Color.White], square) ? Color.White : Color.Black;

    public bool IsEmpty(int square) => _mailbox[square] == PieceType.None;

    public ulong Pieces(Color color, PieceType type) => _pieces[(int)color, (int)type];
    public ulong Occupancy(Color color) => _occupancy[(int)color];
    public ulong AllOccupancy => _occupancy[0] | _occupancy[1];

    // Square where the king of the given color is.
    public int KingSquare(Color color) => Bitboard.Lsb(_pieces[(int)color, (int)PieceType.King]);

    // True if the side to move is in check.
    public bool IsInCheck() =>
        IsSquareAttacked(KingSquare(SideToMove), OppositeColor(SideToMove));

    public static Color OppositeColor(Color c) => c == Color.White ? Color.Black : Color.White;

    // FEN may publish the square crossed by a double pawn push even when no
    // opposing pawn can capture there. Such a square changes no legal move and
    // therefore must not change the position identity used for TT/repetition.
    // This is the same pseudo-legal capturer test used by the reference engine.
    private bool HasEnPassantCapturer(Color side, int square)
        => square != Squares.None
        && (Attacks.Pawn(OppositeColor(side), square)
            & _pieces[(int)side, (int)PieceType.Pawn]) != 0;

    // Is the square attacked by any piece of the given color?
    // Used to detect checks and to validate castling.
    //
    // Symmetry trick: instead of generating the attacks of every enemy piece,
    // we ask "if a knight stood on this square, would it reach an enemy
    // knight?" (and the same for each piece type). It is equivalent because
    // the attacks are symmetric, and much cheaper.
    public bool IsSquareAttacked(int square, Color byColor)
    {
        int c = (int)byColor;
        ulong occ = AllOccupancy;

        // Pawns: the table of the OPPOSITE color is used — a white pawn on
        // 'square' would attack upwards, therefore the black pawns attacking
        // 'square' sit exactly on those squares.
        if ((Attacks.Pawn(OppositeColor(byColor), square) & _pieces[c, (int)PieceType.Pawn]) != 0) return true;
        if ((Attacks.Knight(square) & _pieces[c, (int)PieceType.Knight]) != 0) return true;
        if ((Attacks.King(square) & _pieces[c, (int)PieceType.King]) != 0) return true;

        ulong bishopLike = _pieces[c, (int)PieceType.Bishop] | _pieces[c, (int)PieceType.Queen];
        if ((Attacks.Bishop(square, occ) & bishopLike) != 0) return true;

        ulong rookLike = _pieces[c, (int)PieceType.Rook] | _pieces[c, (int)PieceType.Queen];
        if ((Attacks.Rook(square, occ) & rookLike) != 0) return true;

        return false;
    }

    // ---------- Low-level internal mutators ----------
    // They keep bitboards, mailbox and Zobrist hash in sync.

    private void AddPiece(Color color, PieceType type, int square)
    {
        ulong bb = Bitboard.SquareBB(square);
        _pieces[(int)color, (int)type] |= bb;
        _occupancy[(int)color] |= bb;
        _mailbox[square] = type;
        ZobristKey ^= Zobrist.PieceKeys[(int)color, (int)type, square];
        if (type == PieceType.Pawn)
            PawnZobristKey ^= Zobrist.PieceKeys[(int)color, (int)type, square];
    }

    private void RemovePiece(Color color, PieceType type, int square)
    {
        ulong bb = Bitboard.SquareBB(square);
        _pieces[(int)color, (int)type] &= ~bb;
        _occupancy[(int)color] &= ~bb;
        _mailbox[square] = PieceType.None;
        ZobristKey ^= Zobrist.PieceKeys[(int)color, (int)type, square];
        if (type == PieceType.Pawn)
            PawnZobristKey ^= Zobrist.PieceKeys[(int)color, (int)type, square];
    }

    // ---------- Make / Unmake ----------

    // Executes a move (which must be pseudo-legal: produced by MoveGenerator).
    // It does not check legality regarding check; the legal generator does
    // that by making the move and verifying whether its own king is attacked.
    public void MakeMove(Move move)
    {
        Color us = SideToMove;
        Color them = OppositeColor(us);
        int from = move.From;
        int to = move.To;
        PieceType movingPiece = _mailbox[from];

        // In a normal capture the captured piece sits on 'to'; in an en
        // passant capture the captured pawn is elsewhere and is handled below.
        PieceType captured = move.Flag == MoveFlag.EnPassant ? PieceType.Pawn : _mailbox[to];

        // Store everything needed to undo BEFORE touching anything.
        _history.Push(new UndoInfo(move, captured, CastlingRights, EnPassantSquare, HalfmoveClock, ZobristKey));

        // Remove the current "variable" state (en passant and castling) from
        // the hash; it will be re-added with its new values at the end.
        if (HasEnPassantCapturer(us, EnPassantSquare))
            ZobristKey ^= Zobrist.EnPassantFileKeys[Squares.FileOf(EnPassantSquare)];
        ZobristKey ^= Zobrist.CastlingKeys[(int)CastlingRights];

        // Fifty-move clock: it resets with captures and pawn moves.
        HalfmoveClock = (movingPiece == PieceType.Pawn || move.IsCapture) ? 0 : HalfmoveClock + 1;

        // By default the en passant square disappears; only a double pawn push creates one.
        EnPassantSquare = Squares.None;

        switch (move.Flag)
        {
            case MoveFlag.Quiet:
                RemovePiece(us, movingPiece, from);
                AddPiece(us, movingPiece, to);
                break;

            case MoveFlag.DoublePawnPush:
                RemovePiece(us, PieceType.Pawn, from);
                AddPiece(us, PieceType.Pawn, to);
                // The en passant square is the one between origin and destination.
                EnPassantSquare = (from + to) / 2;
                break;

            case MoveFlag.Capture:
                RemovePiece(them, captured, to);
                RemovePiece(us, movingPiece, from);
                AddPiece(us, movingPiece, to);
                break;

            case MoveFlag.EnPassant:
                // The captured pawn is on the same file as 'to' but on the rank
                // of the capturing pawn (right "behind" the destination square).
                int capturedPawnSq = us == Color.White ? to - 8 : to + 8;
                RemovePiece(them, PieceType.Pawn, capturedPawnSq);
                RemovePiece(us, PieceType.Pawn, from);
                AddPiece(us, PieceType.Pawn, to);
                break;

            case MoveFlag.KingCastle:
            case MoveFlag.QueenCastle:
                // The move encodes the KING's displacement (e1->g1 or e1->c1);
                // the rook is moved here as a side effect.
                RemovePiece(us, PieceType.King, from);
                AddPiece(us, PieceType.King, to);
                (int rookFrom, int rookTo) = move.Flag == MoveFlag.KingCastle
                    ? (to + 1, to - 1)   // Rook from h1/h8 to f1/f8
                    : (to - 2, to + 1);  // Rook from a1/a8 to d1/d8
                RemovePiece(us, PieceType.Rook, rookFrom);
                AddPiece(us, PieceType.Rook, rookTo);
                break;

            default:
                // Promotions (with or without capture): the pawn disappears and
                // the promoted piece shows up on the destination square.
                if (move.IsCapture)
                    RemovePiece(them, captured, to);
                RemovePiece(us, PieceType.Pawn, from);
                AddPiece(us, move.PromotionPiece, to);
                break;
        }

        // Update the castling rights according to the touched squares.
        CastlingRights &= CastlingMask[from] & CastlingMask[to];

        // Re-insert the new variable state into the hash.
        ZobristKey ^= Zobrist.CastlingKeys[(int)CastlingRights];
        if (HasEnPassantCapturer(them, EnPassantSquare))
            ZobristKey ^= Zobrist.EnPassantFileKeys[Squares.FileOf(EnPassantSquare)];

        if (us == Color.Black)
            FullmoveNumber++;
        SideToMove = them;
        ZobristKey ^= Zobrist.SideToMoveKey;
    }

    // Undoes the last move, restoring exactly the previous state.
    public void UnmakeMove()
    {
        UndoInfo undo = _history.Pop();
        Move move = undo.Move;

        // The side that made the move is the opposite of the current one.
        Color us = OppositeColor(SideToMove);
        Color them = SideToMove;
        int from = move.From;
        int to = move.To;

        switch (move.Flag)
        {
            case MoveFlag.Quiet:
            case MoveFlag.DoublePawnPush:
                PieceType moved = _mailbox[to];
                RemovePiece(us, moved, to);
                AddPiece(us, moved, from);
                break;

            case MoveFlag.Capture:
                PieceType movedCapture = _mailbox[to];
                RemovePiece(us, movedCapture, to);
                AddPiece(us, movedCapture, from);
                AddPiece(them, undo.CapturedPiece, to);
                break;

            case MoveFlag.EnPassant:
                RemovePiece(us, PieceType.Pawn, to);
                AddPiece(us, PieceType.Pawn, from);
                int capturedPawnSq = us == Color.White ? to - 8 : to + 8;
                AddPiece(them, PieceType.Pawn, capturedPawnSq);
                break;

            case MoveFlag.KingCastle:
            case MoveFlag.QueenCastle:
                RemovePiece(us, PieceType.King, to);
                AddPiece(us, PieceType.King, from);
                (int rookFrom, int rookTo) = move.Flag == MoveFlag.KingCastle
                    ? (to + 1, to - 1)
                    : (to - 2, to + 1);
                RemovePiece(us, PieceType.Rook, rookTo);
                AddPiece(us, PieceType.Rook, rookFrom);
                break;

            default: // Promotions
                RemovePiece(us, move.PromotionPiece, to);
                AddPiece(us, PieceType.Pawn, from);
                if (move.IsCapture)
                    AddPiece(them, undo.CapturedPiece, to);
                break;
        }

        // Restore the scalar state as it was. The hash is restored directly
        // from the saved copy (simpler and more foolproof than undoing every XOR).
        SideToMove = us;
        CastlingRights = undo.CastlingRights;
        EnPassantSquare = undo.EnPassantSquare;
        HalfmoveClock = undo.HalfmoveClock;
        ZobristKey = undo.ZobristKey;
        if (us == Color.Black)
            FullmoveNumber--;
    }

    // ---------- Null move (for the engine's null-move pruning) ----------

    // "Passes the turn" without moving: only the side to move (and the en
    // passant square, which is no longer capturable) change. This is not a
    // legal chess move — the engine uses it as a search heuristic: "if I do
    // NOTHING and my position is still winning, this branch can be pruned".
    public void MakeNullMove()
    {
        _history.Push(new UndoInfo(Move.None, PieceType.None, CastlingRights,
                                   EnPassantSquare, HalfmoveClock, ZobristKey));

        if (EnPassantSquare != Squares.None)
        {
            if (HasEnPassantCapturer(SideToMove, EnPassantSquare))
                ZobristKey ^= Zobrist.EnPassantFileKeys[Squares.FileOf(EnPassantSquare)];
            EnPassantSquare = Squares.None;
        }

        // A null move is not a played half-move: the fifty-move clock stays
        // unchanged. It still forms a hard boundary for repetition scans.
        SideToMove = OppositeColor(SideToMove);
        ZobristKey ^= Zobrist.SideToMoveKey;
    }

    // Undoes a MakeNullMove (and nothing else — the two calls must pair up).
    public void UnmakeNullMove()
    {
        UndoInfo undo = _history.Pop();
        SideToMove = OppositeColor(SideToMove);
        EnPassantSquare = undo.EnPassantSquare;
        HalfmoveClock = undo.HalfmoveClock;
        ZobristKey = undo.ZobristKey;
    }

    // ---------- Repetition detection ----------

    // How many times the CURRENT position already occurred earlier in the
    // game (0 = first time). Only positions since the last irreversible move
    // (capture or pawn move) can repeat, so the scan is bounded by the
    // halfmove clock. Used for the threefold-repetition rule and by the
    // engine, which treats even a single repetition as a draw score.
    public int CountRepetitions()
    {
        int count = 0;
        int distance = 0;
        foreach (UndoInfo undo in _history) // Newest to oldest.
        {
            // A null move is not part of the legal game history. No repetition
            // may cross it, even though its piece placement is unchanged.
            if (undo.Move == Move.None)
                break;
            if (++distance > HalfmoveClock)
                break;
            if (undo.ZobristKey == ZobristKey)
                count++;
        }
        return count;
    }

    // True if ANY position in the reversible history since the last pawn
    // move, capture or null move has occurred before. Unlike CountRepetitions,
    // this also remembers a cycle after play has already moved on to a new
    // current position. The normal rule-50 window fits on the stack; only an
    // artificially long game continuing past 128 reversible plies allocates.
    public bool HasRepeated()
    {
        if (HalfmoveClock < 4 || _history.Count < 4)
            return false;

        int capacity = Math.Min(HalfmoveClock, _history.Count) + 1;
        Span<ulong> keys = capacity <= 128
            ? stackalloc ulong[capacity]
            : new ulong[capacity];

        int count = 0;
        keys[count++] = ZobristKey;
        int distance = 0;

        foreach (UndoInfo state in _history)
        {
            if (state.Move == Move.None || ++distance > HalfmoveClock)
                break;
            keys[count++] = state.ZobristKey;
        }

        Span<ulong> visited = keys[..count];
        visited.Sort();
        for (int i = 1; i < visited.Length; i++)
            if (visited[i] == visited[i - 1])
                return true;

        return false;
    }

    // True when the side to move has a reversible move that reaches a repeated
    // position. If the earlier occurrence lies strictly after the root
    // (distance < ply), a second occurrence is enough; at/before the root it
    // must itself already have occurred, producing a genuine threefold cycle.
    public bool HasUpcomingRepetition(int ply)
    {
        if (ply < 0)
            throw new ArgumentOutOfRangeException(nameof(ply));
        if (HalfmoveClock < 3 || _history.Count < 3)
            return false;

        var states = _history.GetEnumerator();
        if (!states.MoveNext() || states.Current.Move == Move.None)
            return false;

        ulong originalKey = ZobristKey;
        ulong other = originalKey ^ states.Current.ZobristKey ^ Zobrist.SideToMoveKey;

        for (int distance = 3; distance <= HalfmoveClock; distance += 2)
        {
            if (!states.MoveNext() || states.Current.Move == Move.None)
                break;
            UndoInfo middle = states.Current;

            if (!states.MoveNext() || states.Current.Move == Move.None)
                break;
            UndoInfo earlier = states.Current;

            other ^= middle.ZobristKey ^ earlier.ZobristKey ^ Zobrist.SideToMoveKey;
            if (other != 0)
                continue;

            ulong moveKey = originalKey ^ earlier.ZobristKey;
            if (!TryGetRepetitionMove(moveKey, out RepetitionMove move)
                || (move.Between & AllOccupancy) != 0)
                continue;

            if (ply > distance
                || WasRepeatedBefore(earlier.ZobristKey, distance, earlier.HalfmoveClock))
                return true;
        }

        return false;
    }

    private static bool TryGetRepetitionMove(ulong key, out RepetitionMove move)
    {
        move = RepetitionCuckoo[RepetitionHash1(key)];
        if (move.Move != Move.None && move.Key == key)
            return true;

        move = RepetitionCuckoo[RepetitionHash2(key)];
        return move.Move != Move.None && move.Key == key;
    }

    private bool WasRepeatedBefore(ulong key, int targetDistance, int reversiblePlies)
    {
        if (reversiblePlies < 4)
            return false;

        int distance = 0;
        foreach (UndoInfo state in _history)
        {
            distance++;
            if (distance <= targetDistance)
                continue;

            int delta = distance - targetDistance;
            if (delta > reversiblePlies || state.Move == Move.None)
                break;
            if (delta >= 4 && (delta & 1) == 0 && state.ZobristKey == key)
                return true;
        }

        return false;
    }

    // True if the side has any piece besides pawns and the king. Used by the
    // engine to avoid null-move pruning in king-and-pawn endgames, where
    // "zugzwang" (every move worsens the position) breaks its assumption.
    public bool HasNonPawnMaterial(Color color)
    {
        int c = (int)color;
        return (_occupancy[c]
                ^ _pieces[c, (int)PieceType.Pawn]
                ^ _pieces[c, (int)PieceType.King]) != 0;
    }

    // ---------- FEN support (internal use by the Fen class) ----------

    // Completely empties the board. Only the FEN parser uses it.
    internal void Clear()
    {
        Array.Clear(_pieces);
        Array.Clear(_occupancy);
        Array.Fill(_mailbox, PieceType.None);
        _history.Clear();
        SideToMove = Color.White;
        CastlingRights = CastlingRights.None;
        EnPassantSquare = Squares.None;
        HalfmoveClock = 0;
        FullmoveNumber = 1;
        ZobristKey = 0;
        PawnZobristKey = 0;
    }

    internal void PlacePiece(Color color, PieceType type, int square) => AddPiece(color, type, square);

    internal void SetState(Color sideToMove, CastlingRights castling, int enPassant,
                           int halfmove, int fullmove)
    {
        SideToMove = sideToMove;
        CastlingRights = castling;
        EnPassantSquare = enPassant;
        HalfmoveClock = halfmove;
        FullmoveNumber = fullmove;

        // Hash components that do not depend on the pieces.
        if (sideToMove == Color.Black)
            ZobristKey ^= Zobrist.SideToMoveKey;
        ZobristKey ^= Zobrist.CastlingKeys[(int)castling];
        if (HasEnPassantCapturer(sideToMove, enPassant))
            ZobristKey ^= Zobrist.EnPassantFileKeys[Squares.FileOf(enPassant)];
    }
}
