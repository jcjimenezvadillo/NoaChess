# CHANGELOG

## 2026-08-13 (v5.0.1) - the engine spent six seconds recapturing a queen it had to recapture

Found in a real game rather than by a test: [lichess.org/E6wD3ggu](https://lichess.org/E6wD3ggu),
move 47, 180+2, forty-three seconds on the clock.

```
r7/2k3B1/3b4/1Pp5/5Q2/7P/6P1/3R3K b - - 0 47
```

Eleven pieces. White takes the queen with Qxf4, and `Bxf4` is the ONLY capture on the board: not
taking it back is giving a queen away. The engine took **six seconds** over it.

**A move being OBVIOUS and a position being WON are different things, and the time manager treated
them as one.** The only mechanism that could cut the budget required a decisive score, `|score| >=
700`. Here the evaluation was -1.6 pawns, so nothing ever fired and a forced recapture was paid for
at the full rate. That is not an edge case: a recapture in a level position is the clearest easy
move there is, and it was excluded by construction.

**The first fix was wrong, and measuring it is what said so.** Cutting the clock when the best move
has been stable for many iterations looks right and is not: a quiet endgame is stable too. Over 40
positions from the bot's own games it fired on 15 and CHANGED the move in two middlegames where the
engine had been thinking for nine seconds and was right to.

What actually separates a forced move is not stability but **effort**. On a forced move every
alternative is refuted at once and nearly the whole iteration goes into the move that gets played;
a position with a real choice spreads its nodes over several. The root now measures the share of
each iteration spent on the move it chooses, and the cut requires **90%** of it, on top of the move
having been settled since depth 4 and never once changed.

**And the first deployment was still too timid.** It shipped, and the same
complaint came straight back from a live game: two legal replies to a check, one
of them a recapture, and the engine took nearly two seconds. The trace showed the
cut firing correctly with `share=1.000` - every single node of the iteration had
gone into that move - and then leaving 30% of a six second budget on the table
anyway. When the search reports the decision was UNANIMOUS, 30% is the wrong
answer. At a share of 0.95 or better the cut now uses the same fraction the
decisive-score case does, 12%:

| position | 5.0.0 | first cut | now |
|---|---|---|---|
| rook recapture, 2 legal moves | 6.30 s | 1.90 s | **0.81 s** |
| queen recapture | 4.08 s | 1.63 s | **0.74 s** |

Same move in every case.

Measured over the same 40 positions, single-threaded:

| | before | after |
|---|---|---|
| cut fired on | - | 13 of 40, **move unchanged in every one** |
| total time over 40 positions | 238.1 s | 221.9 s (**6.8% less**) |
| moves differing | - | **0 of 40** |

**The noise floor was measured before trusting any of this.** The same binary against itself
differs on **three of the same forty**, because a search that stops on a wall clock is not
reproducible: one position swung between 12.40 s and 1.22 s across two runs of identical code. An
earlier run of the comparison showed three differences and every one of them was in a position where
the cut had NOT fired. Across both runs the cut has never changed a move in a position where it
acted.

**Not measured in Elo.** No SPRT has been run. What is established is that the flagrant waste is
gone, that the cut fires rarely and only where the search itself says the choice was never in
doubt, and that the engine does not think less overall.

354 tests (106 Core, 248 Engine).

## 2026-08-12 (v5.0.0) - the desktop board stops being a test harness

The Windows board has been rebuilt. What was there worked and was honest about what it was: a way to click a piece, watch the engine answer and check that the whole stack held together. It could not take back a move, could not show the game that had been played, and showed one line of engine output. This release makes it a board you can actually analyse on.

**The engine is untouched.** Nothing in this version changes how a move is chosen, so nothing in it is measurable in Elo. The version number moves to 5.0.0 because the version constant is shared: `ChessEngine.Version` is what the UCI `id name` reports and what the window title shows, so the engine now reports 5.0.0 as well without a single search change behind it.

### Moving pieces

Pieces are **dragged**. The piece leaves its square, follows the cursor with a shadow under it and drops where it is let go; a drop outside the board or on an illegal square puts it back. Clicking the origin and then the destination still works and always did, and the two get along: a press that does not move is a selection, a press that moves is a drag.

Legal destinations are marked the way online boards mark them: a **dot** on an empty square, a **ring** around a piece that can be taken. The square the cursor is over during a drag is outlined.

### Premoves

A move entered while the engine is thinking is **held and played the instant the turn comes back**. If the position that arrives makes it illegal it is discarded and nothing happens, which is what makes it safe to use at all. Right-click cancels one. A premoved promotion takes the queen rather than stopping to ask.

The candidate squares shown while premoving are a guess by construction: the opponent has not moved yet. They come from the piece's movement pattern over the current occupancy, and the real legality check happens later, against the position that actually turns up.

**Squares occupied by your own pieces are offered as premove targets**, which is not an oversight. The premove that matters most is the RECAPTURE, and when it is entered the target still holds your own piece precisely because the opponent has not taken it yet. Filtering those squares out removed the main reason to premove at all; being generous costs nothing, since an illegal premove is discarded anyway.

### Navigation and the move list

First, previous, next and last, on buttons and on Home, Left, Right and End. The move list writes the game in **figurine notation** (the piece as a picture rather than a letter, including the piece a pawn promotes to), highlights the move on the board and jumps to any move that is clicked.

Playing a move from a rewound position **replaces the continuation**. That is deliberate and it is what makes exploring an alternative line one gesture: go back, play something else. Take back does the same thing for the last full move, so the user gets their own decision back rather than a position where it is the engine's turn again.

### The opening has a name

Above the move list, updated as the game goes: **B90  Sicilian, Najdorf
Variation**. It goes into the saved PGN as the `ECO` and `Opening` tags, which
is how every database indexes a game.

**Matching is by POSITION, not by move order.** Each line of the table is
replayed once at startup and indexed by the Zobrist key of the position it
reaches, so a line arrived at by transposition gets the same name as the direct
route - which is the whole reason databases name openings by position. The
Italian reached through 3.Bc4 Bc5 and through the same moves in a different
order come out as the same opening, and there is a test that says so.

Walking back through a game shows the name it had **at that point**, not the one
it ended up with. The saved PGN carries the name at the END of the game, because
a file tagged with what the game was called on move three would be wrong about
the game.

The table is a plain text file that ships with the program, `openings.tsv`, and
anyone can add to it without a rebuild. It holds 86 positions: the main lines,
not the full ECO index. Every entry is a well established name for the position
it points at, and the deeper sub-variations are deliberately absent rather than
guessed at - **a confidently wrong opening name is worse than no name**. There
is a check that replays every line in the shipped table and fails if any move in
it is not legal.

### An opponent you can beat

The engine plays at about 3271 CCRL. Shipping that as the only setting would be
shipping a program almost nobody can win a game against, which is a bad product
however good the engine is.

**New game** now also asks how hard it should try, from Beginner to Full
strength. The limit is a **node cap**: the engine may look at that many
positions and no more. That is honest in a way a "skill level" dial is not - it
says exactly what it does, and it degrades the way a weaker player does, by
seeing less far rather than by playing deliberate nonsense. Engines that reach a
target rating by injecting random blunders produce moves no human of any strength
would choose.

**The level names are descriptions, not rating claims.** No gauntlet has been run
at these caps, so putting an Elo number next to them would be inventing a
measurement, and the dialog says so.

### The engine varies its opening

An engine is deterministic: the same position gives the same move, so every game
against it is the same game, and it stops being interesting on the third night.

For the first eight plies it now picks at random among the moves it rates within
a third of a pawn of its best. That is enough spread for a real choice of
opening and not enough to hand anything away, and it costs nothing at a stage
where the clock has nothing at stake yet. It can be turned off.

### Hint

The move the engine would play, drawn as a single arrow, on Ctrl+H. It is the
only arrow that appears during a game, and it appears because the player asked
for it: the automatic candidate arrows stay out of a game on purpose, since an
opponent permanently telling you what to play is not an opponent.

### The clock, and choosing a side

**New game** now asks the two questions a chess program asks: which side you take, and how time is measured. Three answers to the first (white, black, or both sides for analysis) and three shapes for the second:

- a **real chess clock**, base time plus increment, with the usual presets from 1+0 to 30+0 and custom fields for anything else. Both clocks are shown beside the players, the running one is lit, the last twenty seconds turn red, and a side that runs out **loses on time**.
- a **fixed budget per move**, which is what the previous version always did.
- a **fixed depth**, where neither side can lose on time.

The clocks are measured with a stopwatch rather than by counting timer ticks. A UI timer is not a metronome, and the engine's own progress reports keep the UI thread busy, so counting ticks would run the clocks slow, in the player's favour and against the budget the engine was given. A clock game hands the engine's remaining time to the engine's own time manager, the same scheduler it uses under UCI.

The clock starts with the **first move**, so an application that is merely open burns nobody's time.

### Games in and out: PGN

Open, save, copy and paste, which is how a chess program exchanges games with
every other chess program.

The reader keeps the **main line** and understands the rest well enough to skip
it correctly: brace comments, rest-of-line comments, numeric glyphs, and
variations that nest inside variations. Move numbers glued to their move
("1.e4") are read, which a naive split loses entirely. A file holding several
games is split and the first is loaded.

**Nothing is guessed.** Every token is resolved against the position it appears
in, so a move that is not legal there stops the load and says which move it was,
rather than skewing everything after it. A game that did not start from the
initial array carries its FEN both ways.

Loading a game switches to **analysis** mode on purpose: a loaded game is being
reviewed, not continued, and a play mode would have the engine answer the last
move of somebody else's game the moment it appeared.

Files are read as UTF-8 and fall back to Latin-1 when the bytes say so, which
is most of the PGN in the world, and written as UTF-8 with no byte-order mark,
which is what every database expects.

**Game details** edits the tag pairs, so a saved file says who played rather
than "You".

### Every move, priced

The engine panel has a second view: **all moves**, every legal move of the
position ranked best first with the score it earns and how far behind the best
it is, with a bar behind each row so the shape reads before any number does.
Clicking one plays it.

This is what a single best line can never show. Each move is searched in its own
subtree at the same depth, which is what makes the numbers comparable with each
other; a search of the root prunes most of these away long before it could price
them, and that is its job rather than a defect.

**On the board, while analysing**, the best few are drawn as arrows on a green
to orange scale: green for the best, sliding through yellow to orange as they
give more away, with the best one heavier and more opaque so the eye finds it
first. The scale saturates at three pawns, past which every move is simply bad
and a finer gradient would only make good moves look worse than they are.

The arrows appear in **analysis only**, including any game opened from a PGN.
During a game they would be telling the player what to play, which is a
different program from this one. The ranking that feeds them runs automatically
at a shallower depth on every position, because it happens on every position and
has to be over before it becomes an annoyance; the button runs the full depth.

The full-depth pass is published **as it goes**. Fifty moves at depth twelve
take a minute and a half, and a panel that stays empty for a minute and a half
looks broken: this way the good moves surface within a second and settle as the
rest come in. Closing the panel abandons the pass. The arrows are drawn once, at
the end, because redrawing them per move scored would have them twitching around
the board for the whole minute.

### Reviewing a whole game

What Fritz and ChessBase call full analysis: every position of the game is
searched and the moves that lost material or advantage are marked in the move
list - **?!** for an inaccuracy, **?** for a mistake, **??** for a blunder - with
an accuracy percentage and the counts for each side underneath.

Two decisions worth stating:

- **Fixed depth, not a time budget.** A timed review would make the same move a
  blunder in one run and fine in the next, depending on how busy the machine
  was. The depth is settable (8 to 20) and defaults to 12.
- **One search per position, not two.** A search already returns the score AND
  the move it would have played, so the position before a move and the position
  after it are each searched exactly once and the result of one step is the
  "before" of the next. The first draft searched twice and took double the time
  for the same answer.

A position that is already decided is not marked: sliding from +9 to +6 is not a
mistake anyone needs pointing out.

### Practise the positions you got wrong

A review tells you where you went wrong. **Practise** puts you back in the chair:
it replays the game to the position before each mistake and waits for you to
find something better, one exercise at a time.

The verdict compares your attempt against **the move you played in the game**,
not against perfection. That is the question a player is actually asking - would
I do better this time - and a move that gives up less than the game move is a
better move whether or not it happens to be the engine's first choice. A move
that gives up nothing is simply right, and it says so even when the engine would
have chosen differently.

Only mistakes and blunders become exercises. An inaccuracy is not worth setting
a position up for, and there would be a dozen in every game.

Trying a different move **replaces the continuation** - that is what trying a
different move means - so practice snapshots the game first and puts it back
between exercises and at the end. Without that the second exercise would be set
in a game the first one had already destroyed.

### Several games in one file

A PGN file usually holds a collection, and opening the first game while saying
nothing about the rest hides most of the file. Opening one now lists what is in
it: players, result, length and event, one row each. Double-clicking opens.

### Where the game was decided

**This one does not exist in any other chess program**, and it is the reason to
put an installer round this.

Every chess program marks the moves you got wrong. None of them tells you where
the DECISIONS were, and those are not the same thing. In a forty-move game,
thirty-five moves may be forced or irrelevant - it made no difference what you
played - and the game turned on three positions. The usual review flags move 24
because half a pawn went missing there. It never mentions move 19, where the
evaluation swung two pawns depending on what you chose, and where you happened
to guess right.

That gap is measurable, and the ranking of every legal move already measures it:
**the distance between the best move and the second best is exactly how much the
choice was worth**. Twenty moves within a tenth of a pawn is not a decision.
Best +1.5 against second best -0.8 is the fork in the road, whether or not the
player found it.

A review now runs a second pass over the game asking that question, and reports
the positions in order of how much they were worth, saying for each one what was
played, what the best was, and how long the player actually spent. Clicking one
jumps the board to it. The headline is the point:

> 3 positions decided this game. You found the best move in 2 of them.
> You spent 6% of your thinking time on them.

That last sentence is why the move clock is recorded. Knowing where the
decisions were is only half of it; the other half is finding out whether you were
looking. Time is only mentioned when there was a real clock to measure - a game
replayed from a file says nothing about it rather than inventing a percentage.

The pass is deliberately **shallow**. "How far apart were the alternatives" is a
different question from "was that move a mistake", and its shape is visible long
before the exact numbers settle; a deep pass would multiply a thirty-move scan by
every position of the game for an answer that looks the same.

### The material beside each player, and a list that stays put

Three things reported from using it.

**Only the SURPLUS is shown now, not the tally.** Every piece taken used to be
drawn, so an even trade put a pawn on each side that cancelled visually to
nothing while taking up the strip. What is drawn is the difference: a pawn each
way disappears from both sides, and a side that is a knight up shows one knight.
Per piece type that difference needs no bookkeeping, because both armies started
with the same number, so (initial - theirs) - (initial - mine) reduces to
mine - theirs.

**The dark pieces were a silhouette on a silhouette.** Black pieces on a dark
panel were barely there. They are drawn with a light halo behind them now, which
costs nothing and leaves the piece set alone; the row of white pieces gets none,
because it never needed one.

**The move list no longer moves under the hand clicking it.** The scroll was
jumped to a computed fraction of the list on EVERY ply change, so clicking a
move you could already see threw the list somewhere else. Two rules replace
that: a move picked from the list scrolls nothing at all, and a move reached any
other way scrolls only when it is off screen, and then by the smallest amount
that brings it back. Verified against the real window: clicking a visible move
leaves the offset at exactly where it was, and stepping to a move below the fold
still brings it into view.

### The keyboard, and the space bar playing a move for either side

Set up a king and pawn against a king, press space, and the engine plays the
move for whoever is to move. Press it again and it plays the reply. Nobody has
to be assigned a colour: it is the position on the board that decides who is
being moved for, which is what makes a set-up ending walkable one move at a
time. While the engine is already thinking it still means the other thing it
always meant, stop and take the move you have.

**The bare keys had to be taken back from whatever had the focus.** A WPF button
treats space as a click and a list treats the arrows as its own navigation, and
both of them HANDLE the key before the window's input bindings are ever
consulted. So after clicking any button the space bar silently repeated that
button, and after clicking a move in the list the arrows scrolled the list
instead of walking the game. The arrows, Home, End and space are caught on the
way DOWN at the window now, which is the only place that ordering can be won, so
they do the same thing wherever the focus happens to be, the way the established
board programs behave. They are left alone while the caret is in a text box,
because a FEN needs its spaces and its arrows, and combinations with a modifier
are left to the ordinary bindings, which nothing steals.

### Other engines, and a game between two of them

Any UCI engine on the machine can be loaded and given a colour. **Game, Engines**
keeps the list; **New game** chooses who plays white and who plays black, and the
three answers for each are the same: you, NoaChess, or one of the engines on the
list. Setting both sides to an engine starts a game that plays itself.

An engine is added by pointing at its executable, and it is accepted only if it
answers `uciok`. A program that starts and says nothing is rejected with what it
did say, so a wrong file is a sentence rather than a hang. The wait before giving
up is thirty seconds on purpose: an engine loads its evaluation network before it
answers a single word, and this engine's own debug build takes ten.

An engine is given the same hash size and thread count as the built-in one, and
only for the options it actually declared during the handshake: a match where one
side had been handed eight threads and the other kept its own default would be
measuring the settings rather than the engines.

Everything the window already shows works the same during a match. The evaluation
bar, the depth, the node count and the variation follow **whichever engine is
thinking**, so a game between two other programs reads as one conversation rather
than going blank on alternate moves. The clock, the move list, the opening name
and PGN saving do not know the difference.

**Pause** stops a match between the move that is being searched and the next one,
and tells every engine that could be working to stop, not just the built-in one.
Resume picks it up from the position on the board.

An engine that answers with a move that is not legal in the position it was given
has lost track of the game. That colour is not asked again and the game stops
where it is: asking again would only get the same answer for ever, and playing it
would corrupt a game that is still worth saving.

Each engine is a child process, started on its first move and shut down with the
game. Closing the window while one is thinking is a normal way to close the
window, and it stops being an unhandled exception on a thread nobody owns.

### The rest of it

- **Replay**: the game plays itself through from the start, at three speeds.
- **The board as a picture**, copied to the clipboard or saved as a PNG, rendered
  at twice its logical size so it is usable in a document rather than a blurry
  screenshot.
- **The window remembers where it was**, unless that position falls outside every
  screen currently attached: a window restored onto a monitor that has been
  unplugged is a window nobody can reach.

### Endgame tablebases

The Syzygy folder is a setting now, remembered between runs. With the tables
loaded the last few pieces stop being an estimate and become a fact, which is
the difference between an endgame analysis being useful and being decorative.
The menu bar says which are loaded.

### Setting up a position

A board editor, reachable from Game. Pick a piece from the rack and click the squares; the right button clears one. Side to move and castling rights sit alongside, and the FEN box works both ways: it shows what has been built and it loads whatever is pasted into it.

It refuses to hand back a position that is not one, and says why: a side without exactly one king, a pawn on the first or last rank, or a check against the side that is NOT to move, which would mean the previous move left its own king attacked. Placing a second king moves the first rather than making two, and castling rights are dropped silently when the king or that rook is not home.

### The engine no longer thinks before the game starts

The idle analysis used to begin on the start position, so opening the window put a core to work commenting an opening the player had not chosen yet. Nothing is analysed until the first move has been played. Analysis mode is the exception, because there the engine's opinion is what the window is for.

### Engine output

A table, one row per completed search iteration, newest first: depth, evaluation, elapsed time, node count and the **principal variation written in real notation**. The variation is replayed on a copy of the position to be written that way, and a move that does not fit stops the line rather than corrupting it, because a variation reconstructed from a hash table can end in a stale move.

The evaluation also drives a **bar beside the board** through the same win-probability curve online boards use, so it tracks winning chances rather than material, and it turns over when the board is flipped.

- **Dark theme throughout.** Every control the window uses is retemplated, because the stock ones are drawn for a light window and come back as white boxes on a dark one.
- **Five board colour schemes**, coordinates on the edge squares, and check drawn as a glow under the king.
- **Captured pieces and the material count** above and below the board.
- **The NNUE network is loaded automatically** if one is found beside the executable or in the repository, so the board plays at the engine's real strength instead of on the classical evaluator. Which evaluator is active is stated in the menu bar.
- **Threads and hash size** are settings, and they persist between runs along with the time control, the board colours and the display options.
- **Play as white, play as black, or analysis** where the user plays both sides and the engine only comments.
- Copy the position as FEN, copy the game as PGN, paste a FEN to set up a position.
- The release notes open once after an upgrade instead of on every launch.

### Seven defects found while building it

None of them was found by reading the code. Every one came out of driving the
window from a harness and looking at what it did, or from somebody using it.

**Two searches could run in the same engine at once.** A search cancels the previous one and waits for it, but cancelling is itself an await: three requests arriving before the first of those awaits resumes all saw an idle engine, and two of them went on to search the same instance. The engine documents that this is not allowed, and it is not a theoretical hazard - driving the board quickly crashed it with an index out of range deep inside the move loop. Search requests now queue on a gate, and one that is overtaken while it waits gives up instead of running a search nobody wants any more.

**Engine progress could reach the panels on the wrong thread.** The reports were marshalled by the ambient synchronization context, which is not installed everywhere the ViewModel can be built. They now go through the dispatcher explicitly.

**Closing the window while the engine was thinking threw.** Disposing an engine disposed the gate that serialises its searches, and the search still waiting on that gate then had to run its finally and release it. The result was an ObjectDisposedException on a thread nobody is catching, in both the built-in engine and the external ones. The built-in case is the ordinary one: in analysis mode the idle search is running almost all the time, so almost any close hit it. The gate is not disposed any more, which costs nothing: a SemaphoreSlim that was never waited on as a handle holds nothing worth reclaiming.

**Pausing a game between two engines only silenced the built-in one**, which in a match between two other programs is precisely the engine that is not working.

**A move refused as illegal did not actually stop the game.** The status line said it had, and nothing had: the scheduler came straight back and asked the same engine the same question. An engine that answers with a move that is not legal in the position it was given has lost track of the game, so that colour is not asked again.

**The evaluation waited on work nobody had asked for.** In analysis mode every
position was put through a full ranking of every legal move BEFORE the analysis
of the position itself was allowed to start. That is thirty to forty searches,
so after each move the evaluation, the depth and the variation simply were not
there: measured on the start position, **2727 ms of nothing**, and a middlegame
with more legal moves is worse. The order is inverted now, and the ranking is
also deferred by six hundred milliseconds after the position settles: the
evaluation appears in **71 ms** and the arrows follow it, and stepping quickly
through a game no longer pays for a ranking of every position it passes through.
The old comment in that code said the pass "delays the evaluation, the depth and
the variation as well" and treated a depth cap as the answer. It was not the
answer; the ordering was.

**The opening name handed out null strings and crashed the PGN.** It is a struct, so "no opening" is its default value, and a default struct has null strings whatever the declaration says. That is every game set up from a position, and saving one threw. The fix is in the struct rather than in each caller: the stored fields are nullable and the ones that are read are not, so nobody gets a null from it. This is the second crash from that same trap, which is why it was worth fixing at the source.

### Notation

`San.Format` joins `San.TryParse` in the Core: the reader had no writer, and a move list needs one. It is the exact inverse of the parser, including the minimum disambiguation each move needs, and it leaves the board it inspects exactly as it found it. Twenty tests, one of which round-trips every legal move of a middlegame position through both directions.

354 tests (106 Core, 248 Engine).

## 2026-08-10 (v4.7.0) - two ways the engine could choose a move it had already refuted

Both defects were found while building a harness that replays a lost bullet game while speaking the ponder protocol and spending the opponent's real think time. That last part was the whole difficulty: five earlier reproduction attempts resolved the ponder instantly, which leaves the move budget intact, and with a full budget the engine found the right move every single time. The engine charges the ponder's wall clock against the next search, so in the game the reply came out of a 10 ms search and in every reproduction out of a 5 s one.

The harness still does not reproduce that specific game. What it did do is put the search under the conditions the game ran under, and two defects were visible there on inspection.

### A worker that believed it was being mated took the move from everyone else

The Lazy SMP vote tested for a decisive line with `Math.Abs(score) >= MateScore - 1000`, so **a forced LOSS satisfied it**. That test short-circuits the vote ahead of any weighting, so one worker announcing its own defeat took the move outright, whatever the other workers had found. And once such a result held the lead, the branch below it kept whichever decisive score had the larger magnitude - which among losses is the **shortest** mate. Given only bad news, the vote actively chose to be mated sooner.

The symptom is the same one v4.6.2 addressed from the other end: the engine plays a move its own printed variation never mentions, because the variation comes from the main worker and the move comes from the vote. The depth filter added in v4.6.2 stopped blind shallow workers from voting; it did nothing about a deep worker with a lost score, which passes the depth test cleanly.

Decisive now means winning. Being mated is not a claim worth acting on - every alternative is at least as good - and the existing `score > -decisiveBound` test already kept lost results out of the ordinary vote, so with the sign restored they can no longer win anything. A worker that finds a genuine forced win still overrules one that does not. Three tests in `VoteBestResultTests` cover it.

### A soft stop inside a fail-low returned a score the search never proved

When the aspiration window fails low, every root move came back at or below alpha and the score is an upper bound waiting on a re-search. If the soft time limit fired during that re-search, the partial result was recorded anyway, typically hundreds of centipawns below the last completed iteration.

That number then left by two doors: it is what `info score` prints, and it is what the SMP vote weighs the worker by, since the weight **is** the score. A worker stopped inside a fail-low was handing the vote a figure its own next re-search would have refuted - and a big ponder credit makes exactly this stop, at exactly this moment, the normal case rather than a rare one.

The last completed iteration now survives a fail-low stop. The partial is used only when no iteration has finished at all and there is nothing else. Fail-high is untouched: a move proved at or above beta is good news and safe to take.

### Measurement

Neither fix is measurable the same way. The fail-low change affects every search and is on the single-thread SPRT (`sprt_463_vs_462.bat`). The vote change does not exist below two threads and is on `sprt_463_vs_462_smp.bat` at four threads, the configuration the bot runs. The vote defect is rare and catastrophic rather than a steady drip, so that SPRT is there to show the fix costs nothing; it is not the kind of bug an Elo number is good at finding.

## 2026-08-10 (v4.6.2) - the training pipeline was the bottleneck, not the search: +195.4 Elo from a change the engine never sees

The largest measured gain in the project's history, and it required no engine change at all. It also cost two blunders in live games from a bug this release fixes, both of which are documented below because the sequence matters more than the result.

### The feature transformer was mostly zero, and it was measurable

Quantising `ds1e60` - the net that had been shipping since v4.4.0 - and comparing it against its own float weights:

- **85.6% of the feature transformer quantised to exactly zero** at QA=255 (89.8% at QA=127)
- **2,221 of 22,528 features were entirely dead**: they contributed nothing to any evaluation
- the largest quantised weight reached **52 of the 127** available

Then, quantising each stage alone against the float forward pass over 4,000 real positions:

| stage quantised | mean absolute error |
|---|---|
| feature transformer only | **38.77 cp** |
| head (L1 + output) only | 4.9 cp |

against a mean absolute evaluation of 231 cp. The engine was evaluating **16.6% away from the network that had been trained**, p95 98 cp, essentially all of it in the transformer.

The cause is structural. A HalfKAv2_hm index is (king bucket, piece, square), so each of the 22,528 features only ever fires when the king sits in one of 32 regions. Individually they are rare, their weights never grow, and quantisation rounds them away.

### Feature factorization: +195.4 +/- 57.5 Elo

Training now optionally adds **704 virtual (piece, square) features**. Every real feature fires its own row **and** the king-independent row it is a copy of, so the shared row collects 32x the gradient and carries the bulk signal while the real rows learn only the per-king-bucket deviation. At export each virtual row is added into its 32 copies.

**The fold is exact, not an approximation**: the sum the accumulator computes over the active features is unchanged, so the exported file evaluates every position to the value the trained net produces. The engine, the header and the arithmetic are untouched - the virtual features exist only while gradients are flowing.

Trained on the same 70 files, the same 60 epochs, the same hyperparameters and the same export arch as the baseline, one axis:

| | ds1e60 | fact60 |
|---|---|---|
| validation loss | 0.005301 | 0.004848 |
| quantisation error | 38.79 cp | **17.63 cp** |
| correlation with the teacher | 0.9402 | 0.9531 |
| regression slope | compressed | 0.976 |
| weights quantising to zero | 85.6% | **21.3%** |
| dead features | 2,221 | **1,024** |

The dead count of exactly 1,024 is worth keeping: those are precisely the **structurally impossible** features, pawns on ranks 1 and 8, being 32 impossible base features across 32 king buckets. The factorized net has **zero legal features it ignores**, against the baseline's 1,197.

**SPRT: +195.4 +/- 57.5 Elo, LOS 100%, H1 accepted in 102 games** at 10+0.1, the same binary on both sides with only `EvalFile` differing. This is not a CCRL figure - it is measured against a version of itself, and the cascade has never added up in this project.

Predicted at 35% to pass, with eval scale drift named as the main risk. Both wrong. That is the second failed prediction of this kind (output buckets were predicted at ~0 and measured +20.1), and the pattern is consistent: on this engine, fixing a measured defect in the training pipeline has paid far more than expected.

### Quantization-aware training, implemented and verified, not yet measured in games

Behind `--qat`. Weights are rounded and activations floored inside the forward pass with a straight-through estimator, at exactly the scales the exporter uses. The two roundings are different on purpose: weights round because export rounds them, activations **floor** because the engine's L1 divides by QB with integer division, which truncates. Rounding there would train against arithmetic the engine does not perform, and the bias is systematic rather than noise.

With it on, the trained net's float forward pass and the engine's integer arithmetic **agree to 0.47 cp**, inside the engine's final truncation to a whole centipawn. Without it they differ by up to 31 cp.

### Weight decay reached the feature transformer

`Adam(model.parameters(), weight_decay=...)` applied decay to the transformer as well as the head, and because `EmbeddingBag` produces a dense gradient, every one of the 22,528 rows was decayed on every one of ~1.1M steps, including rows whose feature never appeared in the batch. `--ft-weight-decay` separates them. On a matched pair (same data, same epochs, decay 1e-5 against 1e-4) the mean absolute weight moves 0.00149 to 0.00045 and the dead fraction 86.2% to 95.5%.

Honest caveat: on that same pair the **relative quantisation error barely moved** (15.8% to 17.3%), so the mechanism by which this buys Elo is plausible and not established. That is what its SPRT is for.

### The Lazy SMP vote could be overruled by a blind worker

`SearchResult` carried `(Move, Score, Nodes)` and no depth, so the vote compared a worker at depth 1 with one at depth 17 as though their scores meant the same thing. They do not: the shallow one reports a rosier number purely because it has not met the refutation yet, and the score weighting then hands it the vote by a wide margin.

On 2026-08-09 this played **two mates in one in four minutes** on Lichess. Both games printed the correct move on every `info` line and then played a different one, because the PV comes from the main worker and the move came from the vote:

```
info depth 12 score cp -1613 nodes 554 pv e7f6 e3c2 b7b6 ...
bestmove c2e3
```

With the real numbers, the depth-1 helper won **656 votes to 14**. `SearchResult` now carries the depth and a helper may only vote from at least the main worker's depth; deeper helpers still vote, which is the entire point of the vote.

This defect had been recorded for months as an unexplained "PV does not match bestmove" residue at ~2% of moves. It was not cosmetic: it decides moves.

### Helper threads are a persistent pool

They were brand new OS threads created on **every search**, which made the thread count a fixed latency cost paid before a single node was searched. Measured, delay to `info depth 1`:

| Threads | per-search threads | persistent pool |
|---|---|---|
| 1 | 0.7 ms | 0.7 ms |
| 8 | 2.9 ms | 0.7 ms |
| 16 | 6.5 ms | 3.4 ms |
| 24 | **34.1 ms** | **2.2 ms** |

The curve flattens: 24 threads now cost what 2 did. The bot had been forced down to 8 threads to stay alive at 1+1.

**This change made the vote bug far more frequent, which is how it was found.** With threads created per search the helpers started ~34 ms late and often never wrote a result at all, leaving `Move.None`, which the vote skips. Parked threads start instantly, so they now reliably returned a depth-1 result - and won.

### Ponderhit keeps the deeper answer

Measured over 127 pondered moves: the ponder reaches **depth 21.6** against 19.5 for a fresh search, and **91% of relaunches play exactly what the ponder concluded**, in a quarter of the time. The fast reply after a ponderhit is the design working, and an earlier claim here that it was a time-management defect was wrong.

The tail was not. One case in a hundred looked like this:

```
ponder depth 43 said c5d3 | relaunch depth 1 said a5b5 | played a5b5
```

A depth-1 search overruling a depth-43 one on the same position, because a long opponent think consumed the credit and left the relaunch nothing. When the relaunch comes back five or more plies shallower **and** disagrees, the pondered move now stands. The guard has been verified not to leak across positions; it has **not** been observed firing, because the starvation could not be reproduced synthetically.

### Not released: v4.6.0 and v4.6.1

v4.6.0 was a node-prologue cleanup in the search (dead variable, hoisted computations past the pruning, per-ply slices instead of `stackalloc`, deferred in-check test). Paired measurement: **0.0%** (+0.29% slower pooled, p = 0.22). The engine is memory-latency bound, so removing instructions does nothing while changing memory layout does - the opposite conclusion to v4.5.0's array flattening, from the same kind of measurement. It ships inside this release because it is harmless, not because it is worth anything.

v4.6.1 was the vote fix alone and lived two hours before v4.6.2 replaced it.

### Also in the training pipeline

- **`--loss-style reference`**: an antisymmetric win-rate target with its own offset and scaling per side and an exponent of 2.5, against the previous squared error on a single 400 cp scale.
- **`--start-lambda` / `--end-lambda`**: linear interpolation over the run.
- **`--prefetch`**: batches built on a background thread. Measured **1.06x**, not the 1.6x predicted, and reported as such.
- **`list_checkpoint_data.py`** and a rewritten `Noa-DataScale-Train.ps1`: a candidate now inherits its dataset list from the baseline **checkpoint** instead of globbing, and takes an explicit export arch. A run that was meant to test data volume differed from its baseline in three ways at once - epochs 60 to 22, export arch 1 to 2, and a silently dropped shard - and measured -108.6 Elo for reasons that had nothing to do with what it was testing.
- **A correction that closes an axis**: `--max-records` is inert in the streaming loader, so every net had already trained on the whole 324,297,032-position corpus. The data axis was never open.

### Measured

**Net: +195.4 +/- 57.5 Elo (SPRT, H1, 102 games) over the previous net.** 245 tests.

### Field gauntlet: 3271 +/-40 CCRL, +128 over v4.5.0

600 games at 60+0.6 against the same 12-engine field (2862-3281), single-threaded, ponder off, no tablebases for anyone, rating solved for R by `audit/gauntlet.py`:

| opponent | score | performance |
|---|---|---|
| 3281 Patricia 3.0 | 40.0% | 3211 |
| 3250 Defenchess 2.2 | 60.0% | 3320 |
| 3230 Princhess 0.19.0 | 56.0% | 3272 |
| 3150 Rubichess 1.5 | 76.0% | 3350 |
| 3120 Winter 1.0 | 67.0% | 3243 |
| 3010 Bit-Genie 9 | 83.0% | 3285 |
| 2978 Pedone 1.5 | 84.0% | 3266 |
| 2917 Tcheran 6.0 | 90.0% | 3299 |
| 2910 Ethereal 8.62 | 92.0% | 3334 |
| 2905 Inanis 1.5.0 | 81.0% | 3157 |
| 2900 Marvin 3.5.0 | 90.0% | 3282 |
| 2862 Colossus 2025b | 89.0% | 3225 |
| **total** | **75.7%** | **3271 +/-40** |

Per-opponent performances span 3157 to 3350, so no single pairing carries the figure. Against v4.5.0's **3143 +/-31** this is **+128**, well outside what two measurements of this size leave uncertain, and the crossover has moved above the field: the only negative result is Patricia at the top, while Defenchess 3250 and Princhess 3230 both go 56% or better.

The self-SPRT said +195.4 and the field says +128. That gap is the usual direction and worth recording as such: a figure measured against a version of itself is not a CCRL figure, and this project has now put a number on the shortfall.

## 2026-08-08 (v4.5.0) - the same search, about 10% faster, with every step proved by byte-identical node counts

Three changes, none of which alters a single node the search visits. That is the whole point: every one of them was gated on the same test, `audit/bench_identical.py` reporting **byte-identical node counts position by position** over 150 positions at depth 12, plus a control comparing the bench against itself. 34,690,140 nodes, identical in every comparison. Anything else and the change is reverted, whatever the unit tests say - the discipline that stopped a full SEE implementation in v4.4.0 after it passed ~70k equivalence comparisons and still moved node counts 1.8%.

### The NNUE accumulator was eager

`NnueAccumulatorStack.PushMove` copied both perspectives and did the feature math on **every** `MakeMove`, whether or not the position was ever evaluated. Profiling put it at **7.39%** of engine self time with another **2.49%** in the king-move refreshes.

v4.4.0 made that worse rather than better: quiescence now reaches the transposition table, so far more children return from a cached static evaluation or a score cutoff without ever calling the evaluator, and every one of those was an accumulator update nobody read.

A push now only **records** what the update would be and marks the level uncomputed; `GetPerspective` walks back to the nearest computed ancestor and materialises forward, on demand. No king squares are stored: along any chainable sequence that perspective's king cannot have moved, because a king move invalidates its own perspective and forces a refresh instead of a chain, so the current board supplies the right value for every link.

**The first cut of this was slower than what it replaced, and looked perfect.** Collapsing the pending chain straight onto the top level is one copy cheaper per evaluation and leaves the intermediate levels uncomputed, so a parent that never evaluates has its update replayed by *every one of its children* - and plies in check skip the static evaluation, which makes that the common case. Counters: 1,087,813 replays against the eager version's 1,029,978 updates, i.e. **5.6% more feature work than before**, with node counts already byte-identical and all tests green. Materialising each level as the chain is crossed brings it to 377,767 replays, **36.7% of eager**. Node identity cannot catch a performance regression; the `nnueprofile` counters added for this can.

Measured: **+3.6% pooled** (253/290 positions faster, sign test p ~ 0).

### Move generation

The jumper helper took a `Func<int, ulong>` and paid an indirect, non-inlinable call per piece to read `KnightAttacks[square]` - while the slider helper next to it already carried a comment explaining why it avoids exactly that. It takes the table as an array now.

`HasLegalMove` tested moves in generation order (pawns first, king last) when every search caller reaches it **in check**, where a pawn move is legal only if it happens to block or capture the checker and a king step usually is. It scans backwards now; the result is a bool, so the order is not observable.

`GeneratePawnMoves` called `Attacks.Pawn` twice per pawn whenever an en passant square was set, re-walking a jagged array by colour each time, and re-evaluated four loop-invariant en passant conditions once per pawn. All hoisted.

Measured: **+0.6%**, and reported with a caveat. The pooled figure was +1.09% but its sign test split 148/287, which is the pattern that means a few positions moved a lot rather than a broad speedup - one baseline pass had been the slowest of the four. The trustworthy pair, both runs in the same speed regime, says **+0.57% with 84/141 positions faster, p = 0.028**. Small, real, and not worth continuing down that path.

### Multi-dimensional arrays, which turned out to be the real find

.NET cannot give `T[,]` or `T[,,]` the single-dimension fast path: every access computes its address through a helper, bounds-checks each rank separately, and the JIT will not hoist those out of an inner loop. The engine had them in its hottest reads.

Flattened to `T[]` with an explicit index helper: `HistoryTable[2,64,64]` (read for every quiet the picker scores), `KillerTable[ply,2]` (likewise), `Board._pieces[2,6]` (move generation, attack detection, ordering context), `Zobrist.PieceKeys[2,6,64]` (two to four times per make/unmake), `_counterMoves[12,64]`, `LmrReductions[64,64]` and `CorrectionHistory[2,N]`. `MoveList` now exposes its moves as a raw array the way it already did with the parallel scores, so the sort loops stop reaching half their data through an indexer. `IsSquareAttacked` computes the side offset once instead of five times.

Flattening the Zobrist table is safe only because the initialisation loop keeps its nesting order, so every key keeps its exact value. Change one and every hash in the engine changes with it, moving transposition hits, repetition detection and therefore node counts. The node-identity check is what proves it did not happen.

Measured: **+6.12% pooled**, 95% [+5.34%, +6.20%], **397/439 positions faster, sign test p ~ 0**, with all three pairs agreeing (+4.39%, +8.52%, +5.51%).

**The mistake worth recording**: the profile said `MovePicker.ScoreAndSortQuiets` was 5.84% and half a session went into designing a better sorting algorithm. The cost was not the algorithm, it was an `int[2,64,64]` in the inner loop. When a profile points at a function, check the data layout it touches before redesigning what it does.

### Measured

**About +10.6% nps over v4.4.0**, chaining the three paired measurements. Node counts byte-identical throughout. 316 tests.

Field gauntlet against the 12-engine CCRL field at 60+0.6, single-threaded, ponder off, no tablebases for anyone: **62.4% over 580 games, performance 3143 +/-31** against a field averaging 3042. Per-opponent performances run 3062 to 3210, so no single pairing is carrying the number.

Against v4.4.0's **3124 +/-30** that is **+19, and not significant**: two measurements at +/-30 each leave the difference uncertain to about +/-43. It is the right sign and the right size - +10.6% nps is worth roughly +7 by this project's own calibration - but what the gauntlet establishes is the absence of a regression, not a gain.

### The performance figures are now computed one way, and the old ones were not

Both numbers above come from `audit/gauntlet.py`, which solves for the rating R whose expected score against each opponent sums to the score actually made. The figures published before this release used "average opponent + Elo(score)", a shortcut that biases whenever the field is spread out and the results are lopsided against its ends - which is exactly this field, running 2862 to 3281.

The whole series, recomputed the same way so it can be read as a series:

| version | games | score | field | performance |
|---------|-------|-------|-------|-------------|
| v4.3.1 + gen7 | 165 | 59.7% | 3039 | **3116 +/-57** |
| v4.4.0 + gen9 | 600 | 60.1% | 3043 | **3124 +/-30** |
| v4.5.0 + gen9 | 580 | 62.4% | 3042 | **3143 +/-31** |

The previously recorded ~3110 and ~3114 are the same games under the old formula; they are not wrong so much as computed differently, and mixing the two methods in one comparison is what this table exists to prevent.

### Measurement method, which is where most of the day actually went

Comparing bench **totals** measures the machine, not the change: two passes of the same binary differed by **4.2%** while the effect under test was 1.3%. `audit/bench_time.py` pairs each position against itself across runs, which cancels the common-mode drift, and reports a geometric mean with a sign test. Validated against a known answer - the accumulator work, independently measured at +3.1% on totals, came out at +3.61% pooled.

The rule that fell out of it: **if the geometric mean says "faster" but the sign test splits 50/50, do not believe the mean.**

### Time management: the hypothesis was refuted by measurement

Two Arena games at 3+2 suggested the engine was burning its clock too early - 201 of its first 230 seconds in 25 moves, then 63 moves on the increment. `clock_curve.py` binned 40 games by move number and reconstructed the remaining clock.

**Front-loading is what every engine does at this control.** By move 25 the engine has spent 78% of everything received; the field spends **73%**. Ratios run 1.10 and 1.02 over the first twenty moves and 0.87-0.92 after move fifty; depth holds at 20-21 with spikes to 26-33 in long games; no game in 40 was lost on time.

The one real defect is small: the clock flatlines at about 25 s from move 41 onward and is never spent, while the field burns down from 37 s to 12-15 s, so long games end with the engine richer than its opponent. The field spends 2.13-2.23 s where this engine spends 1.91-1.94 s, a ~10% late-phase deficit. Sized honestly, that is worth 1 to 2 Elo, and the time manager is not where the remaining strength is.

### Not chased, because it is not real

`Thread.PollGC` at 3.90% and `Buffer.ZeroMemoryInternal` at 0.99% together outweigh all of move generation in the profile. Both are an artifact of the harness: `ucinewgame` calls `Reset()`, which clears the 64 MB transposition table and every history table, and both the profiler and the node bench send `ucinewgame` **before every position**. A real game does that once per game, not once per move.

### On the SPRT

The accumulator change was put through an SPRT anyway, on request. After 2,091 games: 533-501-1057, **+5.2 Elo, 95% [-5.1, +15.4]**, LLR +0.056 against bounds [0, 10] - **2% of the way to H1**. At that rate it needs on the order of 100,000 games to conclude. Stopped as unresolvable, which is itself the useful measurement: 2,000 games move the needle 2% at this level, so node-identical speed work has to be validated by the bench, not by games.

### Move ordering: killers and the counter move stop being untouchable

Killers sat at a fixed 3,000,000 and the counter move at 2,900,000, both returned from `MovePicker.Score` **before history was even read**, with every history-scored quiet clamped just underneath at 2,899,990. Up to three moves per node were therefore ordered by a constant, and no amount of learned evidence about *this* position could overtake a refutation inherited from a sibling node.

That is a ceiling on every history experiment the engine has ever run: multi-level continuation history was measured four times (-33.9, -10.9, ~0, -4.2) and a butterfly-history LMR term three times, all rejected, because making history smarter could only ever reorder what sat **below** the reserved slots.

The bands did win the final v2.8.2 SPRT against removing them - but that measurement is not evidence for keeping them today. At v2.8.2 the butterfly table was numerically broken (2^20 rail, gravity term integer-truncating to zero, median -8 against a mean of +71.8, only 25% of entries positive). Ordering by a table in that state would lose to almost any fixed prior. The rails were rebuilt afterwards, so the comparison was worth making again on a table that works.

They are now bonuses (4096 / 3072 / 2048) inside the same additive score as butterfly history, continuation history, the safe-check bonus and the threat-escape term, and they are not mutually exclusive: a move that is both the killer at this ply and the refutation of the opponent's last move carries two independent pieces of evidence. **Measured +8.0 ±14 over 1125 games** against the previous ordering.

The bonus magnitudes are **not** bench-derived, deliberately. A paired 150-position node bench put 0, 4096, 8192 and 16384 all within ±2% geometric mean of each other, every 95% band crossing zero (sign test p = 0.93, 0.93, 0.46, 0.16), single positions swinging between x0.29 and x3.86. Node counts can say none of them wrecks the ordering; they cannot rank them.

Removing the bands made killers and the counter move pay the per-move check detection they used to skip, costing **3.8% nps**. Direct-check detection now precomputes its masks once per node: knight, pawn and king attack relations are symmetric and occupancy-independent, so those resolve with one bitboard test; sliders keep the exact per-move computation behind an empty-board ray filter that rejects destinations not even aligned with the king. Behaviour-preserving, verified by byte-identical node counts.

### Quiescence search was not using the transposition table at all

Not a probe, not a store. Every quiescence node paid a full network evaluation for its stand-pat even when the position had already been evaluated elsewhere in the tree - while the main search has cached its static evaluation in the table since 5F, *"revisits pay one cluster read, not a full evaluation"*.

Profiling put **28.7%** of engine time inside `NnueInference.EvaluateInt16` and **31.6%** inside quiescence. This is where the two overlap. It now:

- reuses a cached static evaluation, and caches what it computes on a miss (**+4.4% nps**);
- takes an early cutoff when a stored bound covers the window, off the PV only;
- uses a stored score as a better stand-pat floor than the static evaluation when its bound points the right way;
- saves its own result on the way out, always as a bound and never exact - quiescence searches a truncated move list, so its score is a bound on the true value, never the value.

The reference distinguishes quiescence entries from evaluation-only ones with negative depths (`DEPTH_QS = 0`, `DEPTH_UNSEARCHED = -2`). `TTEntry.Depth` here is a byte and cannot hold a negative, so the distinction is carried by `BoundType.None`, which is the same test by another route.

One trap this opened: the shallow ProbCut accepted entries at `entry.Depth >= depth - 4`, and the moment quiescence started writing depth-zero entries **with** a real bound, `0 >= 0` let a truncated captures-only score stand in for a ProbCut verification at depth 4. Guarded with `entry.Depth >= 1`. The main cutoff, the singular-extension gate and the LMR adjuster were audited and are protected by their own conditions.

### Measured

150 positions from real games at fixed depth, both engines run simultaneously under identical machine load: **6.8% fewer nodes, 4.4% higher nps, 10.7% less time to reach the same depth**.

Field gauntlet against the 12-engine CCRL field (2862-3281) at 60+0.6, single-threaded, ponder off, no tablebases for anyone: **600 games, 60.1%, performance ~3114**. Per-opponent performances are tight (3052-3215), so the number is not being dragged by one pairing. Against the ~3110 measured for v4.3.1+gen7 this is **statistically indistinguishable** - a 600-game gauntlet resolves about ±20 and the change is worth roughly +7, so this reads as "no regression, position confirmed", not as a measured gain. 309 tests.

### Measured and rejected - not in this release

- **A second continuation-history distance** (independent table keyed on the move two plies back, plus butterfly history at double weight). Cost 0.9% nps for nothing measurable; A+B together scored -2.1 ±17.5 over 659 games against A alone at +8.0.
- **The reference's time-management constants** (falling-eval clamps 0.576/1.728, linear stability ramp, instability base 1.077). Neutral at 20+0.2 (-0.5 ±16.4 over 772 games), and a direct probe showed it makes the *pondered* case worse: the ramp's floor is 0.700 against the current 0.815, and pondering leaves the search well past the ramp's ceiling.
- **A threshold-only static exchange evaluator** (the reference's `see_ge` with early exits). Passed an exhaustive equivalence test against the exact evaluator - 160 positions, every pseudo-legal move, eleven thresholds, ~70k comparisons - and **still** changed the search's node count by 1.8%. Worth about 1% nps, so not worth shipping a behaviour change nobody could explain.
- **Network width 512** (-76 at 10+0.1, -93 at 60+0.6) and **8 output buckets** (-15.2, H0 at 435 games), both trained identically to gen9. The NNUE capacity axis is closed in both directions.

### Time management: investigated at length, not broken

The bot leaves large amounts of clock unused - one 300+2 game finished with 253 of 300 seconds. The engine itself is fine: measured over the 168-game gauntlet it spends **1.32 s/move against the field's 1.31 s/move, ratio 1.01**, per-opponent range 0.93-1.08.

The difference is **ponder**, which is off in every gauntlet and SPRT this project runs and on in both bots. Pondering hands the search free iterations, so by the time `ponderhit` arrives the best move has often been stable for 10+ iterations; `timeReduction` is a binary cliff (`lastBestMoveDepth + 9 < depth`), and crossing it halves the budget from 6310 ms to 2994 ms - on a coin flip, 3 of 6 probe runs. Without ponder it never fires (3/3 at the full budget).

Ponder itself was measured for the first time in this project: **+150.3 ±79.1, H1 in 27 games** with ponder against the same engine without it. It is not broken, and the banked clock is ponder working. The cliff remains as a known cost, filed for the ordering campaign's third step.

## 2026-08-05 (v4.3.1) - the engine was playing one move and reporting another, on 2% of all moves

### The audit that found it

v4.3.0.4 closed one PV/bestmove mismatch by reading a single annotated game. This time the same check ran over **every game the bot has played since 2026-08-04**: 221 games, 9,570 annotated moves, comparing the first move of the PV variation that lichess-bot writes into `game_records/` against the move actually sent.

| Version | Annotated moves | PV does not start with the move played |
|---------|-----------------|----------------------------------------|
| pre-fix (v4.3.0.3) | 5,268 | 138 (2.62%) |
| v4.3.0.4 (gen7) | 1,832 | 35 (1.91%) |
| v4.3.0.4 (gen8) | 2,470 | 53 (2.15%) |

The rate barely moved across the v4.3.0.4 fix, so the root-move bound was never the whole story. And the mismatches land on exactly the moves worth auditing:

```
mv60  played Qxf7+   PV said a4     eval -9.50  d20
mv47  played Qg1+    PV said Qd3+   eval -44.33 d18
```

An independent static material scan of the same games had already flagged `mv60 Qxf7+` as losing a queen with `a4` as the best alternative. **The engine's own principal variation named the right move and a different one was sent.**

### The cause: a stop path that updates the answer without announcing it

Only completed iterations report progress. The stop handling in `FindBestMove` replaces `best` with the interrupted iteration's result and breaks out without ever calling `progress.Report`, and the static fallback that guarantees a legal move does the same. The caller is then holding the PV, the depth and the evaluation of a **different move**: the last thing announced belongs to the previous depth.

That is what put a variation starting with `a4` next to a played `Qxf7+`, and an evaluation of -9.50 that describes the discarded line rather than the move on the board.

**The move selection itself is correct and was left alone.** In an interrupted iteration a move only takes the lead after beating alpha in a full-window re-search, and if the stop lands between the scout and that re-search the loop breaks before the unvalidated score can be accepted. The correlation between mismatches and blunders is a shared cause - time pressure produces both interrupted iterations and hard positions - not a causal link.

So this is a **reporting fix worth no Elo**, and it is worth shipping anyway: reading the annotated PGN is how the last two real bugs were caught, and the channel was lying on 2% of moves, concentrated on the critical ones.

### Three correctness fixes alongside it

**The root filed fail-low scores as exact.** `SearchRoot` chose between `LowerBound` and `Exact` with a two-way test, so an iteration where every root move fell at or below the aspiration window was stored as an exact score the search had never proved. An aspiration fail-low is an upper bound and nothing else. The inner nodes already did the three-way test against their original alpha; the root now does the same.

**ProbCut keyed continuation history off the wrong piece.** It read the piece type from the destination square *after* the move was made, where the root and the main move loop both read the moving piece from the origin *before*. The two agree for every move except a promotion, and ProbCut only admits queen promotions - so a queen promotion filed its observation under Queen while every other path reads that same move under Pawn. This file already warns about exactly this failure mode at `ContinuationCorrectionKey`: filing under one key and reading under another is silent and permanent.

**Tablebase verdicts were announced as about 988 pawns.** The tablebase band sits just below the mate range on purpose, so the engine never claims a mate it has not proven - but that also puts it under the mate test in `FormatUciScore`, and the raw number went out as `cp 98872` to the eval bar and to everything reading the score, including the bot's resign and draw-offer rules. Reported as the conventional saturated value now, keeping the ply ordering so a win found sooner still scores higher.

### Scope

Search behaviour changes only through the two correctness fixes (what the root stores, and one history key), so node counts shift slightly; neither is a tuned change and neither has its own SPRT. Callers that pass no progress sink - datagen, tests, fixed-depth analysis - are byte-identical. 308 tests.

### CCRL field gauntlet: ~3110 ±45

**v4.3.1 + gen7 scored 59.7% over 165 games** at 60+0.6, `Threads=1`, ponder off, against the 12-engine field averaging 3043, for a performance of **~3110 ±45**. Per opponent:

| Opponent | NoaChess score |
|----------|----------------|
| Colossus 2862 | 82.1% |
| Ethereal 2910 | 82.1% |
| Marvin 2900 | 71.4% |
| Inanis 2905 | 67.9% |
| Pedone 2978 | 67.9% |
| Tcheran 2917 | 60.7% |
| Bit-Genie 3010 | 60.7% |
| Winter 3120 | 60.7% |
| Rubichess 3150 | 50.0% |
| Defenchess 3250 | 38.5% |
| Patricia 3281 | 37.5% |
| Princhess 3230 | 32.1% |

The crossover sits around **3150**. Applying one formula to all three runs the project has: gen5 3050, gen7 3098, this one 3111. **The +13 is well inside ±45, so this does not measure the correction histories or the 4.3.x fixes** - it establishes a band, roughly 3070-3155, and nothing sharper. Two parameters differ from the earlier gauntlets (`plies=16` on the book, and **no tablebases for any engine** - uniform and therefore fairer than a run where only NoaChess has them), so this is better read as the start of a cleaner series than as a continuation of the old one. Stopped at 165 of a planned 600 games.

On method, since it came up: **ponder has been off in every gauntlet and SPRT this project has run.** cutechess-cli only pondering when the bare `ponder` flag appears on an `-engine` line, and none of these bats pass it; `option.Ponder=false` would be a UCI setoption to the engine, which is not the mechanism and does nothing here. "Reasonable concurrency" means not running more simultaneous games than the machine can give real cores to - each game is two single-threaded processes, so `-concurrency 4` keeps 8 cores busy on a 24-core host, which is comfortable. Dropping to `-concurrency 1` removes contention that is not measurable at that load and costs four times the wall clock.

### gen8: trained, measured, rejected

The data-scale campaign's first net (331M positions at 6000-node labels, ft=128, one output bucket, 6 epochs) did not ship, on three independent measurements agreeing with each other:

1. **SPRT vs gen7** at 60+1, `Threads=1`, ponder off: stopped at **H0** after 198 games (59W 95D 41L, 53.8%). No evidence of the +50 Elo the bounds asked for.
2. **Real bot games**, identical binary, only the net swapped: the avoidable material-loss rate **tripled** (0.23 to 0.72 per 100 moves, p≈0.017), and the score fell from 80.5% to 75.8% against opposition only 58 Elo stronger.
3. **Gauntlet vs the 12-engine field**: started, then abandoned once the first two measurements agreed.

**The cause is the training schedule, not the data.** Validation loss never flattened - it fell 0.008005 to 0.005993 across the six epochs and the largest single drop was the *last* one - and `CosineAnnealingLR` is built with `T_max=args.epochs`, so the learning rate hit its floor exactly when the schedule ran out. Training stopped because the calendar ended, not because the model converged. The `--epochs 6` default dates from the 4-20M-position generations; at 331M positions it is roughly 2 billion samples seen, low for a corpus this size.

**Numbering rule, gen6 as precedent:** a generation number is consumed by the attempt, not the promotion. gen6 trained, drifted below 0.5, was never promoted, and the next net was still called gen7. Same here: gen8 keeps its number and its row in [NNUE_HISTORY.md](NNUE_HISTORY.md); the retrain - same corpus byte for byte, only the epoch count changes (60 against 6) - is **gen9**.

## 2026-08-04 (v4.3.0.4) - the ponder credit was starving the search, and the root could return a move its own search never endorsed

### The root move nothing had validated

Found in the game against Ichy-Fish (`ZkymFiQ5`, 180+2). On move 51, with a bishop attacked and 1:07 left on the clock, the bot played `g5` and lost the piece. The annotated PGN that lichess-bot writes to `game_records/` gave it away:

```
51... g5 { [%clk 0:01:07] }  ( 51... Be4 52. Bd1 Bg2 ... { [%eval 13.17,16] } )
```

**The reported PV starts with `Be4`; the move sent was `g5`.** Same mismatch on move 55, and on move 9 of the previous game against ElaChessBot.

### The cause

At the root only the first move is searched with the full window, so only its score is exact. The rest are scouted with a null window and their result is a **bound**, promoted to an exact score only by the re-search that fires when they beat alpha. On a fail-low iteration that re-search never happens - and the comparison

```csharp
if (score > bestScore)
```

still let one of those unverified bounds take the lead. The search then returned a move nothing had validated, while the PV - published only on completed iterations - kept showing the real best move.

The reference engine assigns exactly these moves `-VALUE_INFINITE` so they cannot compete. Same contract now:

```csharp
if (score > bestScore && (searched == 1 || score > alpha))
```

### Why it never reproduced on the bench

Over 40 searches of that position - every clock budget from 100 ms to 120 s, 1 and 4 threads, with and without AVX2 - the engine played the bishop retreat every single time. The bug needs a fail-low iteration interrupted at the right moment; a clean bench search never shows it. **The annotated PGN was the only evidence, and it was free.**

### Measured on the side: the NNUE is not SIMD-invariant

While chasing this, the same position at the same depth gave different scores and sometimes different moves depending on the vector path (`AVX2` / `no AVX2` / no intrinsics): -776 / -792 / -799 cp on move 51, -1174 / -1135 / -1217 on move 52. A build should not change its mind because of the vector width, so this is a real defect and a parity test across the three paths is pending.

It is **not**, however, affecting the bot. The first reading of it was wrong: the macOS host was assumed to be Apple Silicon running the x64 build under Rosetta 2 (no AVX2, hence the scalar path), and that assumption was never checked. It is an **Intel** Mac - the arm64 build refuses to start on it with `bad CPU type in executable` - so it takes the same AVX2 path as Windows. Verified rather than argued: `go depth 20` from the start position at `Threads=1` returns **identical node counts, scores and PV on both machines** (12,066,208 nodes, cp 46). The two hosts are the same engine bit for bit; games played on either are equally good evidence. The parity defect matters for a future non-AVX2 target, not for anything deployed today.

> **RESUELTO el 2026-08-12, y no a proposito.** Re-medido en v4.7.0 con **las
> mismas dos posiciones, la misma profundidad 18 y las tres rutas**: puntuacion
> identica (-821 y -1174), jugada identica (`c2e4`) y **recuento de nodos
> identico - 418.316 y 463.848 exactos**. Ese ultimo dato es la prueba y no un
> detalle: si la evaluacion difiriera en un solo nodo del arbol, las busquedas
> divergirian y los nodos se separarian. Confirmado ademas sobre 28 posiciones
> mas, 22 de ellas de una partida real del bot, a profundidad 10-11: 28 de 28
> identicas. El defecto era real aqui y desaparecio por el camino, con la
> reescritura del acumulador perezoso de v4.5.0 como candidato mas probable.
> Nadie lo cerro porque nadie lo estaba arreglando. Arnes: `scratchpad/simd3.py`.

### The ponder audit

Found by auditing all **150 games** the bot played between 2026-08-03 19:14 and 2026-08-04 13:32 (100 wins, 13 draws, 37 losses, 5 losses on time), scanning every pair of consecutive NoaChess evaluations for a collapse.

### 1. A long ponder made the next move a depth-1 move

The headline. Several blunders in the audit shared a signature that should be impossible: **depth 1 with a full clock**. `RZwdbv4z` move 23 `Qh3` at depth 1 with **41 seconds** still on the clock; `PciLwqDt` move 65 `c2` at depth 1 with 21 s; `8jWoV4Xx` move 57 `Qxa8` at depth 1 with 27 s.

The ponder credit in `UciLoop` clamped against the **hard** budget, leaving 100 ms of it. But iterative deepening is driven by the **soft** budget, so once the opponent had thought for longer than that budget the relaunched search began already past its deadline and broke immediately after depth 1. Reproduced exactly, at 60+1 with a soft budget near 2.5 s:

```
pondered   500 ms -> depth 16, 4646 ms searching
pondered  2000 ms -> depth 15, 3550 ms
pondered  5000 ms -> depth 11,    5 ms
pondered 10000 ms -> depth  1,    5 ms
```

**The longer the opponent thought, the shallower the reply** - and against slow bots that is most moves of the game.

The credit is now capped at half the soft budget. Charging the full pondered time is correct for a scheduler that *continues* the pondered search, as the reference does; this engine relaunches and inherits only the transposition table, so the depth has to be re-established and that needs time. After the fix the depth no longer degrades: 15, 16, 15, 18, 18 for ponders of 0.5, 2, 5, 10 and 30 seconds.

### 2. The DTZ root filter, unified at last

v4.3.0.3 chose between DTZ and WDL ranking on whether the side to move had pawns. That fixed K+N+pawns but left **K+Q vs K with no gradient at all**, which is why the bot took seconds per move in a four-piece won endgame and wandered.

The real distinction was never pawns. DTZ measures plies to the next irreversible move, and in K+Q+Q vs K letting the opponent capture a queen zeroes the counter in 1 ply, which beats mating in 3. Insisting on the single best DTZ made that sacrifice the *only* move kept.

So DTZ ranks again in every position, and the filter now keeps a **band** of ranks rather than the single best. Since rank is `MaxDtz - dtz`, the band is a slack in plies, and it shrinks as the fifty-move counter climbs: freedom while there is room, strict obedience when the draw is close. The mating moves survive alongside the "optimal" sacrifice, and because `_rootInTb` already switches off the flat in-search tablebase scores, the search sees the real mate distance and takes it.

### 3. A decided endgame no longer costs a full move budget

Every move surviving the root filter is optimal before the search starts, so the search is only breaking ties among winning moves. Measured at 60+1, K+Q vs K spent **3129 ms** on a four-piece position. The soft budget for a tablebase-resolved root is now capped at 300 ms (hard at four times that), and it applies to drawn roots too - a dead draw deserves the saving as much as a win.

```
                  before    after
K+Q vs K   60+1   3129 ms   528 ms
K+R vs K  180+2      -      477 ms
K+N+P vs K 60+1      -      467 ms
K+N+N vs K (draw) 1869 ms   503 ms
middlegame 60+1   1968 ms  1968 ms   unchanged
middlegame 180+2  2909 ms  2909 ms   unchanged
```

### All the endgame behaviours, together

| Position | result | time |
|---|---|---|
| K+Q+Q vs K | mate in 3 | 398 ms |
| K+Q+Q vs K+B | mate in 3 | 423 ms |
| K+N+pawn vs K | promotes to a queen | 289 ms |
| K+P vs K | promotes to a queen | 216 ms |
| K+B vs K | scores 0, a draw | 48 ms |
| stalemate trap | avoids it, mate in 6 | 97 ms |

308 tests.

### Still open

K+N+N vs K scores +5.58 instead of 0. The move is safe because the root filter keeps only drawing moves, but the number is wrong and the bot's draw-offer rule reads it. The cause is known: the in-search probe requires a zeroed halfmove counter, which in a pawnless endgame never happens again after the root. The fix is to report the tablebase verdict as the root score, which touches the search's return value and was not worth rushing.

### SPRT vs v4.3.0.4

`sprt_ponderhit_noreg.bat`, 60+1, ponder on for both sides, `elo0=-5 elo1=0` (a non-regression bound, not a strength claim - the ponderhit and root-move fixes are correctness fixes, not features up for a vote). Stopped by hand once the trend was unambiguous, not run to the SPRT's own bound:

**Elo difference: 55.5 ± 39.6, LOS: 99.7%, DrawRatio: 59.2%. LLR 0.736 (25.0% of the way to +2.94), rising monotonically with no dip.**

Mixes both fixes together - the ponder-credit cap and the root fail-low exclusion were not isolated separately. The direction confirms the diagnosis: v4.3.0.3 relaunched a starved search on every long ponder, and this pairing (both sides pondering, 60+1) is exactly where that bug bit hardest.

## 2026-08-03 (v4.3.0.3) - the endgame filter fix, fixed

v4.3.0.2 stopped the DTZ root filter from giving material away by ranking root moves with WDL below a halfmove-clock threshold. **That fixed one bug and created another**, and a real game found it the same evening.

Against zipfile_chess-bot ([lichess.org/HUAC6sVf](https://lichess.org/HUAC6sVf), 60+2), K+N+3P against a bare king:

```
123. Nc3 Kb2  124. Kc5 Ka1  125. Nb1 Ka2  126. Kd6 Ka1  127. Nc3 Kb2
128. Nd1+ Ka1 129. Ne3 Kb1  130. Nc2 Kc1  131. Nb4 Kb1  132. Kc6 Ka1
   ... twenty moves of knight and king going nowhere ...
143. f6! Kb1  144. f7 Ka2  145. f8=Q Kb1  146. Qf1+ Ka2  147. Qa1# 1-0
```

Twenty wasted moves, then the halfmove counter crossed the threshold, DTZ engaged, and the pawn walked in for mate in five. Everything needed to win was already there; only the reason to make progress was missing. WDL ranking keeps every move that preserves the win and says nothing about which one advances, so the search had no gradient at all.

### The distinction that was missing

DTZ steers towards the next irreversible move, and that is **exactly what a won endgame wants when there is a pawn to push**: the push zeroes the counter and is genuine progress towards promotion. DTZ is only harmful with no pawns of ours and nothing of theirs to capture, because then the only way to shorten it is to let the opponent take one of our pieces - the K+Q+Q vs K queen sacrifice of v4.3.0.2.

So the rule is now about pawns, not about the clock: **our own pawns present, DTZ always; no pawns, WDL until the fifty-move counter makes progress the thing that saves the win.**

All three behaviours are correct together for the first time:

| Position | before v4.3.0.2 | v4.3.0.2 | v4.3.0.3 |
|---|---|---|---|
| K+N+pawns vs K | promotes | **20 moves shuffling** | **mate in 5** |
| K+Q+Q vs K | **sacrifices the queen** | mate in 3 | mate in 3 |
| K+P vs K | queens | queens | queens |

308 tests. The game is cited in the source with its URL, so the next person to touch this filter can see what each version actually did.

## 2026-08-02 (v4.3.0.2, follow-up) - the time-manager question, settled by instrumentation

The working hypothesis all day was that the scheduler under-spends the clock. **It does not, and the way that was established matters more than the answer.**

`NOA_TM_DEBUG=1` makes the iterative-deepening loop print its target, every factor applied to it and what has actually been spent. Off by default, so a released build pays one static boolean test per iteration.

Traced across consecutive middlegame moves at 3+1:

```
jug 16: soft=5748 fe=0.500 red=0.940 inst=1.000 total= 2703 -> spent  4060 ms
jug 17: soft=5896 fe=1.500 red=0.940 inst=1.000 total= 8317 -> spent 12481 ms
jug 18: soft=5815 fe=0.500 red=1.982 inst=2.033 total=11630 -> spent 13052 ms
jug 21: soft=5497 fe=0.500 red=0.940 inst=1.000 total= 2585 -> spent  2591 ms
```

It spends 2591 to 13052 ms against an optimum near 5500, **averaging about 107% of target**, and the dynamic factors swing the budget fivefold between a quiet move and a dangerous one. That is the scheduler working, not failing.

The same trace shows the v4.3.0.2 SMP cap earning its place: on move 18, `total=11630` against `soft=5815` is exactly the new 2x bound. Under the old 1x bound that entire extension - on the one move where the eval was falling *and* the best move was flapping - would have been discarded.

### Where the earlier reading came from

Measuring moves 1-20 only. That range is the opening damp doing its job by design, and generalising from it to the whole game produced a confident, wrong conclusion. The source now records that so the next person does not repeat it.

### Where the clock actually goes unused, in order of size

The bot's two overhead settings, then the easy-move rule, then games simply ending before the clock does. `uci_options.MoveOverhead` is reserved x52 across the move horizon, so 600 instead of 30 costs a quarter of the bullet budget - measured optimum 3189 -> 2345 ms at 60+1 - and lichess-bot subtracts its own `move_overhead` from the clock it reports before the engine ever sees it. **Both are configuration. No engine change was warranted, and none was made.**

### Also

The tablebase probe is now disabled only for a **decisive** root. On a drawn one the flat score is the truth, and switching the probe off made the search report a fantasy evaluation for a dead position: +531 on `8/8/8/4k3/8/8/4KNN1/8 w`, K+N+N vs K, which cannot be forced. The move stayed safe because the root filter still ran, but that score feeds the UCI output and the draw-offer rule in the bot.

### An experiment that did not survive

Raising the falling-eval floor 0.5 -> 0.65. A 30% change moved the median spend by 5 ms, ruling the floor out as the binding constraint. Reverted rather than shipped unmeasured.

## 2026-08-02 (v4.3.0.2) - the tablebases and the clock were throwing play away

**MEASURED +9.8 Elo, 95% CI [-1.9, +21.6], LOS 95.0%** over 1094 games at 60+1 against v4.3.0 (193-162-739). Run at `Threads=4` on purpose: the fourth defect below is a no-op on a single thread, so a single-threaded SPRT would have measured nothing.

**Read that interval honestly.** It touches zero at the bottom, so this is not a proven gain and the SPRT was stopped before it concluded, at a point where resolving it would have cost another one to two days of machine time that the data-scale campaign needs more. What it does establish is the thing that actually needed ruling out: letting the SMP budget extend to twice the optimum **does not cost anything**, and the centre of the distribution is positive.

The 67.6% draw rate is itself informative: two versions that differ in very few positions. Of the four defects, three fire only in endgames that barely occur across a thousand bullet games; the fourth acts on every move and is what the number really measures.

Four defects, all of them found by watching the bot play on Lichess and all reproduced on the bench before a line was changed. Three of them made the engine play *worse than it would have with the feature switched off*, which is the class of bug that hides longest: nothing crashes, nothing is slow, the move is simply wrong.

### 1. Tablebase scores are flat, so the search could not find the fastest win (v4.3.0.1)

The in-search Syzygy probe returns `TbWin - ply` for **every** winning continuation. Promoting to a queen and promoting to a rook therefore scored identically, and so did delivering mate now versus circling for another ten moves. On `8/P7/8/8/8/8/8/K6k` the principal variation wandered back to the square it had started from.

Whenever the root itself is resolved by the tables the root move list is already restricted to game-theoretically optimal moves, so the win cannot be thrown away no matter what the search decides. `AlphaBetaSearch` now records that in `_rootInTb` and skips the probe inside the search entirely, leaving ordinary evaluation and mate distance to choose among the moves the tables call equal. `tbhits` over that search fall from 53 to 1, and the variation now walks the enemy king to the edge.

### 2. The DTZ root filter was giving material away

DTZ is the distance to the next **irreversible** move, not to mate. On a board with no pawns and nothing of the opponent's to capture, the only move that can shorten it is letting the opponent capture one of ours. In K+Q+Q vs K that makes hanging a queen the "DTZ-optimal" move, and restricting the root to DTZ-optimal moves *forced* the engine to play it.

Same binary, same position, only the tables switched:

```
FEN 8/8/8/5K2/8/2k5/8/4q1q1 b - - 6 81
tables off -> e1e7, score mate 3     both queens kept
tables on  -> e1e5, score cp 1283    the queen goes
```

The tables were making the engine worse. The filter now ranks by **WDL**, which still cannot throw the win away, and only falls back to DTZ once the halfmove clock reaches `DtzUrgencyClock` (50) and progress towards a zeroing move is what actually saves the game. That position is now mate in 3.

The two fixes are coupled: WDL ranking leaves several moves on the table, and the search can only choose sensibly among them because fix 1 stopped the flat scores.

### 3. MoveOverhead could switch the clock off entirely

`MoveOverhead` is reserved across the whole move horizon, so `MoveOverhead x 52` at the default. With 600 in a 30+0 game that is more than the clock exists: `30000 - 31200` is negative, the `Math.Max(1, ...)` clamped the usable time to **one millisecond**, and the engine played an entire ultrabullet game in about seven seconds. Measured over the bot's games that day: median 0.00 s per move, 97% of the clock unused.

The reservation is now capped at half the clock. Sane configurations come out bit for bit identical (`MoveOverhead 30` at 180+2: 276,440 ms before and after); only the pathological case changes, from 1 ms of usable time to 15,000. Reserving overhead must slow the engine down, never switch it off.

### 4. The Lazy SMP cap made the time manager one-sided

The multi-thread safety clamp bounded the modulated budget at the optimum itself. Since the bot never runs on one thread, that meant **every reduction applied in full while every extension was discarded**: a falling eval and a flapping best move, the two signals that mean "this position is dangerous, look harder", could not buy a single millisecond. The engine could only ever think less than its target, never more.

Measured across 49 games on 2026-08-02: it finished with **73% to 98% of its clock unused** depending on the time control, with **zero losses on time**. The cap now sits at twice the optimum (`SmpExtensionCap`), isolated in one constant so the value can be tuned by SPRT without touching anything else. The hard maximum and the mid-iteration guard remain the real brakes.

### Also here

- `SyzygyIntegrationTests.SearchRoot_DoesNotRegenerateMovesAfterTablebaseFiltering` no longer pins an exact move. Under WDL ranking the filter deliberately leaves the search a choice, so the test now asserts the property it actually cares about: that the position reached is still won.
- `EngineBenchmarks` stopped compiling when architecture 3 made `ArchitectureId` required and turned `OutBias` into an array. It had been taking the whole solution build down with it since v4.2.0.

### What this does not fix

The clock is still under-used outside the pathological case. The easy-move rule spends 12% of the budget once the score passes 700 cp with a stable best move, which in won games is nearly always, and that was itself introduced on the back of an SPRT. It stays until another SPRT says otherwise.

Two configuration values on the bot side also matter and are not engine changes: `uci_options.MoveOverhead` belongs at 30, not 600, and lichess-bot's own `move_overhead` subtracts a further second from the clock it reports to the engine.

## 2026-08-01 (v4.3.0) - BLOCK 12 search, part 1: the complete correction histories

**MEASURED: +25.7 ±16.4 Elo vs v4.2.0, LOS 99.9%, SPRT H1 accepted** (280-212-429 over 921 games at 10+0.1, LLR 2.97 against a 2.94 bound). Same gen7 net embedded on both sides, so the difference is the search and nothing else - and since v4.2.0 is strength-identical to v3.3.0, this is **the first real Elo of the v4.x campaign, measured against the ~3080 engine**.

Caveat worth stating with the number rather than after it: this is 10+0.1. Elo does not transfer 1:1 to the slow control CCRL uses, and this project has measured it going both ways - v2.6.9 gave +34.3 at STC and only +16 relative at LTC, while v2.7.0 gave +4.0 at STC and +43 at LTC. Evaluation gains shrink at long TC; search gains grow. Correction histories are search, so this may hold or grow, but the CCRL figure comes from the gauntlet, not from arithmetic on this one.

**First strength-affecting engine change of the v4.x campaign.** Everything since v4.0.0 has been infrastructure with the search provably untouched; this one changes it deliberately. Node count on the fixed-depth suite moves 107,484 → 109,940, deterministic across runs at `Threads=1`.

### What a correction history is for

A static evaluator's errors are not random - it misjudges particular structures the same way every time it meets them, and those structures recur across many branches of one search. The difference between what the evaluator said and what the search actually found is therefore worth remembering, and correcting the static evaluation before it feeds forward pruning and the improving flag removes that bias where it does the most damage.

Until now there was exactly one such table, keyed on pawn structure (v2.8.2). But pawn structure is not the only thing an evaluator can be systematically wrong about: a bias that follows the minor pieces recurs across positions whose pawns differ, and a bias in how *one side's* pieces are judged is invisible to a colour-blind key.

### Six tables instead of one

Added: **minor pieces** (knights + bishops), **major pieces** (rooks + queens), **non-pawn material per colour** (two tables, kings included), and a **continuation** table keyed by the move that reached the position - which describes *how* the position was arrived at rather than what stands on the board, a genuinely different axis, since the same position reached by a quiet regrouping and by a forcing capture tends to be misjudged differently.

Each needs its own incrementally-maintained Zobrist key, so `Board` now carries `MinorZobristKey`, `MajorZobristKey` and `NonPawnZobristKey(colour)` alongside the existing pawn key. Add and remove share one toggle path, because XOR is its own inverse and two copies of that logic is how an incremental hash silently drifts.

### The combination rule is deliberately additive

All six tables estimate the *same* quantity from different keys, so they are combined by weighted average, not summed - summing independent estimates of one quantity would over-correct exactly when they agree, which is when they are most trustworthy.

**The pawn weight equals the divisor.** When only the pawn table has learned anything, the correction is arithmetically identical to what v4.2.0 produced, and the five new tables can only add on top, bounded at ±320 cp.

This is not a detail. Folding six tables in by plain averaging would let an empty table pull the correction toward zero and quietly shrink a validated behaviour by a factor of six - and then a failed SPRT would be unattributable between "the new keys are useless" and "we damaged the one that worked". A test asserts the pawn-only case directly rather than trusting the arithmetic.

### Tests

308 total (232 engine, 76 core). The new ones target the failure modes that do not announce themselves:

- Partial Zobrist keys checked against a board rebuilt from FEN **at every ply** of random legal games (three seeds), not along a hand-written line. A fixed sequence only tests what its author thought of; random play reaches captures, en passant, promotions and castling on its own. The first version of this test used a hand-written line that turned out to contain an illegal move.
- Every partial key restored exactly by make/unmake, over every legal move of a complex position.
- Keys proven to *separate* what they should: a rook leaves the minor key at zero, a knight leaves the major key at zero, the same piece for the other colour does not collide, and pawns never enter the non-pawn keys.
- The continuation sentinel (0 = no previous move) proven unreachable as a real key.
- The combined correction proven bounded under 500 extreme consistent observations.

### The "coupled bundle" half of v4.3.0 was withdrawn, not deferred

The roadmap paired these tables with re-entering statScore, cutNode, double extensions and multi-level continuation history *as a bundle*, arguing each was worth only +2–5 Elo alone - below an 8,000-game SPRT's resolution - and that they were worth more together.

**That argument does not survive the evidence already in this repository, and the plan was withdrawn before any machine time was spent on it.** These were not sub-resolution results: statScore in LMR was tested in *three* variants against v2.8.3-class baselines (−18 Elo H0; −4.8 ±11.4 with LLR −2.89; +4.2 ±9.1 flat over 3000 games) and `AlphaBetaSearch.cs` records the conclusion *do not re-add without a new mechanism*. Double extensions: four SPRTs, worst −19.7/−12.5. Multi-level continuation history: four builds, −33.9 → −10.9 → [0.496] → −4.2, with the per-distance tables, gravity and depth gate all built. cutNode: cut at both magnitudes.

A supporting premise was wrong too - the plan assumed statScore had failed because the butterfly table was miscalibrated and that v2.8.3 fixed it afterwards. Those three measurements were taken *after* the gravity fix.

The coupling rule remains sound in general; it was applied here to features cut for being clearly negative, with root causes identified at the time. What survives as defensible is one targeted change, not four resurrections: 5G's root cause was that the hard killer/counter ordering bands sit above all history, so no history refinement below them can express itself. Addressing those bands is the "new mechanism" the code asks for. Still speculative, still needs its own SPRT.

`sprt_corrhist.bat` measures this version alone.

---

## 2026-08-01 - MEASURED: the network is data-starved, by +182 Elo

Not a release; the measurement the v4.1.0 pipeline was built to make. Phase 0 of the data-scale campaign, run on two arms matched by **total search work** rather than position count:

| arm | positions | nodes/move | node-work |
|---|---|---|---|
| `fast6k` | 20,001,946 | 6,000 | 1.2e11 |
| `deep28k` | 4,288,192 | 28,000 | 1.2e11 |

Identical architecture (ft=128, l1=32, unbucketed), identical hyperparameters, same teacher. The only difference is how the same machine time was spent.

**`fast6k` wins by +182.2 ±16.6 Elo, LOS 100%** (1167-307-312 over 1786 games at 10+0.1). **The campaign runs at 6,000 nodes.**

**The magnitude says more than the direction.** This is not "deep labels add little" - it is that **4.3M positions cannot train this network at all**. The feature transformer alone holds 22,528 × 128 ≈ 2.9M parameters, so the deep arm trained on roughly 1.5 positions per parameter.

It is the strongest confirmation yet of the BLOCK 12 diagnosis, and it explains gen3 through gen7 retroactively: those generations raised node counts 14k → 20k → 24k → 28k and landed flat, because label depth is not the binding constraint at this size. The axis that mattered was never being moved.

**It does not prove 6,000 is optimal**, only that it beats 28,000 decisively. Both arms sit deep in the starved regime, so the slope cannot be extrapolated into the 300-500M range where returns must diminish - extrapolating from two points is the class of error this project has already paid for. A third arm at 3,000 nodes / 40M positions (same total work, ~7h) would establish whether 6,000 is the plateau; that choice doubles or halves the campaign corpus for the same hours.

---

## 2026-08-01 (v4.2.0) - BLOCK 12 capacity: output buckets, width support, and a way to price width

**MEASURED 2026-08-01: output buckets are worth +20.1 ±14.0 Elo, LOS 99.8%, SPRT H1** (658-560-474 over 1692 games at 10+0.1, LLR 2.95). Two nets trained on the identical 84.7M-position corpus at identical width and hyperparameters, differing only in `--out-buckets` (1 against 8), played in the same engine binary. Buckets ship.

**This contradicted a prediction made before the run and recorded here for the record.** The expectation was ~0: the campaign had just measured the network to be starved of DATA (+182 Elo for volume over label depth), and buckets add CAPACITY, so the reasoning was that there was nothing for extra head capacity to feed on. That reasoning was wrong. Head capacity and input capacity are not the same constraint - the head had room to specialise even while the feature transformer did not have the data it wanted. Worth noting that the "data-starved" finding is still correct; what was wrong was treating it as a blanket argument against all capacity.

Note this does NOT add to the +25.7 measured for v4.3.0: different axis, and measured on a different pair of nets (neither of them gen7).

**Capability release. The embedded net is still gen7 and strength is unchanged - 193,746 nodes on the fixed-depth suite, the same figure as v4.0.0 and v4.1.0.** What ships is the architecture the capacity step needs, verified across both languages, plus the measurement that the width decision was missing.

### Output buckets (architecture 3)

The head is replicated per bucket and the bucket is chosen from the piece count, so the network gets a specialised readout per phase instead of one linear map serving a 32-piece opening and a 4-piece ending alike.

**It is almost free at runtime.** Only ONE bucket is evaluated per call - the others are never touched - so the arithmetic per evaluation is exactly the arch-2 cost. What grows is the weight table, and only for the head: at ft=128/l1=32 the L1 matrix goes from 16 KB to 128 KB against a 5.5 MB feature transformer. That ratio is why buckets land before any width increase.

Bucket selection is `clamp((pieceCount - 1) * buckets / 32, 0, buckets - 1)` - with 8 buckets, the familiar `(pieceCount - 1) / 4`. It lives in exactly one function per language, because a trainer and an engine that disagree here read a head the net was never trained for, and nothing fails loudly when that happens.

### Verified across the language boundary, not assumed

The engine and the trainer are two independent implementations of the same integer arithmetic joined only by a byte layout and that bucket formula. So both were checked directly:

- **Bucket formula**: the C# golden values are asserted in the test suite and the Python side was run against the same table - identical for every case, and in range across piece counts 0-40 × 1-16 buckets.
- **End-to-end values**: a bucketed net trained in Python, exported as arch 3, loaded by the engine and evaluated on three positions spanning three different buckets (7, 0, 5) gives **18 / 80 / 62** - and the new `verify_export.py`, which reproduces the engine's integer forward pass from the exported FILE, gives **18 / 80 / 62**. Exact agreement, not approximate.
- **Backward compatibility**: re-exporting gen7 as arch 1 still reproduces the shipped payload **byte-identically** (sha `3c7e94a9…`), and the engine still searches the same 193,746 nodes.

`verify_export.py` is new and is meant to be run on every future export.

### Pricing width without training anything

The v4.0.0 rule was that width must be decided on measured NPS, never on an attribution table. Measuring it normally means training a net at each candidate width first - days before the first number arrives.

It does not have to. **The cost of a width is a property of the shapes, not of the weights**: a randomly initialised net of the same dimensions executes the same instructions over the same memory. The new `nnuewidth` command synthesises shape-accurate nets and times them, so the cost curve takes seconds and only the widths that survive it are worth training. Preliminary (on a loaded machine, so indicative only):

| ft | eval | vs 128 | accumulator move | vs 128 |
|---|---|---|---|---|
| 128 | 898.6 ns | 1.00× | 28.6 ns | 1.00× |
| 256 | 1361.1 ns | **1.51×** | 44.6 ns | 1.56× |
| 512 | 2370.0 ns | **2.64×** | 83.8 ns | 2.93× |

Cost rises **sub-linearly** with width in this range - doubling the transformer costs about 1.5×, not 2×, because the fixed overheads (activation packing, the output layer) do not scale. That is a materially better trade than the "wider is counterproductive" assumption v3.2.0 was built on.

The first version of this sweep reported ft=256 as *faster* than ft=128, which is impossible; the estimator now takes the **minimum of five repetitions** (interference only ever makes a measurement slower, so the fastest observation is closest to the truth) and warms up before the first width. The report prints its own sanity check - if cost does not rise with width, the machine was busy and the table is noise.

### Buckets can be measured now, on data that already exists

Width needs the 300-500M corpus; buckets do not, because they add head capacity rather than input capacity, and 84.7M positions already fits eight small heads. `Noa-Buckets.ps1` trains two arms that differ **only** in `--out-buckets` (1 against 8) - same data, same width, same hyperparameters - and `sprt_buckets.bat` plays them off. Both arms are trained fresh rather than reusing gen7 as the control, because gen7 differs in hyperparameters and epoch count and would confound buckets with everything else.

### Also fixed

**A mistyped subcommand started a 500-game datagen.** `nnueprobe` instead of `--nnueprobe` fell through into the default run path and launched two full datagen runs on an already-loaded machine. Options always begin with `--`, so a bare first word that is not a known subcommand is now rejected with exit code 2. Deliberately narrow - it cannot affect any invocation that starts with a flag.

**`stackalloc` inside a loop in `ShardWriter.CountExistingRecords` (CA2014).** The buffer was allocated per shard and never released until the method returned, so the stack frame grew with the shard count. Harmless at five shards; a 300M-position corpus produces sixty-plus, and the pattern has no upper bound. Hoisted out of the loop.

### Campaign-script fixes (outside the repo, recorded here because they cost a run)

The v4.1.0 campaign script invoked the datagen through `dotnet run --project`, which **rebuilds on every invocation**. That killed the calibration's second arm after the first had spent seven hours succeeding: the rebuild wrote into `tools/NoaChess.DataGen/bin`, which the still-running elite-labelling process held locked, and the build failed with MSB3027. It also meant different shards could come from different builds - and a corpus assembled from more than one binary has a story for provenance rather than a fact. The datagen is now published **once** into a campaign-private directory and invoked as a frozen executable.

The calibration itself was redesigned. It gave both arms the same **position count**, which sounds fairer and asks the wrong question: at 28,000 nodes a position costs ~4.7× what it costs at 6,000, so the deep arm would have taken ~32 hours against the fast arm's 7. The campaign never gets to choose "20M positions at any depth" - it gets a compute budget. Both arms are now matched on **total search work** (`20,000,000 × 6,000 ≈ 4,285,714 × 28,000`), which costs the same hours and answers the question that actually decides the campaign: with the same machine time, are many cheap labels worth more than few expensive ones?

228 engine tests, 71 core tests.

---

## 2026-07-31 (v4.1.0) - BLOCK 12 data scale: the pipeline that makes 300-500M positions possible

**Infrastructure release. The embedded net is still gen7 and strength is unchanged from v4.0.0.** The Elo this version targets (+80 to +150) comes from *running* the campaign, which is 2-3 days of datagen; what ships here is everything needed to run it safely, plus a cheap experiment that tests the campaign's core assumption before those days are spent.

### The wall that had to come down first

Decoding records into features ran at **13,816 records/s** in a per-record Python loop. For the corpus BLOCK 12 targets that is **6 hours for 300M positions and 10 for 500M** - before training could even begin, and again on every change to the data mix. That is not slow, it is prohibitive.

`decode_block` does the same bit twiddling with numpy over whole blocks: **169,366 records/s, a 12× speedup - 300M positions in 29 minutes instead of 6 hours.** The per-record `record_to_features` stays as the readable definition of correctness, and the vectorised path is asserted equal to it over 50,000 real records (0 mismatches), because a decoder that is fast and subtly wrong would poison every net trained afterwards.

### Sharded, crash-safe, resumable datagen

A NOADATA file only becomes usable when its header is patched at the end of the run. Fine for 13 hours; unacceptable for 2-3 days, where a crash at hour 40 destroys everything - the pipeline even documents this ("the datagen did not reach 'done:' → the file is useless").

`--shard-size N` closes each shard properly as it fills: header patched, manifest written, SHA recorded. An interrupted run now loses at most the shard in flight, and every completed shard is immediately trainable (the streaming loader already takes many files). `--resume` counts finished shards and continues numbering after them, so a long campaign can be run in sessions instead of one uninterruptible block. Shards roll **between games**, never inside one, because records are ordered by game and the train/validation tail cut depends on that.

Also `--positions N`: corpus size is what is actually being specified, and `--games` only approximates it - game length varies with node budget and opening source. **`--positions` counts what is already on disk**, so resuming a 20M target that stopped at 12M tops it up to 20M rather than producing 32M; a run already at its target generates nothing and says so. Verified by killing a datagen mid-run: completed shards survive, the orphaned shard is flagged by `corpus` as INTERRUPTED, and `--resume` recovers to a clean corpus.

### `corpus` - audit what you are about to train on

```
NoaChess.DataGen corpus --in data\datascale
```

Reports composition by source (mode, opening provenance, node budget, evaluator, WDL signal), verifies every shard off disk (header count against derived count, schema, record size, manifest presence), and samples the label distribution. Run against the existing corpus it prints, at a glance, the fact that took five generations to discover:

```
84,697,234 positions across 7 datasets - every one "openings=8-9 random legal"
```

It also flags a shard whose header says zero records as INTERRUPTED, and warns when over 20% of scores are exactly zero - the signature of the label bug that zeroed 57% of gen1-era labels. It opens files with `FileShare.ReadWrite` so a corpus can be audited *while* a datagen is still writing it, which is the main reason to want the tool.

### The campaign, and the experiment that gates it

`Noa-DataScale.ps1` runs it in phases: **0 calibrate → 1 generate → 2 audit → 3 train → 4 publish**.

Phase 0 is the important one. The whole premise of v4.1.0 is that at ft=128 **volume beats label depth**, which is why the node budget drops from gen7's 28,000 to ~6,000. That premise is an assumption, and this project has already paid for one of those. So phase 0 builds two matched 20M-position corpora - one at 6k nodes, one at 28k - trains both at identical width and plays them off, in about four hours. Parity already favours the cheap arm, since it costs ~5× less machine time per position; a clear loss says build the corpus deeper.

Phase 1 mixes three sources deliberately (45% bulk self-play, 20% human opening seeds, 35% human middlegame seeds) and passes **`--require-book`** on every book-seeded arm, so the block-8 failure cannot repeat silently. Phase 3 trains at **ft=128, width unchanged**: keeping width fixed is what isolates the data axis, and changing both at once is precisely the mistake that made block 8 uninterpretable.

`sprt_datascale.bat` measures the result against v4.0.0; `sprt_datascale_calib.bat` runs the phase-0 arms against each other.

293 tests pass. Verified end to end on a real corpus: sharded datagen → resume → audit → streaming training over 8 shards → export → engine loads and plays.

---

## 2026-07-31 (v4.0.0) - BLOCK 12 foundation: NNUE cost profile, int8 L1, accumulator cache, streaming dataset

**No Elo claim, and the shipped net is unchanged.** This release exists to remove three ceilings that make the rest of BLOCK 12 possible, and to replace an assumption with a measurement. Strength is identical to v3.3.0: the embedded net is still the arch-1 gen7 net, byte-identical, and node counts on a fixed-depth suite are unchanged (193,746 exactly, before and after).

### The measurement that motivated everything (`nnueprofile`)

A new non-UCI command reports where NNUE time actually goes: isolated per-primitive costs multiplied by real call counts from an instrumented fixed-depth search. First run, gen7 at ft=128/l1=32:

| | share |
|---|---|
| L1 dot product | **26.2%** |
| Feature-transformer row traffic | **73.8%** |

`NnueInference.cs` had asserted for two versions that the L1 dot product was *"THE cost of NNUE eval"*. It is roughly a quarter. That assertion is what justified keeping the network narrow ("a wider net is counterproductive at real TC", v3.2.0), so **the decision not to widen rested on a cost model that does not survive arithmetic**: at ft=128 the dot product is 32 × 256 = 8,192 int16 MACs, about 512 AVX2 instructions per evaluation - far too small to dominate anything at 446k NPS.

Those are the numbers from the FIRST run, before the accumulator work below. The shipped build measures **43.4% / 56.6%**, because making the feature-transformer primitives cheaper naturally raised the dot product's share. Both readings are correct at their own point in time; anyone running `nnueprofile` today should expect the second pair.

### Accumulator updates: profile-driven fix, and an honest negative result

The profile showed `MoveFeature` at 387 ns for what is 8 vector additions. Cause: `new Vector<short>(array, index)` bounds-checks every iteration and the JIT does not reliably hoist it. Rewritten with `Vector256.LoadUnsafe` over a ref taken once, the same idiom the inference kernel already used:

| primitive | before | after |
|---|---|---|
| `MoveFeature` (fused) | 387.2 ns | **154.9 ns** (2.5×) |
| `Add`/`SubtractFeature` | 270.9 ns | **145.0 ns** (1.9×) |
| `CopyFrom` | 138.2 ns | **62.2 ns** (2.2×) |
| attributed NNUE total | 427.8 ms | **255.2 ms** (−40%) |

**End-to-end wall time did not move** - 730-767 ms across six alternating runs of both builds, fully overlapping, node counts byte-identical. The reason is that feature-transformer rows come from a 5.5 MB table indexed by feature, i.e. near-random access that misses L2: the bottleneck is **memory latency, not instruction count**, and removing bounds checks does not make DRAM faster. Reported as it measured. The profiler now prints this caveat itself, so the attribution table is never read as a promise of what an optimisation will return.

### int8 L1 (architecture 2)

L1 weights move from int16 to int8, with activations packed to unsigned bytes and the dot product running on `VPMADDUBSW` + `VPMADDWD` - the AVX2 path, because the target CPU is Zen+ and `VPDPBUSD` is not available. Measured on gen7 re-exported: **evaluation 1039.9 → 774.7 ns (−25%)**, NPS 243.7k → 268.7k.

Moving the weights costs nothing: export already clipped them to ±127 while storing them as int16. The one real change is that **QA must drop from 255 to 127**, and that is a correctness constraint rather than tuning. `VPMADDUBSW` sums two products into an int16 lane, which saturates:

```
QA=255 -> |255*127 + 255*127| = 64,770  > 32,767  -> saturates, WRONG
QA=127 -> |127*127 + 127*127| = 32,258  < 32,767  -> exact, always
```

The loader refuses any arch-2 model with QA > 127, and the exporter re-checks the bound against the actual exported weights (gen7 uses 65.9% of the headroom). Arch 1 remains fully supported - a format change that stranded the net currently playing would be a regression, not an upgrade. Verified: re-exporting gen7 as arch 1 reproduces the shipped payload **byte-identically** (sha `3c7e94a9…`).

**The embedded net stays arch 1 for this release.** Dropping QA to 127 changes evaluation values enough to change search: the same fixed-depth suite goes from 193,746 to 140,008 nodes. That is a strength-relevant change and it belongs behind an SPRT, in v4.2.0, where the net is retrained at QA=127 from the start rather than re-quantised after the fact.

### Accumulator cache ("finny table")

King moves invalidate a whole perspective because every HalfKAv2_hm feature is king-relative. The old path rebuilt it from the bias by adding ~32 rows. A per-thread cache now keeps, for each (perspective, king square), the accumulator it last produced and the piece placement that produced it, so a refresh applies only the difference: **4407 ns → 110 ns per refresh (40-52× cheaper), 99.6% of refreshes served from cache, 5.5 rows touched instead of ~32.**

Keyed by king SQUARE, not by bucket: two squares sharing a bucket are horizontal mirrors whose `Orient()` differs, so every feature index differs, and a bucket-keyed cache would blend two different feature spaces. At ft=128 refreshes are only ~6-7% of NNUE work, so this is insurance for v4.2.0 rather than a speedup today - at ft=1024 the same rebuild is 8× more expensive.

### Streaming dataset - the RAM ceiling is gone

`precompute_features` built dense in-RAM arrays at ~136 bytes per record, which is why `train_nnue.py` carried a `--max-records 120,000,000` cap. That cap was an **architectural ceiling**, not a safety valve: BLOCK 12 targets 300-500M positions, which is 40-136 GB.

Features are now decoded once into memory-mappable `.npy` shards and streamed. Chunk order is shuffled globally, then buffers of chunks are shuffled internally, keeping reads sequential while mixing across the whole file. Leftover rows are **carried into the next buffer** rather than dropped - discarding a partial batch per buffer silently loses training data every epoch (measured at 0.29% with a small buffer). Verified against the in-RAM decode of the same file: **0 duplicated records, exact subset of the training range, only the final tail dropped (< one batch), 0 validation leakage.** Training equivalence: streaming val 0.052585 vs in-RAM 0.052003 over 4 epochs, ~1% apart, which is shuffle-order trajectory noise.

Shards are rebuilt when older than their `.noadata`, and the completion marker is written last so an interrupted decode cannot be mistaken for a finished one.

### Provenance gate - the failure that cost gen7

Every dataset manifest on disk records `"openingPlies": "8-9 random legal"`. The human-opening seeding credited to v3.2.0 **never ran**: the pipeline was correct and `books/human.fens` existed, but `-Book` was never passed and nothing complained, so "pure self-play is exhausted" entered the ROADMAP, README and release notes as established fact.

The datagen now prints an unmissable `PROVENANCE:` line on every run, and `--require-book` turns operator intent into a checked precondition - a run that would have produced random openings while the pipeline reported book seeding exits with code 2 immediately, instead of 13 hours later.

### Also fixed (pre-existing, would have destroyed a training run)

When the validation split is smaller than one batch, no validation batches are produced, the loss is `nan`, no epoch counts as an improvement, and the checkpoint was written with `"model": None` - losing the entire run and only failing at export time. Now warned about explicitly and backed by a fall-back to the final epoch's weights.

222/222 tests, including new parity gates: cache vs direct refresh over random games and across the king-mirror boundary, int8 scalar vs SIMD over random games, and an explicit worst-case saturation test at maximum activation against maximum weight of both signs.

---

## (planned, v4.1.0 → v4.3.0) - BLOCK 12: remaining campaign

**Branch `4.0.0`. Supersedes blocks 7 and 8 and the "more generations" strategy entirely.** The v4.0.0 foundation above has shipped; what follows is the rest of the campaign.

### Diagnosis driving the campaign

Five consecutive generations landed flat (gen3 +4.5, gen4 +1.9, gen5 +34 over an inflated 1495-game link, gen6 no promotion, gen7 +3.7 parity), and deepening labels 24k → 28k nodes bought +3.7 Elo. That is a **saturated network**, not exhausted data. Against reference-class nets: feature transformer **128 wide vs 1024**, **1 output bucket vs 8**, **13.1 M positions per generation vs billions**, ~550 M samples seen per run vs tens of billions, 5.7 MB net vs 45–70 MB.

The prior conclusion that self-play was exhausted is void in any case: the human-opening seeding it rested on never ran (see the v4.0.0 provenance gate above).

### Versions

- **v4.1.0 - Data scale.** Labelling depth cut to 5–8k nodes; 300–500 M positions mixing bulk self-play, book-seeded openings, middlegame seeds and the elite WDL-anchored set; a 128-wide control net to isolate the data axis. *Expected +80 to +150.*
- **v4.2.0 - Capacity.** FT 128→512→1024 measured stepwise with NPS reported alongside Elo; 8 output buckets by piece count; deeper head. *Expected +150 to +300 - the largest single gain in the project.*
- **v4.3.0 - Search.** Complete the correction histories (only pawn exists today); re-enter statScore, cutNode, double extensions and multi-level continuation history **as a bundle**. *Expected +60 to +110.*

### Two permanent rules added

1. **Provenance must be machine-checked, never asserted.** If the manifest does not prove it, it did not happen. *(Enforced in code as of v4.0.0.)*
2. **"One term = one SPRT" is correct for evaluation terms and wrong for tightly coupled search features.** A feature worth +2–5 Elo alone is below the resolution of an 8,000-game SPRT, and search features are worth more together than apart. This is why 5C, 5E and the multi-level history all read as failures.

### Retired

More self-play generations at 13 M positions · lambda sweeps · NNUE eval-scale recalibration (measured −61.7) · competition opening book (deferred to v4.9.0) · **C++ interop for hot blocks** - the single-exe requirement is a standing decision, the NNUE hot path already emits the same AVX2 instructions MSVC would, a managed→native transition per leaf costs more than it saves, and ~+50–70 Elo per *doubling* of NPS makes a generous 15% gain worth ~+10 Elo. NativeAOT is the constraint-preserving version of that idea.

**Expected destination: ~3080 → 3300–3450 CCRL.**

---

## (unreleased, v3.4.0) - elite-data infrastructure: middlegame seeds + WDL anchoring

**Tooling only so far - no engine change, no net change, no strength claim.** Groundwork for the two data levers left after self-play, node depth, net size, lambda and search-threshold recalibration were all measured out.

**Middlegame seeds (needed no code).** `pgnbook` already exposed `--min-ply`/`--max-ply`, so seeding self-play from **ply 20-40** instead of 12-20 is a flag change. The point: gen7 seeded openings from elite games and landed at parity, and openings are the part of the game where an engine is least likely to be lost - the middlegame is where evaluation decides games.

**Elite WDL anchoring (new).** Two additions:

- `pgnbook --with-result` writes `FEN;R` (R = +1/0/-1 from White) instead of a bare FEN. `PgnReader` now captures the result token it previously discarded, and games still in progress (`*`) are skipped in this mode.
- `NoaChess.DataGen --label-book <file>` is a new data source that does **not** play games: it takes positions real strong players reached and labels each with **(our own deep search score, their actual game result)**. It reuses the self-play path's quiet-position filter (no in-check, no tactical best move, |score| < 20000) so the two datasets stay comparable.

**Why this is the one lever that adds information.** Every other signal in the pipeline is the engine's own opinion fed back to itself - self-play WDL is just its evaluation played out, which is why the gen3-era lambda sweep found it actively harmful (lambda 0.750 → score 0.338). A real game's outcome is external. **This is not imitation learning:** the human supplies neither an evaluation nor a move, only the position and who eventually won - the label's score still comes from NoaChess's own search. Training on human move choices is a known way to make a net *weaker* (distribution shift) and is deliberately not done here.

The dataset format needed no change: byte 32 of each record already carried "game result from the side to move". The manifest now records `mode` and `wdlSource` explicitly, so an elite-labelled dataset can never be mistaken for self-play - the kind of confusion that cost a whole gen7 training run when a stale feature cache went unnoticed. 281/281 tests.

## 2026-07-31 (v3.3.0) - proven-mate stop; NNUE scale alignment measured and cut

**Search-side only - no retraining, the embedded net is still gen7. Built on v3.2.1. Two things were tried; one ships, one was cut by SPRT and the negative result is recorded below because it closes a line of work.**

**SPRT vs v3.2.1 (10+0.1): +3.3 ±23.1, LOS 61.2%, [0.507] over 523 games - strength-neutral, stopped by hand at LLR 0.03 rather than run to exhaustion.** A ~0 Elo effect never converges against `elo0=0 elo1=5`, and the effect had to be ~0 by construction: the stop only fires on a mate proven within 2 moves, where the game ends immediately and the banked clock has almost no chance to be spent. The run's purpose was to rule out a REGRESSION - this engine has previous form here, an earlier "break on any mate score" made it walk into the shortest mate when losing - and 523 games with a ±23 interval do rule one out. **Shipped for the behaviour, not for Elo.**

**1. Proven-short-mate stop (the shipped change; validated by direct measurement).** The iterative-deepening loop now breaks when a completed iteration proves a mate in <= 3 plies for us, or that we are mated in <= 2 - the narrow case where deepening cannot improve the answer. Mirrors the reference time manager (`search.cpp`: `score >= mate_in(3) || score == mated_in(2)`). This is the deliberate exception to the existing "never break on mate scores" rule, which stays in force for LONG mates (deeper iterations find shorter mates when winning and longer defenses when losing).

Fixes an observed defect: **a mate-in-1 took ~1.07 s** because the only mechanism that could shorten it was easy-move, whose gate needs `depth >= 12`, while the mate is proven at depth 1. Measured at 5+5, same move and same reported mate in every case:

| position | v3.2.x | v3.3.0 |
|---|---|---|
| mate-in-1, 1 thread | 1074 ms (reaches d12) | **22 ms** (d1) |
| mate-in-1, 30 threads (bot config) | 1253 ms | **112 ms** |
| normal midgame | 12992 ms, d18, cp -19 | 13421 ms, d18, cp -19 |
| normal opening | 5035 ms, d16, cp 44 | 5073 ms, d16, cp 44 |

Clock mode only, so fixed-depth play is byte-identical (verified).

**2. NNUE-to-classical eval scale alignment - MEASURED AND CUT.** Every pruning constant is expressed on the CLASSICAL centipawn scale and several are compared directly against the evaluator's output, so the scale mismatch is real: measured over **6000 real positions** from the human opening book, gen7 regresses on the classical evaluator at **slope 0.783** (mean|nnue| 95.7 vs mean|classical| 114.0, ratio 0.84 - matching the training pipeline's own validate slope of 0.840). Correcting it looked obvious. **It lost decisively: 1250 permille scored 144-261-261 [0.412] over 666 games, −61.7 ±20.7 Elo, LOS 0.0%, H0 accepted** (10+0.1, proven-mate stop in both arms), negative from the first sample and monotone. The knob was removed.

**Why, since the measurement itself was correct:** (1) the margins are already calibrated to the compressed net in practice - gen3 through gen7 were each SPRT-validated with it, so the shipped combination is the empirically tuned one and "fixing" the scale broke a calibration that worked; (2) inflating the eval makes pruning MORE aggressive - RFP fires on `staticEval - margin >= beta`, so a 25% larger eval trips it far more often (likewise razoring and futility), producing unsound cutoffs. A compressed eval against fixed margins is equivalent to LARGER margins, i.e. safer pruning, and the engine prefers that.

**Method note kept in the code:** the first calibration attempt used artificial material positions (removing a piece from the start position) and produced a confident but WRONG "1.29x inflated" reading - the opposite direction. Such positions are far outside the net's training distribution, so the two evaluators simply disagree there rather than differing by scale. Always regress over REAL positions, in the magnitude range the consuming margins operate in.

## 2026-07-31 (v3.2.1) - bot stability: unbounded ponder spin + invisible stalls

**Hot-patch over v3.2.0. No evaluation change and no strength claim: this is a robustness release, born from diagnosing a Lichess bot that "stopped playing after a few games" and had to be restarted by hand.**

**The diagnosis first, because it was not what it looked like.** The engine was not hanging: the running session played 47+ games straight at 9-13 games/hour with no long silences and no `EngineTerminatedError`. The bot logs pointed elsewhere - **550 `TimeoutError` raised inside `asyncio.wait_for(protocol.initialize(), timeout)` on 2026-07-29** (lichess-bot passes `timeout=60.`, so the engine needed over a MINUTE to answer `uci` + `isready`), and **210 dropped lichess connections on 2026-07-30**, against zero such errors on 22-29 July. Root cause: lichess-bot spawns a fresh engine process per game, and with `Threads: 30` that process actually carries **~70-74 OS threads** (measured live) because ServerGC adds roughly one GC thread per core on top of the search threads. On a 16-core/32-thread machine nothing is left for the bot's Python/network thread or for the next game's engine startup. With `challenge.concurrency: 1`, one failed game is enough for the bot to look dead. **That part is fixed in the bot config (`Threads` 30 → 24), not in the engine.**

**Two genuine engine defects surfaced during the investigation, and those are what this version ships:**

- **Unbounded iteration depth in unlimited searches.** The iterative-deepening loop ran to `limits.MaxDepth`, which is `int.MaxValue` for ponder/infinite. In a repetition position with a warm transposition table every iteration returns instantly, so the loop spun through ever-higher depths that could no longer search anything - caught in a live bot game as **depths 22→26 completing in 30 ms** with the node count barely moving, burning a core for the whole of the opponent's thinking time. The loop is now capped at `MaxPly`, which the search stack could never exceed anyway, so only the degenerate spin is removed.
- **Stalls were invisible.** `WaitForSearchToFinish` waits for the search task without a timeout. That wait is correct - proceeding would break `ChessEngine`'s one-search-at-a-time contract - but a search that ignored cancellation would freeze the command loop with no trace at all, which under lichess-bot silently ends the night. It now emits `info string search still stopping after Ns` once per stalled second, so the failure is diagnosable from the GUI or bot log instead of looking like a freeze.

Matchmaking time controls were left unchanged, bullet included: on Lichess a player's clock does not start until AFTER their first move, so the engine's 2.5-7 s process startup costs no game time, and bullet is what maximises games per day. 281/281 tests.

## 2026-07-30 (v3.2.0) - NNUE gen7 (NNUE-0.7) + human-opening datagen pipeline

**SPRT gen7 vs gen5 (the previous embedded net, tc=10+0.1): +3.7 ±10.2, LOS 76.2%, [0.505] over 3000 games - marginal (parity; not a formal H1). SPRT gen7 vs classical: +28.5 ±13.0, LOS 100%, H1 accepted over 2176 games - the total accumulated NNUE value over the classical evaluator. CCRL gauntlet (240 games, 60+0.6, single-thread, 12-engine field 2862–3281): 57.9%, ~3080 ±40 CCRL, up from gen5's 51.0% (~3050) but within combined gauntlet noise.**

Promotes the gen7 net under the generational rule (Elo > 0 and LOS ≥ 75% - the same rule that promoted gen3/gen4). Honest read: the human-opening seeding did **not** produce a strength jump over gen5 - the net change is neutral-to-slightly-positive. The value of the release is elsewhere:

- **Human-opening datagen.** Self-play datagen now seeds openings from a human elite game book (chess.com >2800 + Lichess elite; 3.04M unique FENs deduped via the new `pgnbook` subcommand) instead of 8–9 random legal plies, which oversampled junk positions. This is the correct data foundation for the next step (elite-human-data training).
- **NNUE-over-classical pinned at +28.5 ±13.** The direct gen7-vs-classical SPRT (LOS 100%) supersedes the old cascade-sum estimate (~+46 by transitivity), which over-counted: self-play Elo deltas are **not additive** (draw compression across similar nets, plus an inflated gen5-vs-gen4 link measured over only 1495 games).
- Includes the **v3.1.2 time-management fix** (decisive-position clock waste + easy-move).

**CPU note (pre-existing, reconfirmed):** the magic-bitboard path auto-selects PEXT/BMI2 only on Zen3+/Intel at runtime (`Magics.UsePext`, via CPUID family gate); Zen1/Zen+/Zen2 keep the multiply-shift magics, where PEXT is microcoded and slower. One binary, no per-CPU builds. 210/210 tests.

## 2026-07-29 (v3.1.2) - time management: decisive-position clock waste + easy-move

**SPRT vs v3.1.1: −5.0 ±27.7, LOS 36.3%, [0.496] over 283 games (tc=5+0.05, Threads=1) - strength-neutral at this TC. The fix is justified by the direct clock-waste measurement (8.5 s → 1.68 s at 5+5), which matters most in bullet/hyperbullet where the bot bleeds time and can flag.**

Hot-patch over v3.1.1. No evaluation or search-correctness change. Fixes a measurable clock-bleeding defect present in all prior versions: at 5+5 rapid the engine spent **8.5 s on an obvious recapture** whose forced mate-in-8 it had already found at depth 15, banking nothing while the opponent banked time.

**Root causes (two, both in `AlphaBetaSearch.cs`, clock-mode only - fixed-depth is byte-identical):**

1. **Mid-iteration node-level cap now applies to single-thread.** The soft deadline is enforced only at root-move boundaries; in a won position with a warm TT a single deep root move can overshoot to the loose hard maximum before the next boundary check. A node-level cap `_maxTimeMs = min(hardTime, 1.5 × totalTime)` already existed for SMP (introduced in v3.1.0 for the ponderhit spike); it now applies to single-thread too. Constant renamed `SmpOvershootFactor` → `OvershootFactor`.

2. **Easy-move detection.** When `|score| ≥ 700 cp` (a large material lead or near-mate, well above any ambiguous position) AND the best move has been stable for ≥ 6 iterations AND `depth ≥ 12`, the remaining budget is capped to 12 % of the time optimum. Equal and complex positions are unaffected - measured: opening 13 s, midgame 24 s, Kiwipete 5.8 s, all identical.

**Measured effect:** decisive endgame recapture at 5+5: **8.5 s → 1.68 s**; at 60 s clock: 3.1 s → 0.68 s. Equal/complex positions unchanged. 210/210 tests.

All five constants are SPRT-tunable: `EasyMoveMargin` 700, `EasyMoveMinDepth` 12, `EasyMoveStableDepth` 6, `EasyMoveFraction` 0.12, `OvershootFactor` 1.5.

## 2026-07-29 (v3.1.1) - Engine cold-start fix (ReadyToRun AOT)

Hot-patch over v3.1.0. No search, eval, or Elo change. Pure startup-latency fix for the Lichess bot (lichess-bot spawns a fresh engine process per game; the cold JIT cost was ~25 s per launch, causing opponents to abort before Noa could move).

- Added `PublishReadyToRun=true` to the publish pipeline: the managed code is now compiled to native R2R format at publish time, eliminating the per-process JIT cost. Cold-start time: ~25 s → ~7 s (measured; full uci + isready cycle ~13 s cold, ~8 s warm).
- Reduced NNUE warmup search from depth 6 to depth 1. With R2R the managed code is already native at launch; depth 1 is sufficient to initialise the lazy accumulator before the clock starts.

## 2026-07-29 (v3.1.0) - Lazy SMP: parallel search + SMP time-management fix

**Multi-threaded search, up to 32 threads. `Threads=1` is byte-identical to v3.0.0 (verified: 1,307,077 nodes across a 6-position fixed-depth suite, exact match against the single-threaded build). Node throughput scales ~7.6× at 8 threads. SMP self-comparison: `Threads=30` vs `Threads=1` measures +253 ±104 Elo (tc=20+0.2, 24-1-12 over 37 games, LOS 100%, SPRT H1 accepted); CCRL field / long-TC calibration pending. Ships with an SMP time-management fix that bounds a ponderhit clock spike - a forced queen recapture that took 22-37s at 30 threads now stays ≤~5s (measured max 5.2s over 10 runs) - with single-thread play byte-identical.**

**First CCRL calibration of the NNUE line (measured 2026-07-28):** the embedded `noa-gen5` net scores **51.0% over 240 games** against a 12-engine field spanning 2862–3281 CCRL (20 games each, TC 60+0.6, single-threaded), for a maximum-likelihood performance rating of **~3050 ±40 CCRL** - it beats every opponent ≤3010 and loses to ≥3120, crossover ~3050. This lands only ~+15 over the ~3035 classical estimate, not the +42 the internal SPRT chain implied: the expected shrink of self-play gains against a diverse external field. It is the floor - deeper-node generations (gen6+) and the Lazy SMP multi-core gain add on top.

The search was single-threaded through the entire classical and NNUE campaign. This release adds Lazy SMP: several worker threads search the same root position in parallel, sharing one transposition table so they cross-pollinate each other's best lines, and vote on the move at the end. It is the last big untapped lever before further net generations - and it stacks with them, since the speedup applies to whatever evaluator is loaded.

### Design

- **Shared table, private everything else.** All workers share ONE transposition table (that shared memory is the whole point of Lazy SMP); every other structure - search stack, killer/history/continuation/capture/pawn-correction tables, counter moves, the board, and the evaluator - is per-thread, so the threads never write the same memory except the TT. The main worker (calling thread) owns time management and reports `info`; helper threads run headless until stopped.
- **Lock-free table by benign races.** The clustered table is read and written without locks. A torn 16-byte entry is caught by the 32-bit key verification and the existing pseudo-legality vetting of TT moves, so a corrupt read is discarded, never trusted or crashed on - the standard Lazy SMP contract. No locking overhead on the hot path.
- **Per-thread cloning.** `Board.Clone()` deep-copies the position (including the undo/repetition history) so make/unmake never races. The NNUE evaluator clone shares the read-only network weights but gets its own accumulator stack; the classical evaluator clone is a fresh instance (its scratch buffers are per-call). The shared table is aged exactly once per search, not once per worker.
- **Move voting.** After the main worker stops the helpers, the workers vote on the root move (score-weighted, with decisive-score/shortest-mate handling). Depth-limited and analysis searches take the main thread's line directly, as is conventional.

### Interface

- **UCI `Threads`** is now `spin default 1 min 1 max 32` (was pinned to 1). 1 preserves the exact historical search path; higher values enable Lazy SMP.

### SMP time management

Lazy SMP surfaced a time-management pathology: on a **ponderhit** relaunch over a warm transposition table with many threads, a trivial or forced move could burn far more than its share of the clock (a forced queen recapture measured 22-37s in a 3+2 blitz game, nearly flagging). Three fixes, all SMP-only - single-thread play is byte-identical:

- **Instability factor normalized over the pool.** The per-iteration best-move-instability multiplier was computed from the main worker's root-move changes alone; under the shared-TT races that count is noisy and spiked the soft budget toward the hard maximum. It now averages best-move changes across all workers (peer sum ÷ thread count), matching the reference.
- **Soft deadline capped at the optimum under SMP.** The dynamic factors (falling-eval, reduction, instability) can inflate at once; the extension is bounded at the optimum so a stable/forced move can no longer blow past it.
- **Mid-iteration node-level cap.** The soft deadline was only enforced *between* root moves, so a single deep root move begun near the budget edge - a warm TT after a ponderhit reaches high depth almost instantly - could still coast to the loose hard maximum before the next check. A node-level guard now tightens the deadline under SMP to 1.5× the (dynamic) soft budget, so the stop-check aborts the runaway move mid-iteration and the search keeps the last completed iteration's move. **Verified at 30 threads (10 runs): the forced-recapture spike drops from a 15-37s tail to a hard ceiling of ~5s (max 5.2s).** A non-regression match confirms the cap does not cost strength in normal play (drawish, decisives 3-1 for the capped build).

### Verification

- **`Threads=1` proven byte-identical** to the v3.0.0 base branch: the fixed-depth node-count harness produces the same total (1,307,077) and the same per-position counts. A single-threaded game is unaffected by this release.
- **Concurrency stress**: Classical and NNUE, `Threads` 1→32, repeated timed searches on six positions - no crashes, every returned move legal. NNUE exercises the per-thread accumulator cloning specifically.
- **Node scaling** (NNUE, fixed time): 1.00× / 2.02× / 4.14× / 7.60× at 1/2/4/8 threads - helpers do real work; at ≥4 threads the deeper aggregate search already changes the chosen move.
- **205/205 engine tests green.**

### UCI robustness fixes (both pre-existing, present since at least v3.0.0)

- **Output-pipe deadlock fixed - this is why the engine could go silent under lichess-bot and fast-TC/high-concurrency cutechess matches.** The command loop and the search task shared one blocking output writer. When the GUI was momentarily slow to drain stdout (common at fast TC with many concurrent games), a search-thread write blocked while holding the writer lock; the command loop then blocked trying to answer an `isready` keepalive, stopped reading stdin, and the GUI in turn blocked writing its next command - a classic pipe deadlock, both threads idle, no `bestmove` ever sent. All output now flows through a single-consumer queue: producers only ENQUEUE (never blocks), and one dedicated writer thread is the only thing that touches stdout, so the command loop can never stall on output. **Reproduced deterministically** (a stress match hung at ~24/60 games on both v3.0.0 and this build before the fix) and **verified fixed** (80/80 games complete, and again at Threads=8 vs Threads=1). Move/line order is preserved (FIFO, one consumer).
- **Startup banner suppressed for automated drivers.** The human-friendly banner was printed to stdout before the UCI handshake. It is now emitted only when stdin is a real console (`Console.IsInputRedirected` is false), so a GUI or bot sees nothing before `uci` - a strict UCI reader can desync on text before the first `id`/`uciok`.
- **Spurious `UseNNUE ignored` message removed.** When a GUI (e.g. Arena) sent `setoption UseNNUE true` before the first `isready` - before the embedded net had loaded - the engine printed `UseNNUE ignored: no valid model loaded`, which looked like the net had failed. The embedded net loads on that first `isready` and now applies the requested `UseNNUE` state, with no bogus warning.

## 2026-07-25 (v3.0.0) - HalfKAv2_hm NNUE: the neural evaluation ships

**Generational self-play net beats the classical evaluator: gen3 +4.5 ±11.4 Elo, 1002-968-680 [0.506] over 2650 games, LOS 77.8%, exhausted positive (tc=10+0.1, elo0=0, elo1=5). LTC gauntlet calibration pending.**

The engine's own neural network now outevaluates the hand-tuned classical evaluation that took the project from v2.4.0 to v2.8.4. This is the milestone the whole classical/search campaign was banked for: the NNUE is trained end-to-end from self-play data the engine labels itself, quantized to integer weights, and run through a SIMD inference path fast enough to beat the classical eval at equal time. It is selectable at runtime (`UseNNUE`) and the shipped executable embeds the current net (`noa-gen3`) as a resource.

### The network

- **HalfKAv2_hm feature transformer (feature_schema_id 2).** InputSize 22528 per perspective: 32 king buckets × 704, where 704 = 11 piece planes × 64 squares. Kings ARE features (both share a plane). King-orientation mirroring folds files a-d onto e-h; Black's perspective is rank-flipped. Topology: FT 22528→128 ×2 perspectives → concatenated 256 → L1 32 → 1 output. Clipped-ReLU activations.
- **Quantization contract.** Int16 feature-transformer weights, int32 accumulators, QA=255, QB=64, OutputScale=400. Trained in float, exported to integer with documented scales; C#↔Python inference verified bit-exact within the quantization error.
- **Incremental accumulator.** A per-perspective accumulator pair is pushed and popped with the search stack; `AddFeature`/`SubtractFeature` are vectorized with `Vector<short>`, and a king move triggers a full refresh of the mover's perspective plus an opponent-side patch (kings are features on both sides). A parity gate asserts incremental == full recompute on every position in the suite.

### SIMD inference

- **AVX2 path** via `Vector<short>` VPMADDWD for the L1 matmul, with the clipped activation precomputed once per evaluation (was re-clipped per L1 output) and a fused single-pass `MoveFeature`. Measured **312k → 446k NPS** on the accumulator-hot path, taking NNUE from ~46% to ~66% of the classical evaluator's speed - fast enough that a wider net is counterproductive at real time controls, so the shipped net stays 128×32.

### Datagen and training

- **`NoaChess.DataGen`** self-plays node-limited searches and labels each position with `lambda·sigmoid(score/SCALE) + (1−lambda)·wdl(result)`. Resign (`--resign`) and draw (`--drawscore`/`--drawcount`) adjudication plus a ply cap (`--maxplies`) stop dead games instead of shuffling to the cap. Binary `.noadata` format with a magic header and schema validation. (Syzygy WDL relabeling of ≤6-man positions is planned but not yet wired into datagen.)
- **Training pipeline** (`tools/training/nnue/`): `train_nnue.py` (cosine LR, weight decay, CUDA), `validate_nnue.py` (corr/slope/RMS/sign diagnostics), `export_model.py` (float → quantized `.noannue`). Net width is parametrized (`--ft-out`/`--l1-out`); the C# loader reads dimensions from the header, so architecture sweeps need zero C# changes.

### Critical datagen bug fixed (AlphaBetaSearch.cs)

`FindBestMove` returned `SearchResult.Score = 0` whenever a node-limited search hard-stopped during the first root move of an unfinished iteration, **zeroing 57% of all datagen labels** (a queen-up position could be labeled 0.0). The fix keeps the last completed iteration's result on a hard stop and uses the partial first-move score only when no iteration finished at all. Invisible to game play - UCI reports its score from completed-iteration progress callbacks and plays the same TT-first move; only the returned `.Score`, which datagen consumes directly, was wrong. Verified: **57.6% → 2.1% zero labels** on a fresh 2000-game dataset (the residual 2.1% are genuine repetition/dead-position draws), move selection provably unchanged.

### Generational self-play

A first-generation imitation net learns the classical eval well (corr 0.97) but still loses, because it plays its own games into positions it never trained on - classic distribution shift. The fix is generational: each promoted net teaches the next generation's datagen. **gen2: +1.9 Elo vs classical (H1). gen3: +4.5 ±11.4 Elo, exhausted positive at 2650 games (LOS 77.8%).** The pipeline is automated end to end (datagen → train → validate → export → embed → publish → SPRT → auto-promote).

### Verification

- **276/276 tests green**, including the incremental/full-recompute accumulator parity gate and the frozen golden feature indices. The datagen fix carries 205/205 engine tests with move selection unchanged.

## 2026-07-23 (v2.8.4) - LMR ttCapture and ttPv adjusters on the fixed-point pipeline

**SPRT vs v2.8.3 (tc=10+0.1, elo0=0, elo1=10): exhausted positive at 3000 games - 851-772-1377 [0.513], +9.2 ±9.1 Elo, LOS 97.5%, LLR 1.91. LTC gauntlet pending.**

The v2.8.3 fixed-point LMR pipeline (1024ths of a ply, verified behaviour-neutral) created the precision needed to port individual reduction adjusters from reference engines without integer truncation swamping the signal. This release carries the two adjusters that survived individual screens at the real time control: **ttCapture** (reduce more when the TT move was a capture) and **ttPv** (reduce less at nodes that were on a previous PV). Each was screened individually against the running bundle (+7.1 and +7.5 Elo respectively at LOS >93%), then validated together against the shipped v2.8.3 baseline.

### Changes

- **ttCapture LMR adjuster.** When the TT move is a capture or promotion, late quiets are reduced by an additional ~1 ply (`r += 1079` in 1024ths): the position has a forcing continuation in the TT, so quiets on this node are relatively less interesting. Gated on `ttServed` so the flag is only set when the TT actually delivered the move. Screened +7.1 ±9.1 Elo, LOS 93.7%.
- **ttPv LMR adjuster.** When the TT entry's `ttPv` flag is set (the node was on a previous search's principal variation), late quiets are reduced by ~1 ply less: `r -= 1024 + (nonPv ? 0 : 340)`, plus two small TT-hit sub-terms (`r -= 300` if the TT score beats alpha; `r -= 277` if the TT depth covers the current depth). Scaled ~×0.34 from the reference magnitude to keep the base at ~1 ply rather than ~3 (which would floor the milder reductions to zero). Screened +7.5 ±9.0 Elo, LOS 94.9%.
- **cutNode threaded through Negamax (behaviour-neutral).** The expected-cut-node flag is now propagated correctly through all recursive calls (root, PV, LMR scout/re-search, null-move, ProbCut, singular). The isolated `cutNode` LMR adjuster was measured at both magnitudes on the fixed-point pipeline and rejected (−4.0 H0 at r+=4026; −7.1 H0 at r+=1536). Threading KEPT as a behaviour-neutral correctness change (consumed by ProbCut verification).
- **ContinuationHistory MaxScore 8192 (correctness fix).** The continuation-history table's gravity bound was 2²⁰ (inert - measured flat +4.1 ±9.1). Correctly sized at 8192, matching the operating range of the table. Behaviour-neutral in practice.
- **Dead LMR history term removed.** The `clamp(history/16384, -2, 2)` butterfly-history adjuster was always zero (butterfly is bounded at 7183, so `7183/16384 = 0` in integer division). Three variants of direct butterfly-history adjustment were measured and all rejected: statScore −18 Elo H0, symmetric clamp −4.8 ±11.4 H0, one-sided add-only +4.2 ±9.1 flat. The line is closed.

### New lesson: signal quality predicts LMR adjuster success

**Signal quality, not reduction direction, determines whether an LMR adjuster works.** Both winning adjusters use clean categorical signals (TT move IS a capture? node WAS on a PV?) and work regardless of whether they add or remove reduction. Both losing signals were noisy: our derived cut-node classification and raw butterfly-history magnitude (skewed distribution, mean +71.8 against median −8). Prefer future adjusters keyed on clean categorical facts.

### Verification

- **276/276 tests green** (71 Core + 205 Engine). The two pre-existing intermittent failures (Syzygy DTZ NullReferenceException and EvalSymmetry colour-symmetry assertion) are unchanged and pass in isolation.

## 2026-07-23 (v2.8.3) - working history gravity, and the fixed-point LMR pipeline

**SPRT vs v2.8.2 (tc=10+0.1, elo0=0, elo1=10): 241-183-411 [0.535] at 835 games, +24.4 ±17.5 Elo - the interval excludes zero, but read the caveat.** The run was stopped by hand at 840 games with **LLR 2.61 against an upper bound of 2.94**, so this is **NOT a formally accepted H1**: cutting a sequential test at a favourable moment is exactly what the stopping rule exists to prevent, and it inflates the false-positive rate by an amount this entry cannot quantify. It is reported as a strong point estimate backed by a second instrument, not as a passed test. **Calibrated LTC gauntlet: 624 games, 65.6%, +112 ±24 relative to the field** against v2.8.2's +94 ±23 - the same direction, though the +18 difference sits inside the ±33 the two gauntlets jointly carry.

### The defect

`ContinuationHistory` was given "bounded gravity updates" in v2.8.2 and the release notes credited part of its +28.0 Elo to them. **That update never did anything.** Its decay term is `score × |bonus| / MaxScore`; with `MaxScore = 2²⁰` against entries that live near 7 000 and depth² bonuses near 169, the expression evaluates to `6086 × 169 / 1048576 ≈ 0.98`, which integer-truncates to **zero** on every realistic update. Confirmed twice - by the arithmetic, and by applying the identical rule to the butterfly table at the same bound and watching its distribution fail to move (mean 71.7 against 71.8). **A gravity bound has to BE the operating range**, which is why the reference sizes its butterfly table at 7183 and its continuation tables at 30000, just above where the values actually sit.

The consequence was visible in the data. `HistoryTable` grew with a global halving rescale on the positive rail while clamping individually on the negative one, so its signed distribution came out badly skewed: **mean +71.8 against a median of −8, only 25% of entries positive, and a tail reaching 6086**. Any consumer reading the raw value saw a handful of moves dominate everything.

### Changes

- **`HistoryTable` gravity, bound at 7183.** `score += bonus - score×|bonus|/MaxScore`, with the bonus clamped to the same rail exactly as the reference does. Remeasured after the change: **mean +71.8 → +13.5, p99 2840 → 1628, max 6086 → 3134** - the bias falls 5.3× and the tail halves. This is not only an LMR concern: `MovePicker` reads this table directly to order quiet moves, so the long positive tail was distorting the ordering too.
- **LMR pipeline converted to fixed point (1024ths of a ply).** The reference keeps its whole reduction in fixed point and divides only at the point of use, because every one of its adjusters is a *fraction* of a ply; NoaChess accumulated in whole plies and its table truncated at build time, making each ±1 adjuster three to ten times too coarse. **Verified behaviour-neutral** - identical node counts to v2.8.2 across six positions, since `floor(a)+k == floor(a+k)` for integer k - so it contributes nothing to the measurement above and exists to let the adjuster suite be ported at native granularity later.

### Measured and rejected on the way

- **statScore as the LMR history term: 47.4%, LLR −1.85, about −18 Elo, H0.** This closes a real evidence gap: block 5C cut it on a 1000-game 5+0.05 match, a control this project's own golden lesson calls unable to predict the sign at 10+0.1 - V-b had already demonstrated the inversion, going +17.4 there and −10.8 at the real control. The cut now rests on valid evidence. **Root cause: the skew above.** Subtracting an uncentred statistic does not discriminate between good and bad quiet moves; it exempts a few moves from reduction over and over through the tree, which measured as 15-20% more nodes and, at a fixed time control, cost about the Elo observed.
- **New golden lesson - formula fidelity is not semantic fidelity.** The reference consumes its statistic raw in LMR because its tables are gravity-bounded and symmetric, so it is centred near zero. Copying "use the raw value" onto a skewed table imports a bias the reference does not have. Before porting a consumer, measure the *distribution* of what it reads, not just its magnitude.
- **Node counts cannot calibrate a pruning parameter.** A divisor sweep returned +19.2 / +1.2 / +24.9 / +23.7 / −1.2 percent with searches verified deterministic - genuine chaos, not measurement noise.

### Also corrected in the documentation

The v2.8.2 entry's gravity claim, the v2.8.1 field range (2680–3150, not 2680–3120), the block-5C summary that described all five candidates as measured at the real control when two were not, a `RookOnKingRing` row reading MISSING for a term implemented in v2.6.3, a stale `PARTIAL (v2.7.4)` on capture history that reached the main search in v2.8.1, a "check bonus pending" note for a bonus shipped in v2.8.1, a razoring row carrying no status at all, block 9's implementation section describing a Fathom P/Invoke that was never built, and the outdated statScore formula that nearly caused a bad port. **King Safety Phase B and KingProtector were moved behind block 6 (NNUE)** in all four documents.

### Verification

- **276/276 tests green** (71 Core + 205 Engine). `PartialQuietSort_OrdersOnlyMovesAboveDepthCutoff` needed updating: it drove the table with `depth: 100/200`, producing bonuses of 10 000 and 40 000 that now clamp to the same rail and tie. Rewritten with depths inside the bound; passes 5/5 in isolation.
- **Known coupling, deliberately left alone** to keep this to one measured change: the partial quiet sort cutoff is `−3000×depth`, and with history bounded at ±7183 that cutoff is no longer reachable from history alone at depth 3 or more. Continuation history and the threat terms (up to ±20 000) still reach it, so the optimisation is not dead, but it fires less often.
- **Two intermittent test failures remain**, unrelated to this release and both pre-existing: a `NullReferenceException` in the Syzygy DTZ probe and a colour-symmetry assertion, each firing roughly one run in five. Diagnosis points at shared static state observed across test classes that xUnit runs in parallel. In real play `Syzygy.Init` is called once from `UciLoop`, so this is a test-harness problem rather than a game risk.

## 2026-07-22 (v2.8.2) - validated search audit, correction history, ProbCut verification, UCI log hardening

**SPRT vs v2.8.1 (tc=10+0.1, elo0=0, elo1=10): H1 accepted at 834 games, 256-189-389 [54.0%], +28.0 ±17.2 Elo, LOS 99.9%, LLR 2.99. Calibrated LTC gauntlet: 296-131-197 [63.2%] over 624 games, +94 ±23 relative to the field, ~3013 ±30 CCRL.** Repeated 8-ply openings and reversed colors. The absolute estimate is 3010 from the literal engine labels and 3013 after applying the existing 2548-game field calibration. Component tests below are diagnostic and must not be added as if independent Elo gains.

### Search and evaluation

- **Pawn correction history:** a side-to-move × pawn-Zobrist table learns the residual between searched scores and the classical static evaluation. The corrected value feeds improving, forward pruning and quiescence stand-pat; the raw evaluator value remains in the TT so a learned local bias is never persisted as position truth. Updates are bounded, depth-weighted and restricted to quiet, bound-consistent, non-tablebase conclusions. It was introduced in two isolated steps: main-search correction **49-33-118, +27.9 ±30.8 Elo**, then quiescence correction **59-49-92, +17.4 ±35.5 Elo**. An isolation build with correction completely disabled trended worse (**96-119-181 [47.1%], approximately -20 ±25 Elo at 396 games**) and was stopped; correction therefore remains in the H1-winning final build.
- **ProbCut rework:** entry from depth 3, improving-aware margin and verification depth, SEE threshold tied to the gap between static eval and `probBeta`, mandatory regular-search verification of at least one ply, lower-bound TT storage, fail-soft return outside mate/TB bands, and small ProbCut from a sufficiently deep TT lower bound. Isolated A/B: **59-51-90, 52.0%, +13.9 ±35.8 Elo, LOS 77.7%**. Queen promotions are explicitly exempt from the gap-based SEE gate because the simplified SEE does not model the promoted piece; a regression found in review is covered by quiet- and capture-promotion tests.
- **Aspiration final form:** the SPRT winner retains the fixed profile half-window. The experimental adaptive initial window was removed after it increased re-search cost at 10+0.1. The fail-low beta recentering remains.
- **Proven refutation bands retained:** the experiment that demoted killer and counter moves to small continuous bonuses was removed; the final H1 build keeps the 3.0M/2.9M bands.
- **Continuation-history "gravity" - CORRECTION (2026-07-23).** This entry originally credited the H1 result in part to putting continuation history on bounded gravity updates instead of the ±2²⁰ clamp. **That claim is wrong: the update as shipped is numerically inert.** Its decay term is `score × |bonus| / MaxScore`, and with `MaxScore = 2²⁰` against values that live near 7 000 and depth² bonuses near 169, the expression is `6086 × 169 / 1048576 ≈ 0.98`, which integer-truncates to **zero** on every realistic update. Verified empirically as well: applying the identical rule to the butterfly table at the same bound left its distribution unchanged to one decimal (mean 71.7 against 71.8). The bound has to BE the operating range for gravity to act - the reference sizes its butterfly table at 7183 and its continuation tables at 30000, just above where the values actually sit. **The +28.0 Elo H1 therefore came from the rest of this bundle, not from gravity.** A candidate that fixes the bound is being measured separately.
- **No unconditional check extension:** the speculative `inCheck => depth+1` experiment was removed. It was absent from the measured v2.8.1 artifact, cost depth at short time controls and had no significant isolated evidence.

### UCI reliability and logging

- `NOACHESS_DEBUG_LOG` no longer activates logging. This prevents an inherited user/machine variable from silently creating the same unbounded file in Arena, lichess-bot and test processes.
- `Debug Log File` remains available explicitly. Opening is transactional; an invalid replacement path leaves the current writer intact. Setting it to `<empty>` closes and unlocks the file immediately. Quit/EOF close it in `finally`; write/dispose failures disable diagnostics instead of terminating the UCI loop.
- Clean `quit` and unexpected stdin EOF are logged as distinct termination causes.

### Rejected during the audit

- The complete historical 5H package (adaptive aspiration plus razoring) scored **41-56-103, -26.1 ±33.6 Elo, LOS 6.4%**. Bisection showed aspiration positive, therefore razoring was removed.
- The first all-features candidate (correction + adaptive initial aspiration + unconditional check extension + continuous killer/counter bonuses) failed the formal SPRT: **291-333-491 [48.12%], -13.1 ±15.2 Elo, H0 at 1115 games**. Short component A/B figures had all included zero and did not predict their interaction.
- Removing correction alone did not rescue it (**47.1% at 396 games**). The successful RC2 kept correction while removing adaptive initial aspiration, unconditional check extension and continuous killer/counter ordering; it passed H1 as reported above.
- Dynamic null-move R and multi-cut were not merged: their archived failures are structural, not missing syntax. The former needs the reference `cutNode`/eval-gate ecosystem; the latter repeatedly loses tactical accuracy. No NNUE, SMP or other future-roadmap work was introduced.

### Verification

- **276/276 tests green** (71 Core + 205 Engine), including 8 new cases: pawn-correction residual/clear and side-to-move separation, continuation-gravity bound-and-recover, three debug-log lifecycle regressions (`<empty>` closes and unlocks, invalid switch preserves the active log, clean `quit` differs from EOF), and quiet/capture queen promotions bypassing the ProbCut SEE gate. Counting basis: 276 is the full discovered suite with the local tablebase set present. Earlier entries are not on the same basis - v2.8.1's "193/193" counted only the tests that executed while the Syzygy files were absent and the gated cases skipped; the discovered suite at v2.8.1 was 268.
- **Final RC2-corr SPRT vs frozen v2.8.1:** **256-189-389, 54.0%, +28.0 ±17.2 Elo, H1 accepted at 834 games**.
- **Final LTC gauntlet (tc=60+0.6):** **296-131-197, 63.2%, +94 ±23 relative to the field, ~3013 ±30 CCRL**. The 13 fixed-label anchors give 3010 ±30; replacing their labels with the ratings inferred by the prior 2548-game all-play-all calibration gives 3013 ±30. The existing calibration confirms `Winter-3120`; all other field labels remain within normal uncertainty, with `Rubichess-3150` still only on watch (calibrated near 3108).
- **Provenance of the measured binary.** The published executable answers `id name NoaChess 2.8.2-RC2-corr`, not `2.8.2`: it was built from the RC2 source before `EngineVersion` was set back. Both numbers above, and the copy deployed to the Lichess bot, describe that binary - byte-identical across `engines\NoaChess-2.8.2\` and the bot's engine folder, verified by hash. The difference from the committed tree is believed to be the version string alone, but it was never rebuilt from the commit, so the strict statement is that ~3013 CCRL measures the RC2-corr artifact. Recorded because the same class of gap - a published number describing a binary that is not the committed code - already cost an investigation with commit `5616060`.
- Syzygy and ponder remain operational and independently verified in lichess-bot logs; this release does not alter their probe protocol.

## 2026-07-20 (v2.8.1) - Syzygy correctness fixes, capture-history main ordering, partial quiet sort, threat-aware quiet scoring, NNUE/tuner tools infrastructure

**SPRT vs v2.7.4 (tc 10+0.1, elo0=0 elo1=10): +14.1 ±10.8 Elo, LOS 99.5%, H1 accepted at 2175 games [0.520], DrawRatio 45.3%. LTC gauntlet (tc=60+0.6, 624 games, 13 anchors 2680–3150): +75 ±23 relative to the field (+23 over v2.7.4's +52 on the same field). Strength: ~3000 ±25 CCRL.** Field audit (round-robin 2548 games, 14 engines, tc=60+0.6): **Winter-3200 renamed to 3120** (implied 3118, 2.4σ, confirmed in both gauntlet and round-robin equal to Rubichess-3150). **Rubichess-3150 on watch** (implied 3113, 1.1σ - one more run decides). **Meltdown-2817 cleared** (implied 2822, essentially correct). Tcheran-2917, Ethereal-2910, Colossus-2862 verified to within ≤3 Elo of their labels.

Expert contributor review of the v2.8.0 Syzygy integration found two critical bugs that corrupted every tablebase-assisted game, plus several move-ordering improvements from block 5G that were left pending.

### Critical Syzygy fixes (two bugs that cancelled the v2.8.0 work in practice)

**Bug 1 - Root filter was silently nullified.** `FilterRootMovesByTablebase()` correctly computed the filtered move list and wrote it to `_rootMoves`, but `SearchRoot` then regenerated all moves from scratch, discarding the filtered list entirely. The prober output was correct; the move played was not. Fixed: `SearchRoot` now reads `_rootMoves` directly instead of regenerating.

**Bug 2 - DTZ ranking scored irreversible moves incorrectly.** Root moves that capture, push a pawn or promote zero the fifty-move counter immediately; their DTZ must be derived from the position's WDL BEFORE the move, not from the child's DTZ. Previously the code was reading the child's DTZ and accidentally giving zeroing moves an arbitrary distance instead of ±1/±101. Additionally, lost positions chose the fastest loss (smallest negative DTZ) instead of the longest defense - the comparison was inverted. Both corrected in `TryRankRootMovesByDtz` and `RootDtzRank`.

### TT safety for tablebase scores

`CanReuseTtScore` now blocks reuse of TB-band scores when `halfmoveClock > 0`. The Zobrist key deliberately omits the clock, so a decisive TB score learned immediately after a zeroing move could propagate to the same position with a live counter and cause an incorrect early cutoff.

`ToTT`/`FromTT` now handle the full `TbScoreBound` range (both mate and TB scores) consistently.

### Memory-mapped file reader (future-proofing for 6/7-man)

`SyzygyTable` migrated from `byte[]` + `int` offsets to `MemoryMappedFile`/`MemoryMappedViewAccessor` with `long` offsets throughout. The OS pages in only the blocks a probe actually touches; the 2 GB `byte[]` ceiling is eliminated, making 6- and 7-man files (many exceed 2 GB) usable without code changes. All `PairsData` offset fields are now `long`. `SyzygyTable` implements `IDisposable`; `Syzygy.Init` disposes all open mappings before re-initialising, so no file handles are leaked between path reloads.

### Capture history - main search integration (5G)

`_captureHistory` is now passed to `ScoreAndSortCaptures` and `OrderCaptures` (ProbCut) throughout `AlphaBetaSearch`. Capture cutoffs earn a bonus (`depth²`); captures tried before the cutoff earn a malus. The ordering formula is `captureHistory + 7 × victimValue`, matching the reference.

`CaptureHistory.AddBonus`/`AddMalus` now use a `Magnitude()` helper (`Math.Abs((long)value)`) to prevent overflow before the gravity clamp.

### Partial insertion sort for quiet moves (5G)

`MovePicker.ScoreAndSortQuiets` now accepts `int? depth`. When provided:
- the quiet block is moved in front of any unserved losing captures (`MoveRangeToFront`), matching the reference stage order QUIET → BAD_CAPTURE;
- `PartialSortRange` sorts only moves scoring above `−3000 × depth` into a descending prefix; the low-scored tail is left unsorted (paying O(n²) to order moves the node will never reach has no value).

### Threat-aware and check-aware quiet scoring (5G)

`Score()` now awards `CheckBonus = +16 384` to direct checks that do not clearly lose material (`SEE >= −75`). It also applies a `ThreatEscapeWeight × PieceValue` term: moves that escape a lesser-piece threat score higher; moves that enter one score lower. Threat maps (pawn, minor, rook attacks) are built once per quiet batch in `BuildQuietOrderingContext`.

### X-ray mobility correctness fix

Sliders now see through the own queen only (bishops and rooks through the own queen; rooks additionally through own rooks), matching the reference exactly. The v2.8.0 code was computing x-ray attacks through all queens.

### UCI - Ponder option

`option name Ponder type check default false` is now declared in the UCI handshake. `setoption name Ponder value true/false` is accepted. Relevant for GUI usage (Arena, BanksiaGUI, etc.). Note: cutechess-cli does not implement pondering in tournament mode and warns regardless of the declaration.

### Portable Syzygy test infrastructure

`SyzygyTests.cs` completely rewritten. All integration tests now gate on the local tablebase path (configured at build time) via `SyzygyFactAttribute`/`SyzygyTheoryAttribute` - they skip gracefully when the large external files are absent. New integration cases cover: root-filter-not-regenerated, DTZ-at-rule-50-boundary, lost-position-longest-defense, WDL-only fallback when DTZ files are missing, ProbeDepth ignored below the loaded cardinality, `TbHits` counting. `SyzygyScoreTests` (always-on, no files needed) verifies `ToTT`/`FromTT`/`CanReuseTtScore` arithmetic via reflection.

### New test files

- `CaptureHistoryTests.cs` - gravity overflow guard, main ordering (7×victim + history), capture-promotion ordering, safe-check bonus, threat-escape/enter bonuses, partial sort prefix, quiet-before-bad-capture invariant.
- `UciSearchLimitsTests.cs` - regression tests for the `go` parser combining clock + depth + nodes; movetime takes the tighter bound; `infinite` has no artificial depth cap.

### New development tools (not shipped in the UCI binary)

- `tools/NoaChess.DataGen/` - self-play data generation for NNUE training corpus: random-opening games with fixed-depth search, FEN + result output in a format suitable for the Python training pipeline.
- `tools/NoaChess.Tuner/` - Texel tuning infrastructure: `ParameterRegistry`, coordinate-descent loop, `tuned_values.txt` output. Re-usable for any classical evaluation parameter.
- `tools/training/nnue/` - Python NNUE training pipeline: dataset loader, HalfKP model definition, training loop, validation, weight export. Configs in `tools/training/nnue/configs/`.

### Tests

193/193 always-running tests pass. The 5 regressions present in the early branch state (rule-50/stalemate/dead-position/quiet-mate in `QuiescenceTests`) are resolved by the Syzygy TT-safety and draw-detection fixes above.


## 2026-07-20 (v2.8.0) - block 9: Syzygy endgame tablebases

**Never independently validated.** Two critical bugs in the root filter and DTZ ranking (fixed in v2.8.1) made the Syzygy integration a functional no-op in practice. The SPRT and gauntlet were run against v2.8.1 instead. Everything below that is stated as measured, is measured.

Pulled ahead of NNUE deliberately, following the reference's own order (Syzygy 2014, NNUE 2020): it improves endgame play now, and later it relabels the noisiest slice of the NNUE training corpus with exact WDL.

**Not a P/Invoke - a managed port.** The roadmap called for binding the usual C probing library. That is not what shipped: there is no C toolchain on this machine, and a native DLL would break the single self-contained .exe requirement. The prober is ~1250 lines of C#.

**Why that had to be proven rather than assumed.** A wrong index does not crash: it returns a WRONG result that looks perfectly valid, and the search then trusts it absolutely - strictly worse than having no tablebases at all. The port is therefore differentially tested against an independent prober over randomly generated endgames: **3000 positions, 3-to-5 men, both sides to move, zero WDL discrepancies and zero DTZ discrepancies.** That harness found three bugs that would otherwise have reached play silently:

- the symbol-tree base offset was cached per TABLE instead of per `PairsData`. A pawn table holds eight of them (4 files × 2 sides), so the decompressor walked a misaligned tree and **hung the engine outright** rather than returning anything wrong.
- an off-by-one in the DTZ value remap (`map[idx + value]`, not `idx + value - 1`).
- captures reducing to bare kings have no two-man table; the recursion failed instead of returning the obvious draw.

**What ships:**

- search: **WDL probe** after the TT probe, gated on the fifty-move counter being zero - the tables answer "won" without regard to that rule, and a win needing more plies than the counter allows is really a draw. Castling rights are refused inside the prober for the same class of reason. Verdicts live in **their own score band below the mate range**: they are certain, so they must outrank every heuristic evaluation, but reporting them as mate scores would claim a forced mate the engine has not proven and corrupt the mate-distance arithmetic.
- search: **root move filtering** by WDL then DTZ - win > draw > loss, and among wins the shortest distance to zeroing. Knowing an ending is won is not enough to win it: with no distance to steer by the engine shuffles and draws by the fifty-move rule. **Deliberately a filter and not an early return**: returning the verdict directly would replace "mate in 3" with a plain tablebase win in the UCI output and undo the mate reporting added in v2.7.1. Filtering keeps the search running while making it structurally impossible to throw a won ending away.
- uci: `SyzygyPath`, `SyzygyProbeDepth`, `SyzygyProbeLimit`, `Syzygy50MoveRule`.
- perf: the probe guard is ordered by **selectivity rather than readability** - the piece count is the test that fails at practically every middlegame node, so it goes first, against a cached limit that is 0 when no tablebases are loaded. The obvious ordering cost **3.5% NPS on positions that never probe at all**; this brings it to 1.1%, which is the honest cost of one popcount per node.
- tests: 208/208, including 16 new Syzygy cases. **Every expectation is derived from the independent prober, not hand-reasoned** - five hand-written fixtures were wrong across this session (illegal positions with the side not to move already in check, a "drawn" position where a rook simply hangs, a "won" KPvK that is actually drawn).

**Measured behaviour:** a won KPvK converts in **15 plies with tablebases against 25 without** - the DTZ filter steering to the fastest win. KRvK and KQvK convert identically either way: the engine already handled those. The gain is concentrated where the heuristic is actually wrong (pawn endings, opposition, drawn positions that look won), which is rarer than "I am a rook up".

**Expected reach, counted over the previous runs' own PGNs** - and this corrects an assumption made when the block started: the 10+0.1 SPRT reaches five men or fewer in **32.1%** of games, the 60+0.6 gauntlet in only **22.9%**. At the longer control games are decided earlier and simplify less, so the SPRT is the better instrument here, not the gauntlet. More telling still: of the previous SPRT's decisive games only 189 of 565 reached five men, so **two thirds were settled before tablebases could have any say**. The reachable effect is therefore small - roughly +3 to +10 Elo - and elo0=0/elo1=10 struggles to resolve that. A flat result would not mean the probing is broken; it would mean the ceiling is in the EVALUATION, which is the same conclusion block 5 reached over seven consecutive blocks.


## 2026-07-20 (5F ProbCut · multi-cut · NMP dynamic R) - ALL THREE MEASURED AND CUT, NO RELEASE

**Search block 5 closes here.** These three were the archived items whose stated blocker was the broken quiescence, so they were retried on top of v2.7.4 to close the debt with measurement instead of inference. All three failed, and the premise itself turned out to be wrong: the quiescence was not the blocker for any of them.

| Candidate | Result vs v2.7.4 |
|---|---|
| 5F ProbCut rework | four variants, best still **+5.0% nodes**, WAC flat |
| Multi-cut | **−4.2% nodes but WAC 248 vs 266** |
| NMP dynamic R | **−14.3 ± 15.7 Elo, LOS 3.8%, H0 at 925 games** |

- **5F ProbCut** - reference shape (entry at depth 3, any node type) +16.3% nodes; with our validated conservative entry (non-PV, depth ≥ 5) +7.3%; with a flat depth−4 verification +11.2%, so the reference's improving-aware verification depth is genuinely better than ours; with the SEE threshold floored at 0, +5.0%. That last one is a real finding: the reference's threshold `probCutBeta − staticEval` goes NEGATIVE once the static eval already clears the bar, so every losing capture passes the filter and each one costs a quiescence plus a verification search. Even so, no variant beat the baseline, and WAC 269 vs 266 is inside the ±5 noise band, so there is no nodes-for-accuracy trade. The `probCutDepth` floor at 1 (no cutoff may rest on quiescence alone) is applied throughout and is the fix for the earlier −90 Elo.
- **Multi-cut** - returning the verification score when it reaches beta with the TT move excluded. WAC 248 vs 266, eighteen points down. In 5E the same test measured 265 → 245; after the quiescence fix it is 266 → 248, essentially unchanged. Unsound on our search in its own right.
- **NMP dynamic R** - two findings, and the first is an error worth recording. The formula was ported as `min((eval−beta)/81, 7) + depth/3 + 4` **from this project's own 5B notes, which quote an outdated revision of the reference engine**; the source on disk reads `Depth R = 7 + depth/3`, with no eval term at all. Second and more important: the reference gates its null move on `cutNode && staticEval >= beta − 13×depth − 47×improving + 365`. Its deep R is safe **because** it only fires at expected-cut nodes behind an eval gate, while ours fires everywhere ungated - deliberately, since 5B measured that gate inflating our tree ~30% (our classical eval is noisy relative to the search). So the blocker was the entry ecosystem, not the quiescence.
- The bench signature was the usual trap: −11.9% nodes and −17% wall time meant **more pruning, not better search**, and WAC cannot see unsound prunes. Node counts falling is not by itself evidence of improvement.
- Branches `exp-probcut`, `exp-multicut` and `exp-nmpr` keep the code and the numbers in their commit messages. Not merged.

**Block 5 tally.** Shipped: 5A improving flag (v2.7.0), 5B scope-cut NMP/RFP (v2.7.1), 5D transposition-table redesign (v2.7.2) and the v2.7.4 quiescence rework. Cut: 5C, 5E, 5G, 5F, multi-cut, NMP dynamic R. **Over seven blocks the pattern never moved: infrastructure and exact knowledge transfer** (staged movegen +101, TT redesign +37.9, timeman +14.3); **tuned reference heuristics do not**, because each one depends on entry filters that measure worse on this engine. Next is block 9, Syzygy (v2.8.0) - infrastructure, and it also supplies perfect labels for the NNUE datagen.


## 2026-07-20 (v2.7.4) - quiescence rework: correctness first, plus a terminal-root hang fix

**Correctness release: no measurable strength change.** SPRT vs v2.7.2 (tc 10+0.1, elo0=0 elo1=10): **−2.1 ± 9.9 Elo over 2347 games [0.498], H0**. LTC gauntlet (tc=60+0.6, 624 games, 13 anchors): **+52 ± 23 relative to the field** vs v2.7.2's +48 on the identical field - **+4 ± 32, statistically zero**. Strength stays **~2975 ± 25 CCRL**. Both instruments agree, so the equity is real and is reported as such.

It ships anyway because what it fixes are BUGS, not heuristics - including one that freezes the engine outright.

**The quiescence search handled in-check nodes wrongly in four separate ways:**

- It stood pat on the static evaluation of a position whose king is attacked - a meaningless number - and could therefore return a beta cutoff *while being mated*.
- It generated captures only, so the quiet king step or the interposition that is usually the ONLY escape from a check did not exist as far as the search was concerned.
- It applied SEE pruning in check, discarding the single legal defence whenever that defence loses material.
- It never detected mate: in check with no legal reply it returned the stand-pat score as if nothing had happened.

Every capture that gives check lands the opponent in exactly that node, so the hole sat on the main line of every tactical sequence - and ProbCut, null-move probes and multi-cut all verify captures THROUGH quiescence, so they were reading those wrong scores as proof. That is why five separate reference features have needed gates or been cut since 5B.

- search: in check - no stand-pat (`bestScore` starts at −infinity, the reference's own device for making its pruning block unreachable), ALL moves generated, no pruning of any kind, mate returned as `−MateScore + ply`.
- search: **stalemate guard** at the horizon, in the reference's shape - only reached when the side to move has nothing but king and pawns AND no pawn can even step forward, so full legal generation stays rare.
- search: **fail-soft** scores throughout (the real bestScore, never the alpha/beta rail).
- search: **all four promotion pieces** are searched; only the queen was before. An underpromotion can be the move that avoids stalemate, mates, or dodges a fork.
- search: the reference's **Step 6 pruning block**, ported whole rather than as isolated constants - `futilityBase = staticEval + 147`, a second gate on `min(alpha, futilityBase)`, and the SEE floor relaxed from `>= 0` to `>= −36`. Constants converted by the pawn ratio (the reference's pawn is 208, ours 100 - exactly the project's ×0.48 rule): 306 → 147, −74 → −36.
- heuristics: new **`CaptureHistory`** table `[piece][to][victimType]` with gravity updates (`entry += bonus − entry×|bonus|/4096`), feeding quiescence capture ordering as `captureHistory + 7×victimValue` in place of MVV-LVA.
- **uci/search: a terminal root hung the engine forever.** With no legal move on the board (checkmate or stalemate) iterative deepening looped through every depth without ever producing a best move, and no `bestmove` was ever sent - any GUI handing the engine such a position froze it permanently. Present in v2.7.2 and every earlier release. Now answered instantly with `bestmove 0000`.
- tests: 192/192, including **8 new quiescence correctness cases** (quiet-only escape, sole interposition, mate and stalemate at the horizon, a sole defence with negative SEE, perpetual-check termination, checking captures).

**Bench vs v2.7.2** (60 positions sampled from real games at the test time control, depth 13; wall time over 30): nodes geo-mean **0.943 (−5.7%)**, median 0.928, 33 better / 27 worse; **wall time to depth −9.0% / −12.6%**; NPS neutral; **WAC-300 at 400ms: 269/300 - new record** (v2.7.2 measured 263 the same day; 265 was the old record).

**The two halves only work together.** Correctness ALONE cost +8.3% nodes and +4.0% time - searching quiet evasions and promotions is real work. Adding the reference's own pruning block turned that into −5.7% nodes and −9…−12% time. The reference prunes harder BECAUSE it searches correctly; taking either half without the other is what made every earlier attempt look bad.

**A false premise was corrected in the documentation.** Our notes since 5B claimed the reference qsearch "generates checks at the first ply", and that claim had propagated into five separate design decisions. It does not: its own comment reads *"captures, or evasions only when in check"*. The real difference was the −infinity start in check, which is what is ported here.

**Field audit** (624-game LTC gauntlet): **Marvin-2960 measured 62 low for the second cycle running** (−56 in the v2.7.2 gauntlet, −62 here) → renamed to 2900. **BitGenie-3010 cleared**: +43 here after −130 last cycle, i.e. noise, off the watch list. **Rubichess-3150 (−120) and Meltdown-2817 (+73) to watch** - single-run deviations, and 48 games per opponent carries ±80–120, so neither is actionable yet. (Resolved in the v2.8.1 field audit: Winter-3200 → 3120, Meltdown cleared, Rubichess still on watch.)

## 2026-07-19 (blocks 5E + 5G, the v2.7.3 campaign) - MEASURED AND CUT, NO RELEASE

**The v2.7.3 slot closes with no release: both candidate blocks measured at the real time control and cut per the project decision rule. The engine stays at v2.7.2 exactly; the next release will be v2.7.4. Everything below is archived.**

**5E - singular extensions upgrade: four SPRTs at 10+0.1, all negative.**

| Candidate | Content | vs v2.7.2 |
|---|---|---|
| 1 | full port from an outdated spec (ttPv sign inverted, no multi-cut) | **−19.7**, H0 |
| 3 | trigger only: `depth >= 6 + ttPv` + shuffling guard | [0.492], 897g |
| 4 | trigger + qsearch in-check evasion rework | **−12.5 ± 15.0**, H0 at 1054g |
| 5 | + reference `!is_loss(bestValue)` evasion pruning guard | [0.476], 700g |

Root cause: the reference's extensions are only stable next to reference-grade reductions (r += 4026 cutNode, +1079 ttCapture in 1024ths; our whole LMR table tops out near 4). Also measured and rejected: `depth++` on singular (tree explosion), faithful `(28+32)*depth/63` margins, multi-cut (WAC 265→245), and **qsearch TT probe/store at depth 0** (depth-0 entries flood the clusters and evict main-search entries: d15 nodes ROSE 1.35M→1.75M, nps −11%).

**5G - multi-level continuation history: four builds, the last two at exact equity.**

| Attempt | Content | vs v2.7.2 |
|---|---|---|
| 1 | multi-level read/write on the SHARED single table, averaged blend into statScore | **−33.9 ± 25.8**, stopped at 413g |
| 2 | one table per distance, blend confined to move ordering | **−10.9 ± 14.3**, H0 at 1180g |
| 3 | + blend gated to depth ≥ 6 | [0.496] at ~1900g, stopped |
| 4 | + gravity updates (`entry += bonus − entry·|bonus|/2^20`) | **−4.2 ± 10.9**, H0 at 2000g [0.494] |

Real defects found and fixed along the way (the fixes are proven and stay in the archive):

- A single shared table CORRUPTS the levels: a bonus written for "the move two plies ago" lands on the very key another node reads as "one ply ago". With separate tables, a control build reading only level 0 reproduces v2.7.2 **bit-for-bit**; with the shared table the same control diverged −52%/+17% per position.
- The blend must never reach statScore: reverse futility's thresholds (offset 1250, divisor 180, the measured ×0.28 transfer) describe a one-level signal; feeding them the blend re-tunes the pruning silently (attempt 1's real failure mode).
- Blending everywhere costs −9.9% NPS (5 random probes over 14 MB per quiet move): +9.6% wall time to depth = the −10.9 Elo of attempt 2, exactly. Gated to depth ≥ 6 it wins on nodes AND nps (−11.5/−14.0% wall time to depth).
- The continuation table was never decayed within a game (18M entries, too big to sweep like the butterfly's halving): with depth² bonuses and a hard clamp, frequent pairs park on the ±2^20 rails - and a railed level-0 entry pollutes statScore with ±1M. The reference's O(1) gravity update fixes it; bench-invisible by design (node counts identical to within 5 nodes in 20.7M - short searches never saturate).

**Why the final, defect-free build still measures zero:** killers and the counter move occupy fixed hard bands (3.0M / 2.9M) ABOVE all history, so the multi-level signal can only reorder the tail of already-late quiets. The reference has no hard bands - everything is continuous history - which is what gives its continuation levels room to act. The revisit plan (pre-NNUE checkpoint): fold killers/counter into history-space bonuses first, then the already-built per-distance infrastructure (separate tables, gravity, depth gate) has somewhere to bite.

**Method lessons added to the golden rules:** node counts DO measure ordering, but only over 50–60 positions sampled from a real match PGN (a 4-position bench inverted the sign of every variant tested); wall-time-to-depth, not nodes, is the gate for any change that adds memory traffic; a per-color split must be judged against its MIRROR (both engines scored ~+0.05 with White - first-move advantage, not a black weakness).

## 2026-07-18 (v2.7.2) - block 5D (formerly 5F, renumbered to execution order): transposition table redesign (clustering + aging + cached eval + ttPv)

**SPRT vs v2.7.1 (tc 10+0.1, bounds elo0=0 elo1=10), two independent runs POOLED: +37.9 ± 15.0 Elo at 1103 games [0.554]** (own run +38.3 ± 20.9 H1 at 546g; user confirmation +37.6 ± 20.7 H1 at 557g - near-identical, both LOS 100%) - the largest search gain since the v2.3.0 overhaul. **Strength: ~2975 ± 25 CCRL** - LTC gauntlet (tc=60+0.6, 624 games, 13 anchors 2680–3200): **+48 ± 23 relative to the field, 56.8%** (vs v2.7.1's +44 - the field-relative LTC measure saturates between adjacent versions; the pooled SPRT carries the increment). Field audit: the Dumb 2856→2810 and Marvin 3000→2960 renames are VALIDATED (deviations −16/−56 vs the previous systematic −45/−35); **BitGenie-3010 on watch** (implied −130 this run after a clean previous cycle - single-run volatility, no rename); no further renames. **Bench profile: −19% nodes to depth (4.70M vs 5.81M), +24% NPS (768K vs 620K - the cached eval), WAC 265/300 (best ever; 262 baseline), Fine 70 zugzwang correct, KRK longest defense preserved, 184 tests green (7 new TT tests).**

After 5B and 5C proved that reference HEURISTIC constants do not transfer without their ecosystem (see the 5C post-mortem below), the TT block was pulled forward precisely because it is pure INFRASTRUCTURE - and it delivered (block letters renumbered to execution order: TT = 5D; double extensions and ProbCut/IIR shift to 5E/5F):

- tt: 4-entry clustering - the entry is packed to exactly 16 bytes (key32 verification half, int32 score, int32 cached static eval, move16, depth8, genBound8), so a 64-byte cache line holds a full 4-entry cluster: one memory access serves four candidate slots, and index collisions stop destroying useful entries. (The reference packs 3×10B in 32B by shrinking scores to int16; our ±100000 mate scale keeps int32 scores and gets a 4-wide cluster instead - no risky mate-score rescale.)
- tt: generation aging - every "go" bumps a 5-bit generation (32-cycle); replacement worth is `depth − 8×relative_age` (the reference formula), so stale entries from previous searches yield their slots gracefully instead of squatting. A probe hit refreshes the entry's generation.
- tt: cached static eval - a TT hit serves the stored eval without calling the evaluator, and a miss stores an eval-only entry (bound None, never cuts, never evicts real results, backfills the eval of in-check-stored twins) so the next visit - IIR revisits, re-searches - skips the evaluator too. This is where the +24% NPS comes from.
- tt: sticky ttPv flag - every node records "is or was on the PV" (PvNode || entry.IsPv), preserved across re-stores. **Deliberately consumer-less this release**: the reference's LMR ttPv −2 was measured in the 5C campaign at +220% PV-subtree explosion via a proxy; with the real flag now stored, that adjuster can be A/B-tested BY PLAY in a later block.
- tt: reference overwrite rule - a fresh Exact always replaces; a bound more than 4 plies shallower than the incumbent does not; a known best move survives moveless re-stores; the PV mark is sticky.
- verification: the 5C lesson applied - validated by GAMES at the real TC before handover (own SPRT above), not by benches; benches only corroborate.

## 2026-07-18 (block 5C: reference LMR adjuster suite + 4-component statScore) - MEASURED AND CUT, NO RELEASE

**Every 5C component measures NEGATIVE at the real time control. Cut per the project decision rule (like king-safety Fase B), search reverted to v2.7.1 exactly. The numbers, so this is never re-tried without its ecosystem:**

| Candidate | Content | vs v2.7.1 |
|---|---|---|
| Full reference bundle | 20.26·ln 1D base + delta/rootDelta + 8 adjusters + unclamped statScore/13628 | **−9.7 ± 13.8** (SPRT 10+0.1, H0 at 1252g) |
| Conservative rebuild | validated 2D base + 6 adjusters + clamped statScore, NPS-equal | **−25.7 ± 20.0** (SPRT 10+0.1, H0 at 597g) |
| V-a: adjusters alone | cutNode +1, ttCapture +1, moveCount>7 −1, cutoffCnt>3 +1, singularQuietLMR −1, threat escape −1 | **−11.5 ± 16.0** (1000g @ 5+0.05) |
| V-b: statScore machinery alone | 4-component statScore (contHist ply-2/ply-4 write fix) for RFP + futility reprieve | +17.4 ± 16.1 @ 5+0.05 **but −10.8 ± 14.3 at 10+0.1 (SPRT, H0 at 1218g)** - the hyperfast result did not survive the real TC |
| V-c: V-b + LMR statScore term | the reference's flagship `r −= statScore` consumer | **−6.9 ± 16.3** (1000g @ 5+0.05) |

**Lessons (added to the golden rules):**

- Depth benches and WAC CANNOT green-light a search change: the conservative rebuild had −23% nodes, WAC 263/300 (best profile ever measured) and equal NPS - and lost 25 Elo in play. Only games at the REAL time control validate search heuristics; hyperfast (5+0.05) matches can invert sign vs 10+0.1.
- The reference's LMR adjuster suite presupposes its ecosystem (reduce-from-move-2 including captures, TT static eval + ttPv flag, checking qsearch, its history-table dynamics). Ported onto our validated quiet-only LMR, every subset loses. Same class of failure as 5B's NMP bundle: the reference search suite does not transfer to this classical engine.
- The ply−2/ply−4 continuation-history contexts genuinely never existed (single-parity keys - found by a probe reading exact zeros) and the fix is implemented and archived, but with our depth² tables feeding pruning margins it measures −10.8 at STC: parked with its measurements until 5G reworks the history update rule (reference-style bonus/gravity), which is what makes those reads trustworthy.
- ttPv −2 via a PvNode proxy explodes the PV subtree +220% when stacked with the PvNode depth discount - the real thing needs the TT flag (5F).

The search was verified node-identical to v2.7.1 after the revert (5.81M depth bench, 177 tests, Fine 70, KRK defense); the freed v2.7.2 number went to the 5F TT redesign above.

## 2026-07-17 (v2.7.1) - block 5B: NMP verification + statScore-informed RFP (scope cut by measurement) + mate-search fixes

**SPRT vs v2.7.0 (tc 10+0.1, bounds elo0=0 elo1=10), two runs POOLED: +2.9 ± 7.4 Elo at 4347 games [0.504]** (run 1 stopped stable at 1398g [0.517] +11.8 ± 14.3; run 2 ran to H0 at 2949g [0.498] −1.3 ± 9.0; an A/B control between the two builds involved - with/without the mate-search fix below - scored [0.500] at 1743 games, proving both runs sampled the SAME engine strength, so the pooled figure is the honest STC estimate and run 1 was the high tail of the noise). (A first candidate with the full reference bundle FAILED at [0.451] / −34 Elo over 143 games and was dissected - see below.) **Strength: ~2970 ± 25 CCRL** - LTC gauntlet (tc=60+0.6, 624 games, 13 anchors 2680–3200): **+44 ± 23 relative to the field, 56.3%, vs v2.7.0's +43 on the identical field** (per the 5A lesson, search gains grow with TC - the STC SPRT understates block-5 features; the LTC gauntlet carries the quality signal). Field audit: no renames this cycle - every implied-Elo deviation sits inside the ±100 per-anchor noise; Marvin-3000 (−35 consistent) and Dumb-2856 (~−45) on watch. **Final build: WAC 262/300 vs v2.7.0's 259, depth-15 4-position node bench 2.92M vs 3.72M (−21%), startpos d16 2.25M vs 4.10M nodes (−45%), Fine 70 zugzwang correct.** Smaller tree at equal-or-better tactics.

**Mate-search fixes (found from an Arena game where NoaChess, lost, declined a queen capture that led to mated-in-8 and walked into a mated-in-4 instead):**

- search: iterative deepening no longer stops on a mate score. The old `if |score| > MateBound break` treated every mate as final - but when the engine is the one BEING mated, deeper iterations are exactly what finds longer defenses (the mated-in-8 rook ending needs 16 plies of search; the shallow iteration only saw the mated-in-4 and the search stopped there and played it). It also explains the "sheds all its pieces when lost" endgame behavior: every move re-searched shallow, stopped at first mate sighting, played the first defense on the list. The reference engine never breaks on mate scores - the clock ends the search. Verified: KRK defense now deepens past the first mate sighting (d8 → d22+) holding the longest defense; WAC 262/300 (was 258–259 - continuing past a found mate also finds SHORTER mates when winning); A/B SPRT with/without the fix: [0.500] at 1743 games - the extra clock spent in mate phases costs nothing at STC (adjudication ends those games), and in un-adjudicated real play (Lichess/Arena) the longest defense converts hopeless mates into 50-move/stalemate chances.
- uci: mate scores now go out as `score mate N` (moves, signed) instead of `score cp ±99xxx` - the UCI-mandated form; GUIs showed absurd centipawn evals in mate positions and adjudication could misread them.

What ships (on top of the untouched, validated NMP entry and R):

- search: statScore stack - `statScore[ply] = 2×butterfly + contHist − 1250` (reference `2×main + 3 contHist ctxs − 4433`, unit-rescaled ×0.28 by the MEASURED ratio between our gravity-less depth² tables and the reference's capped ones: butterfly p99 3218 / contHist p99 630 vs caps 14365/29952) recorded for the move that reaches each ply.
- search: RFP statScore term - the parent move's reputation leans on the margin: `staticEval − 85×(depth−improving) − statScore[ply−1]/180 >= beta`, plus the reference's `staticEval >= beta` guard. After a refuted (malus-heavy) parent move the static cut comes easier; after a high-history parent it needs headroom. This term carries real signal: it is the main source of the node reduction.
- search: NMP verification search at depth >= 14 - a null cutoff at high depth is re-proven by a real reduced search on the same position, with null moves disabled for the verifying side until `nmpMinPly = ply + 3(depth−R)/4` (reference nmpMinPly/nmpColor); zugzwang-proof pinned on Fine 70.
- search: NMP fail-soft - a passing null returns `nullScore` (bounded away from mate range) instead of the old hard `beta`; mate-range null scores still fall through to the real search (forced mates stay visible at their natural depth).
- search: improvement value - per-ply eval delta with the reference's ply−4 fallback after checks; the cold default stays STRICT (not improving), see lessons.
- eval: `Winnable.Apply` overload reports the position complexity (initiative magnitude, cp, >= 0) via `IComplexityEvaluator` - plumbing kept for the 5H time-management complexity factor.

**Deferred by measurement - the reference NMP presumes three ecosystem pieces we don't have yet.** The full reference bundle (entry gated on `staticEval >= beta − 10d − improvement/13 + 112 + complexity/25`, statScore skip, deep `R = min((eval−beta)/81,7) + depth/3 + 4`, capture futility, lmrDepth quiet futility) was implemented faithfully, unit-rescaled and bisected against WAC-300 + node benches across seven builds:

1. **Deep R needs a checking quiescence.** The reference's null probes bottom out in qsearch from depth 3–7; ITS qsearch generates CHECKS at the first ply, ours is captures-only - our null-passed positions can't see quiet mate threats (WAC 249/300; the WAC.001 mate went from d13 to invisible past d17/100M nodes; verification onset at 8 neither recovered tactics nor kept the nodes). → revisit after adding qsearch checks.
2. **Eval-gated entry needs an accurate eval.** Gating NMP on `staticEval >= beta` grew the tree ~30% at equal tactics: our classical eval is noisy relative to the search, so probes at eval-below-beta nodes keep finding real cutoffs the gate forbids. → revisit with NNUE.
3. **lmrDepth-scaled futility needs the reference's larger reductions** (its lmrDepth runs systematically lower) - and pruning margins do NOT take the ×0.48 value rescale: the RAW reference margins reproduce our validated shallow margins almost exactly (d3: 251 vs 300, d4: 396 vs 400); the ×0.48 ones pruned double and blinded the tactics. → 5C.
4. Capture futility without a gives-check test prunes sacrificial checking captures (−6 WAC); its reference form also needs captureHistory. → 5G.

- verification: 138 tests green; every failed variant documented in the bisection (full bundle 249 WAC / fastest; old-R variants 251–257 WAC / +36% nodes; final assembly strictly dominates the baseline profile).

## 2026-07-16 (v2.7.0) - block 5A: improving flag

**SPRT vs v2.6.9 (tc 10+0.1, bounds elo0=0 elo1=10): +4.0 ± 27.1 Elo at 380 games [0.507], LOS 61.3%, stopped manually (LLR hovering at 0 - real but small STC gain).** **Strength: ~2965 ± 25 CCRL measured** - LTC gauntlet (tc=60+0.6, 624 games, 13 anchors): **+43 ± 23 relative to the field vs the +16 ± 23 of v2.6.9 on the IDENTICAL field and TC - the gain GROWS at LTC (+27 ± 32 relative between versions)**. The opposite pattern to eval terms (which shrink at LTC): pruning/reduction accuracy compounds with depth, so search features are worth more the longer the time control. First version measured above the 2941–2944 plateau of v2.6.8/v2.6.9.

**Field audit (three-gauntlet cross-check, 216 games per anchor):** per-anchor implied-NoaChess deviations consistent across the v2.6.8/v2.6.9/v2.7.0 runs expose three mislabeled engines, renamed to measured strength: **Ethereal 2756 → 2910** (deviations −186/−125/−154: plays ~150 above its label), **Inanis 2997 → 2905** (+63/+58/+193), **Bit-Genie 3101 → 3010** (+84/+79/+126). **Meltdown-2817 cleared** (−10/−11/+5 - one of the cleanest anchors in the field). Marvin-3000 (~−65) and Winter-3200 (~−50) on watch. The corrected field barely moves the centroid (2923.8 → 2921.5): the renames nearly cancel.

Block 5A opens the search block: the reference `improving` flag - a single boolean, computed once per node, that modulates three pruning/reduction heuristics simultaneously. `improving = staticEval[ply] > staticEval[ply-2]` (same side two plies earlier; false when either node was in check, tracked via a per-ply eval stack with a NoEval sentinel).

- search: LMR - quiet moves in a worsening position are reduced one extra ply (`if (!improving) reduction++`); the single highest-impact use of the flag in the reference.
- search: reverse futility pruning - the margin becomes `85 × (depth − improving)`: an improving eval is trusted one depth-step sooner (reference formula shape `165 × (depth − improving)`, ours already at the ×0.48-equivalent 85/ply).
- search: late move pruning - the quiet-move count threshold `3 + depth²` is halved when not improving (reference LMP shape): in a worsening position late quiet moves almost never rescue the node.
- search: move-loop futility pruning and NMP deliberately untouched - the refined NMP entry condition (which also consumes the flag) is 5B scope.
- tests: 137 green (no eval changes - bench positions unaffected).

## 2026-07-16 (v2.6.9) - block 4I: winnable / endgame scale factors

**SPRT vs v2.6.8 (tc 10+0.1, bounds elo0=0 elo1=10): +34.3 ± 19.5 Elo, LOS 100.0%, H1 accepted at 580 games [0.549], DrawRatio 52.6%.** **Strength: ~2941 ± 25 CCRL measured** - LTC gauntlet (tc=60+0.6, 624 games, 13 anchors 2680–3200; +16 ±23 relative, absolute from the pool centroid equation). Statistically the same absolute anchor as v2.6.8 (2944 ±15): the STC gain shrinks at LTC into the error bars, the project's known pattern - the SPRT carries the reliable relative signal.

Block 4I: the reference `winnable()` correction plus the material-entry drawish factor - the final score is adjusted for positions that are structurally harder or easier to win than the raw eval claims. Applied to the total White-relative score right before the phase interpolation.

- eval: complexity/initiative - `9×passers + 12×pawns + 9×outflanking + 21×pawnsOnBothFlanks + 24×infiltration + 51×purePawnEnding − 43×almostUnwinnable − 110`, computed in raw reference internal units and converted ×0.48 once (the mg/eg caps are NoaChess centipawns). The adjustment can only shrink the midgame component, can push the endgame component either way, and never flips the sign of either (`u = sign(mg)·clamp(complexity+50, −|mg|, 0)`, `v = sign(eg)·max(complexity, −|eg|)`). `almostUnwinnable` = kings crossed past each other (outflanking < 0) with every pawn on one flank.
- eval: endgame scale factor - the eg half of the tapered blend is multiplied by sf/64. Material-configuration factor first (material.cpp): a side with no pawns and at most a bishop of extra material rarely wins - sf=0 below a rook in total (KK, KBK, KNK dead draws), sf=4 against a bare minor (KRKB, KRKN), sf=14 otherwise (KmmKm and friends). If no specific factor applies, general heuristics (evaluate.cpp `winnable()`): pure opposite-colored bishops `18 + 4×strongPassers`; OCB with more material `22 + 3×strongUnits`; single-rook endgames with ≤1 pawn of advantage, the strong pawns on one flank and the weak king defending its pawns → 36; queen vs no queen `37 + 3×queenlessMinors`; everything else capped at `36 + 7×strongPawns` (−4 more on a single flank); and a final −4 on every branch when all pawns sit on one flank. Scale factors are dimensionless ratios - deliberately NOT ×0.48-rescaled.
- eval: specialized endgame functions (KXK, KBPsK, KQKRPs, KPsK, KPKP, KNNK...) are NOT ported - out of 4I scope; Syzygy (block 9) covers exact endgames later.
- perf: no cache needed - a handful of popcounts once per Evaluate; depth-16 wall time unchanged (1.23s vs 1.22s).
- time/uci: ponderhit time credit - the ponderhit relaunch used to start a FRESH timed search with the full budget, ignoring everything already pondered: with Permanent Brain on, every move paid ponder time AND a complete optimum on top (observed on Lichess: 30s thinks on near-forced replies, never an instant answer, clocks bleeding vs instant-moving bots). The reference anchors its clock at "go ponder" so pondering counts toward the budget; now the relaunch carries an `ElapsedOffsetMs` charged against every soft/hard check (floored to leave 100ms of hard budget - one warm-TT iteration reproduces the pondered move). Verified over the wire: 6s ponder → bestmove 30ms after ponderhit (was ~4s). Invisible to SPRT/gauntlets (cutechess plays ponder-off) - pure gain in ponder-on play (Lichess, Arena).
- tests: WinnableTests - every scale-factor branch pinned by hand (KBK=0, KRKB=4, KRBKR=14, pure OCB 18+4×passers, mixed OCB 22+3×units, rook ending 32, queen-vs-minors 43, default cap 57), complexity+interpolation pipeline pinned end-to-end on two hand-computed positions, KBK near-draw and color-symmetry checks; ElapsedOffset defaults + consumed-budget instant-answer pinned - 137 tests green.

## 2026-07-16 (v2.6.8) - 4H material-imbalance polynomial + joint material retune + bullet sustainability guard

**SPRT vs v2.6.7.1 (tc 10+0.1, bounds elo0=0 elo1=10): +78.4 ± 31.5 Elo, LOS 100.0%, H1 accepted at 284 games [0.611], DrawRatio 40.5%.** **Strength: ~2944 ± 15 CCRL** - LTC gauntlet (tc=60+0.6, 1560 games, 13 clean anchors 2680–3200; NoaChess +19 ±15 relative to the field, absolute Elo solved from the pool centroid equation).

Block 4H: Tord Romstad's second-degree material-imbalance polynomial, with joint texel retune of the piece values to eliminate the double-counting that caused the two previous failed attempts.

- eval: `MaterialImbalance` - second-degree polynomial (material.cpp `imbalance()`): scores every PAIR of pieces - own-piece synergies (`QuadraticOurs`: knights gain with own pawns, second rook worth less, queen+rook redundant) and enemy interactions (`QuadraticTheirs`: queen strong vs rooks, knight good vs many pawns). Bishop pair = "extended piece" at index 0; its diagonal entry `[0][0]` is zeroed in both Ours/Eg tables: the standalone texel-tuned `BishopPair` term owns the pair's intrinsic value and removing it cost −30 Elo in the first attempt, so the polynomial owns only the pair's INTERACTIONS with the rest of the material. Tables in raw reference units; reference /16 then ×0.48 → combined factor ×3/100 at output. Pure White−Black difference: exactly zero for symmetric material, so no re-centering of the tables.
- eval: joint material retune - piece values (MaterialMg/Eg) and BishopPair were texel-retuned WITH the polynomial active, using a single equal mg/eg offset per piece to prevent the degenerate free direction (tuning mg/eg independently on near-symmetric positions drove queen to 1841/664). Converged offsets over PeSTO: N+20, B+34, R+126, Q+223; BishopPair S(44,68) → S(67,110). The tuner moved the average synergies that had been absorbed into the piece values back out, leaving the polynomial to contribute only the context-dependent deviation.
- perf: per-instance direct-mapped cache (8192 slots) keyed by the packed ten piece counts via Fibonacci hash; counts only change on captures and promotions, so the full polynomial runs only on a miss (~2.4% NPS cost measured on an identical-tree control build).
- time: sustainability guard (sudden-death branch only) - the soft target is bounded by `inc + clock/16` and the hard deadline by `inc + clock/4 - overhead`. Healthy clocks are untouched; in time trouble the spend converges to the increment (2+1 with 5s left: hard deadline 3.96s → 2.22s). Fixes the bullet death spiral where NoaChess lost won positions on time.
- time: the movestogo branch (classical 40/900-style controls) is deliberately NOT touched - the CCRL-rate behavior is validated as-is.
- tests: MaterialImbalanceTests (symmetric=0, hand-computed knight-with-pawns, bishop-pair diagonal zeroed, mirrored position negation, cache consistency); SustainabilityGuard pinned in both directions - 117 tests green.

## 2026-07-14 (v2.6.7.1) - time-management patch: opening overspend + UCI robustness

**SPRT vs v2.6.7 (tc 10+0.1, non-regression bounds [-5, 5]): +14.3 ± 13.5 Elo, LOS 98.1%, H1 accepted, DrawRatio 44.1%** - the patch not only doesn't regress, it gains. **Strength: ~2920 ± 20 CCRL** - confirmed at the exact CCRL list TC (tc=40/900 round-robin, 2026-07-15; 4 self-consistent anchors Meltdown-2817/Colossus-2862/Tcheran-2917/Pedone-2978, implied 2917–2927, mean 2922), superseding the first ~2890 ± 25 estimate from the tc=60+0.6 gauntlet. A 37-engine verification round-robin at tc=30+0.3 anchors ~2900 at that faster rate, consistent within error. Field audit: **KnightX-2932 EXCLUDED going forward** - three consecutive gauntlets anchor NoaChess 60–130 above every other opponent (2953 → 2970 → 3021, drifting), so its label is wrong (~2830 real). Pedone-2978 anchors low twice in a row (2841 → 2811, plays ~3050 real?) - on watch, one more run decides. Patch release targeting two Arena-observed problems: heavy clock use in the opening at short TC (1+0, 3+2) and a frozen engine after starting a new game (Ctrl+N + DEMO) without restarting it. No evaluation changes.

- time: opening damp - `optScale ×= min(1.0, 0.55 + gamePly·0.025)` in the sudden-death branch (fades out by ply 18/move 10). The reference formula folds the whole future increment into the usable time (inc × 49 over the horizon), which at 3+2 budgeted ~7.5s optimum for the first moves (~19s once the dynamic factors extended it), starving the middlegame. Without an opening book the first moves are the cheapest of the game.
- time: neutral first-move dynamic factors - on the first search of a game (no cross-move history) `fallingEval` was deliberately maxed at 1.5 and `bestMoveInstability` could double the budget because a cold TT flaps the root between near-equal openings; both are now 1.0 exactly once. Measured first move at 3+2: 19s → 6.1s; at 1+0: 1.2s.
- uci: ponder/infinite protocol fix - a "go ponder" / "go infinite" search that finished on its own leaked its "bestmove" while the GUI still considered the search pending, which UCI forbids (fires at the end of nearly every game: pondered positions hold forced mates, and a mate score breaks iterative deepening in milliseconds). Now a self-terminated ponder/infinite search parks on the cancellation handle and only answers when the GUI resolves it ("stop" -> bestmove; "ponderhit"/new position -> suppressed). Verified over the full protocol cycle.
- uci: THE Arena freeze root cause, found via traffic log - Arena's Permanent Brain stalls its whole game controller when a "bestmove" arrives WITHOUT a ponder hint: it waits forever for the ponder position, the engine's clock runs down to a time loss, and not even Ctrl+N recovers (Arena re-sends the setoptions and then nothing) until the engine process is restarted. NoaChess omitted the hint whenever a soft-stopped partial iteration improved past the last completed PV (the returned best move no longer matched the PV head). Now every bestmove carries a ponder hint: the PV reply when available, otherwise any legal reply - a wrong prediction is harmless (ponder miss = stop -> discard -> fresh go), a missing one froze Arena. (Thread-stack forensics on a frozen instance had already shown NoaChess healthy and idle in ReadLine() - the GUI was the side that stopped talking.)
- uci: "Debug Log File" option (+ NOACHESS_DEBUG_LOG env var) - timestamped log of every GUI->engine line ("<<"), engine->GUI line (">>"), stdin EOF and the internal search-wait/park/suppress transitions. This is what pinned the freeze: the log showed the exact bare bestmove after which Arena went silent for 96 seconds.
- uci: zombie hardening - a faulted search task re-threw its exception inside `WaitForSearchToFinish` on the UCI loop thread, killing the read loop and leaving the process alive but deaf. The wait now swallows the already-reported fault; `RunSearch` itself never lets an exception escape (reports `info string` and answers with a legal fallback move so the GUI never hangs waiting for `bestmove`).
- uci: one bad command (e.g. a malformed FEN) no longer kills the read loop - reported as `info string`, loop keeps serving.
- tests: opening damp pinned (first-move soft budget < 3% of a 3+2 clock, damp fades by ply 18) - 154 tests green.

## 2026-07-14 (v2.6.7) - block 4G: reference pawn-structure scoring chain

**SPRT vs v2.6.6 (tc 10+0.1): +28.4 ± 17.5 Elo, LOS 99.9%, H1 accepted, DrawRatio 41.7%.** **Strength: 2895 ± 25 CCRL estimated** - LTC gauntlet (tc=60+0.6, 448 games, 8 clean anchors 2688–2978; per-opponent anchored estimates 2841–2970, mean 2894/median 2893). Ethereal-2901: one game ended with an illegal king move in a 3-fold repetition (Ethereal bug, not a crash - 55/56 normal games). KnightX-4.8 and Pedone-1.5 are the high/low statistical outliers (noted since v2.6.6); all 8 anchors are within their ±76–87 Elo individual error margins and remain in the field. The remaining reference pawn-cache terms (pawns.cpp `evaluate()`), replacing the old additive per-file Doubled / Isolated / Phalanx / Backward model with the reference's chain of mutually exclusive branches (a pawn is either connected, isolated or backward - plus the unsupported-pawn and blocked-pawn add-ons). All values ×0.48.

- eval: full Connected formula - a supported and/or phalanx pawn scores `v = Connected[r] × (2 + phalanx − opposed) + 22 × popcount(support)` with `eg = v×(r−2)/4`, computed in raw reference units (Connected = {0,5,7,11,23,48,87}) and converted ×0.48 at the end. Replaces the simple rank-indexed Phalanx array: the formula also pays attention to whether the pawn is opposed (an opposed chain is worth less) and to how many direct supporters it has, and its endgame half only kicks in from relative rank 3 up.
- eval: WeakUnopposed S(7,9) - an isolated or backward pawn with a free file in front is a permanent rook target that can never be traded forward; added on top of Isolated/Backward (the backward case only off the rook files, per the reference).
- eval: WeakLever S(1,27) - an unsupported pawn attacked by two enemy pawns loses the pawn exchange on either recapture.
- eval: DoubledEarly S(8,3) - extra penalty for a doubled pawn while NO enemy pawn is fixed yet (no own pawn rams or restrains them): early doubling is a real weakness, doubling into a locked structure can be a legitimate byproduct of a capture toward the center.
- eval: BlockedPawn ranks 5-6 - {S(-9,-4), S(-3,1)}: a rammed pawn deep in the enemy camp cramps the defense (turns into a small endgame plus on rank 6).
- eval: reference Doubled semantics S(5,25) - own pawn DIRECTLY behind on the same file and no support (the old model penalized every extra pawn per file regardless of support); trebled isolated pawns behind an own pawn on an enemy-free file pay Doubled instead of Isolated.
- eval: reference Isolated S(0,10) / Backward S(3,9) replace the texel-tuned IsolatedPawn/BackwardPawn (the branch structure changed around them, so the old tuned values no longer describe the same events).
- tuner: Doubled, DoubledEarly, Isolated, Backward, WeakLever, WeakUnopposed, BlockedPawnRank registered; DoubledPawn/IsolatedPawn/BackwardPawn/Phalanx removed. Connected[] stays fixed (raw reference units consumed by a formula - tuning the entries independently of the multiplier breaks the shape).
- perf: all in the pawn cache - NPS unaffected (879k vs 613k at depth 16 startpos, machine-state noise aside no regression).
- tests: PawnChainTermsTests (WeakLever, WeakUnopposed, DoubledEarly on/off with a fixed enemy pawn, BlockedPawn rank 5, connected-vs-loose), Phalanx/Backward tests re-pinned to the new chain - 153 tests green.

## 2026-07-14 (v2.6.6) - block 4F: reference passed pawns

**SPRT vs v2.6.5 (tc 10+0.1): +45.8 ± 23.1 Elo, LOS 100%, H1 accepted, DrawRatio 39.0%.** **Strength: 2880 ± 25 CCRL estimated** - LTC gauntlet (tc=60+0.6, 450 games, 9 clean anchors 2688–3027; anchored estimates 2852–2953 across 8 reliable opponents, mean 2886/median 2881). Patricia-3027 confirmed outlier excluded (anchors NoaChess at 2764, implying Patricia plays ~3290 real; behavior normal, label wrong - permanently added to exclusion list alongside Counter 3.8, Mr Bob 0.9.0, Tucano 8.00, Meltdown 1.10, Minic 1.09). The five missing reference passed-pawn terms (evaluate.cpp `passed()` + the pawns.cpp passed definition), replacing the plain cone-mask test and the simple enemy-on-stop penalty.

- eval: reference passed definition - a pawn is passed when (a) the only stoppers are levers (enemy pawns we can capture right now), OR (b) the only stoppers are lever-pushes and our phalanx outnumbers them, OR (c) the only stopper is the direct blocker, the pawn is on relative rank 5+, and a supporting pawn can safely step up to offer the freeing trade (candidate passer). A pawn behind an own pawn on the same file is never passed. Computed in the pawn cache (pawn-only inputs).
- eval: piece-aware blocked-passer filter (second pass) - a candidate blocked by an enemy pawn only keeps its bonus if a friendly pawn one step behind an adjacent file can step up safely (push square empty, not doubly attacked unless defended); otherwise the rank bonus the pawn cache granted is taken back (equivalent to the reference dropping the pawn from the passed loop).
- eval: king proximity to the block square - the passer's endgame value grows with the enemy king's Chebyshev distance (min 5) to the square in front (`x19/4 x w`) and shrinks with our own king's distance (`x2 x w`), plus second-push coverage when the block square is not the queening square (`w = 5*rank - 13`, ranks 4+).
- eval: 6-level path-to-queen safety ladder, only when the pawn can step forward: k=36 if the whole 3-file forward span has no enemy presence, 30 if all of it is covered by our pawns, 17 if the pawn's own file to queen is clean, 7 if only the block square is safe, 0 otherwise; +5 when the block square is defended or an own rook/queen pushes from behind; an enemy rook/queen behind the passer contests the entire span regardless of distance. Applied `(k*w, k*w)` in reference units, converted x0.48 per pawn.
- eval: PassedFile - S(6,4) penalty per file distance from the edge (reference S(13,8) x0.48): flank passers are stronger than central ones.
- eval: the old simple blocked-passer penalty (a third of the rank bonus back when ANY enemy piece sits on the stop square) removed - superseded by the ladder (k=0 covers a piece-blocked path) and the filter. `BlockedPasserDivisor` deleted; NoaChess's Tarrasch RookBehindPasser kept (texel-tuned, complements the reference k+5).
- tuner: PassedFile registered in the parameter registry.
- perf: NPS unchanged (613k vs 598k at depth 16 startpos - the definition lives in the cached pawn eval, the piece-aware terms visit only actual passers).
- tests: PassedPawnTermsTests (king proximity both signs, free vs rook-guarded path, own vs enemy rook behind, PassedFile edge > central, blocked ram with/without helper) - 148 tests green.

## 2026-07-13 (v2.6.5) - block 4E: reference piece terms + full reference time manager

**SPRT vs v2.6.4 (tc 10+0.1): +19.5 ± 13.6 Elo, LOS 99.7%, H1 accepted, DrawRatio 40.5%.** **Strength: 2835 ± 25 CCRL measured**, two LTC gauntlets (tc=60+0.6, 880 clean games pooled, 10 reliable anchors 2688–3027; per-opponent anchored estimates 2767–2966, mean 2842/median 2840). Four mislabeled/broken opponents excluded from the first run (Counter 3.8, Mr Bob 0.9.0, Tucano 8.00 play 300–500 above their labels; the Meltdown 1.10 exe plays ~600 below) plus Minic 1.09 (anchored ~2600 in BOTH runs - its label 2830 is wrong). The apparent −40 vs v2.6.4's 2875 is a field re-anchoring artifact, not a regression: the direct SPRT (+19.5) is the reliable relative signal, and the v2.6.4 figure was measured on a different, likely slightly optimistic field. Two packages: (1) the eleven reference piece-specific evaluation terms (evaluate.cpp `pieces<>()`), rescaled ×0.48 per the standing scale rule, with the outpost machinery now REFERENCE-EXACT (the first 4E attempt regressed in the wide gauntlet: −167 vs −159 relative Elo); (2) a full port of the reference time manager (timeman.cpp + the search-side dynamic stop factors), replacing the v2.6.4 fixed-slice scheduler.

Piece terms (4E):

- eval: TrappedRook - a rook with ≤3 mobility squares, on a file with an own pawn (not (semi-)open), boxed in on the same side as its own king (`(kf<E)==(rookFile<kf)`), penalized and doubled when the side has already lost its castling rights. Reference geometry, NOT a home-rank heuristic (that early cut wrongly penalized rooks on open files and regressed −1.99 llr @ 200 games).
- eval: RookOnClosedFile - penalty for a rook on a file whose own pawn is blocked (a piece directly in front of it), applied only in the non-(semi-)open branch.
- eval: BishopPawns - penalty per own pawn on the bishop's color, indexed by the bishop file's edge distance (BishopPawns[4] ×0.48) and scaled by (not pawn-protected + own pawns blocked on the center files). Hemmed-in "bad bishops" now cost material honestly.
- eval: BishopXRayPawns - penalty per enemy pawn on the bishop's empty-board diagonals (x-ray): they restrict its scope.
- eval: LongDiagonalBishop - bonus when a bishop sees ≥2 of the four center squares (d4/d5/e4/e5) through pawns; it dominates the long diagonal.
- eval: KingProtector - DISABLED (zeroed). On top of PeSTO PSTs it double-counts king distance and its Eg component cancels the outpost bonuses; it collapsed play at long TC in the 2.6.5 gauntlet. Do not re-enable without an SPRT that proves it.
- eval: MinorBehindPawn - bonus when a bishop or knight has a pawn (either color) directly in front of it (the pawn shields it / it blockades).
- eval: WeakQueen - penalty when the queen is the single blocker between an enemy rook/bishop and a target behind it (relative pin / latent discovered attack), using the same sniper/Between logic as king-pin detection.
- eval: outposts REWRITTEN reference-exact (the fix over the first 4E attempt). Outpost squares are now `outpostRanks & (ownPawnAttacks | pawnShield) & ~enemyPawnAttacksSpan`: (a) the pawn-attacks-span excludes BLOCKED and BACKWARD enemy pawns (they can never advance to evict a piece - the first attempt treated every enemy pawn in the cone as an evictor and granted far fewer outposts than the reference); (b) a square with any pawn directly in front qualifies even without own-pawn protection (shield alternative); (c) the whole outpost chain (KnightOutpost / BishopOutpost / UncontestedOutpost / knight-only ReachableOutpost) moved INTO the piece loop and consumes the real per-piece attack bitboard (x-ray through queens, pin-restricted) exactly like the reference - the old second-pass recomputed plain attacks.
- eval: UncontestedOutpost - for a knight on a FLANK outpost (files a/b/g/h) with no attacks on enemy non-pawn pieces and ≤1 enemy piece on its wing, replaces the normal outpost bonus with per-wing-pawn endgame value (reference `else if` chain, not an additive bonus).
- eval: KnightOutpost keeps the texel-tuned S(51,18) (halving it to the generic ×0.48 measurably lost Elo in the 2.6.5 runs); BishopOutpost scaled by the same tuned-to-reference ratio → S(29,13).
- perf: outpost squares and the pawn-attacks-span depend only on pawns, so they are computed inside the pawn-hash cache (PawnStructureEvaluator) and are nearly free per eval call.

Time management (reference port, replaces the v2.6.4 scheduler):

- time: TimeManager is now the reference `TimeManagement::init` verbatim - `optimumTime`/`maximumTime` from `optScale`/`maxScale`, both time-control shapes: sudden death (`optScale = min(0.0120 + (ply+3)^0.45 · 0.0039, 0.2·time/timeLeft) · optExtra`, `maxScale = min(7, 4 + ply/12)`) and movestogo (`optScale = min((0.88 + ply/116.4)/mtg, 0.88·time/timeLeft)`, `maxScale = min(6.3, 1.5 + 0.11·mtg)`), with `timeLeft = time + inc·(mtg−1) − overhead·(2+mtg)` folding the WHOLE increment over the horizon (the flat 85% share is gone) and `maximum ≤ 0.8·clock`.
- time: search-side dynamic stop - after every completed iteration the optimum is re-modulated: `totalTime = optimum × fallingEval × reduction × bestMoveInstability`. `fallingEval` (0.5–1.5) extends the think when the score drops vs the previous move's average and the 4-iterations-ago score (score deltas rescaled ×2.08 to reference internal units); `reduction` halves the budget when the best move has been stable for 10 iterations (`timeReduction` 1.37/0.65, carried across moves via `previousTimeReduction`); `bestMoveInstability = 1 + 1.7 × totBestMoveChanges` (root best-move changes, halved each iteration) extends it when the root flaps. The v2.6.4 revert note is obsolete: the failed attempt multiplied the RAW slice by instability only; the reference formula's stable state is ~0.5×optimum, so extensions start from a much lower base.
- time: the graceful root-boundary stop now uses the dynamically modulated deadline, and a HARD abort mid-iteration keeps the partial iteration's best move when one exists (it is at least as good as the previous iteration's answer - same argument as the soft stop; the reference keeps partial root improvements the same way).
- time: cross-move scheduler state (previous score, average score, previousTimeReduction) lives in AlphaBetaSearch and resets on `ucinewgame`.
- time: fixes the Arena 40/2h first-move anomaly - the old scheduler allocated `clock/25` soft (~4.8 min) with a hard cap of ~19 min for move 1 (the profile's fixed `AssumedMovesToGo = 25` silently overrode the v2.6.4 adaptive horizon - it had been dead code); now move 1 targets ~2.2% of the clock (×1.5 first-move factor, capped by `maxScale`), and bullet low-clock behavior is bounded by the 0.8·clock ceiling.
- time: MoveOverhead default 100 → 30 ms. The reference formula reserves `overhead × (mtg+2)` (≈ ×52) from the usable time: 100 ms reserved 5.2 s and collapsed bullet endgames under a 5 s clock to instant moves.
- uci: EngineProfile.AssumedMovesToGo removed (obsolete - the ply curve replaces it).
- search fix: the extreme fallback (search cancelled before even depth 1 completes - a cold process on a tiny first-move budget) now returns the STATIC-BEST move (a one-ply eval over the legal moves) instead of the first generated move. Move ordering made the first move a rook-pawn push, so a cold engine forced to move instantly could play …a6/a3.
- tests: PieceTermsTests (TrappedRook, LongDiagonalBishop, KingProtector, MinorBehindPawn, BishopPawns, outposts, WeakQueen), EvalSymmetryTests (mirror-FEN color symmetry), CancelledBeforeDepthOne fallback, TimeManagerTests re-pinned to the reference contract (141 tests green).

## 2026-07-11 (v2.6.4) - time management: use the increment, adaptive horizon

**SPRT vs v2.6.3 (tc 10+0.1): no completed SPRT** (first attempt regressed −5.7 ±11.8 Elo; conservative final design was not retested at fast TC). **Strength: 2875 ± 20 CCRL measured**, LTC gauntlet (tc=60+0.6, 2728 games, 11 rivals rated 2580–2917; per-opponent anchored estimates 2847–2899 across 9 reliable opponents, excluding Pedantic-2888 and Minic-2869 as outliers). The +75 jump from v2.6.3 (2800) reflects better increment use at tc=60+0.6: 85% of 0.6s over ~40 moves adds ~24s of usable time per game vs the old 50% share. Free strength - no eval/search knowledge added, only better use of the clock.

- time: increment spent at 85% instead of 50% (`incrementMs * 85 / 100`). Folding most of the increment into the per-move budget is the main win: v2.6.3 banked half of every increment for no reason and finished games with ~1:50 unused on a 2+6 clock.
- time: adaptive horizon - the assumed remaining-move count follows a ply-scaled curve (`clamp(52 - pow(gamePly+3, 0.45)*2.2, 38, 52)`) instead of a fixed 25. Early in the game the clock is assumed to cover many moves (a small per-move slice on booked/simple openings); by the middlegame the horizon shrinks toward ~38, spending a slightly larger slice where the decisions matter. The divisor is deliberately conservative (~48 opening → ~38 middlegame) so the per-move budget stays a small fraction of the clock - matching what a strong engine's optimum formula produces (~2% of the clock in the opening). The game ply is derived in UciLoop from the board (2*(FullmoveNumber-1) + side).
- tests: TimeManagerTests - soft<hard<clock ordering, 85% increment share, adaptive horizon (middlegame > opening), near-exhausted clock never throws (Min/Max not Clamp), movestogo tightening (79 tests green).
- perf: no evaluation or node-count change.

**Design note - best-move instability extension tried and REVERTED.** The first cut of v2.6.4 also scaled the soft budget by a best-move-instability factor (`1 + 1.7*totBestMoveChanges`) plus a falling-eval factor, and dropped the predictive soft cut. It **regressed −5.7 ±11.8 Elo (H0 accepted, LOS 17%)** and, in bullet, spent up to ~16s on the first move of a 2+1 game: without an eval-complexity metric the instability factor fires hardest in the volatile opening (where any reasonable move is fine), multiplying an already-large base by 3-4x, burning the clock early and rushing the rest of the game. Removed. The instability / falling-eval / complexity time factors belong with the later search block that also ports the complexity signal, not here.

## 2026-07-11 (v2.6.3) - block 4D: shelter/storm + full king safety

**SPRT vs v2.6.2 (tc 10+0.1): +76.9 ± 31.2 Elo, LOS 100%, H1 accepted in 335 games, score 132-97-106 [55.2%]** - the largest single evaluation gain of the project since threats (+103). Well above the +15–30 estimate; king safety was a bigger gap than anticipated.

**Strength: 2800 ± 25 CCRL measured**, LTC gauntlet (tc=60+0.6, 420 games, 8 rivals rated 2780–2917; per-opponent anchored calculation excluding confirmed outlier Leorik-2780). Individual anchored estimates: 2761–2837 across the 7 most reliable opponents, mean ~2807; rounded conservatively to 2800.

- eval: full the reference engine king safety replaces the simple attack-units + pawn-shield scheme. The whole system is computed in RAW internal units (the danger formula, the quadratic transform danger^2/4096 and every table are jointly tuned) and converted to NoaChess centipawns (x0.48) once at the end. No re-centering needed: each side has exactly one king, so constant offsets cancel in the White-minus-Black subtraction.
- eval: shelter/storm (pawns.cpp evaluate_shelter) - ShelterStrength[4][8] per file distance from edge and pawn rank, UnblockedStorm[4][8] for enemy storm pawns, BlockedStorm when our pawn blocks theirs, KingOnFile[ourSemiOpen][theirSemiOpen], computed on the king file and both adjacent files.
- eval: pre-castling shelter (pawns.cpp do_king_safety) - while castling rights remain, the shelter takes the maximum (by MG value) of the current king square and the post-castling squares (g1/c1 relative), so the engine stops fearing phantom attacks on a king that can still castle away.
- eval: endgame king-pawn proximity - shelter minus (0, 16 x Chebyshev distance to the closest own pawn); the king must shepherd its pawns once the danger fades.
- eval: king ring - king attacks of the king square clamped to files b-g / ranks 2-7 plus the square itself, minus squares defended by two own pawns; enemy pawn attacks on the ring seed the attacker count.
- eval: king danger formula (all terms EXCEPT safe/unsafe checks - the v2.4.6 failure was a safe-check mask bug; they remain a possible future sub-block): attackersCount x attackersWeight (weights 76/46/45/14), 183 x weak ring squares, 98 x blockers for the king, 69 x attacks adjacent to the king, king flank attack^2 term, MG mobility difference, -873 when the attacker has no queen, -100 for a knight defender next to the king, shelter feedback, flank defense, +37 bias; penalty (danger^2/4096, danger/16) when danger > 100.
- eval: king flank terms - PawnlessFlank (19,97) when no pawns of either color live on the king's flank; FlankAttacks (8,0) per enemy attack (double attacks counted twice) on the flank inside our camp.
- eval: RookOnKingRing / BishopOnKingRing (stored x0.48: (8,0)/(12,0)) in the piece loop for rooks/bishops aimed at the enemy king ring without directly attacking it.
- perf: shelter cache - direct-mapped 16K-entry table keyed by pawn Zobrist key + king square + own castling rights + color (a strong engine caches king safety in its pawn hash entry and only recomputes when the king moves, ~20% of calls).
- tests: KingSafetyTests (shelter delta, storm delta, pawnless flank sanity, king-ring danger, no-queen discount, pre-castling rights >= no rights, mirror symmetry) - 74 tests green. The color-symmetry fuzz now mirrors castling rights too (the eval reads them since this version).
- bench: fixed-node per-node time +16% vs v2.6.2 in the search bench - partly a tree-shape artifact (the same bench overstated 4C by ~35% in the opposite direction); the SPRT arbitrates the real cost/benefit.

## 2026-07-11 (v2.6.2) - block 4C: non-linear mobility, x-ray attacks, reference mobility area

**SPRT vs v2.6.1 (tc 10+0.1): +6.6 ± 11.5 Elo, LOS 87%, 2000 games (bounds not reached)** - kept: likely positive, no regression risk, and the 4C infrastructure (blockers, pins, x-rays, reference mobility area) is a prerequisite for blocks 4D/4E anyway. Smaller than the 4B jump by nature: it replaces an already SPRT-validated linear mobility term rather than filling a gap.

**Strength: 2780 ± 20 CCRL measured**, confirmed by two independent LTC gauntlets at tc=60+0.6: a 1900-game wide gauntlet (19 engines, 2550–3500 CCRL, ChatGPT-verified ratings) and an 811-game precision gauntlet (10 diverse engines rated 2750–2917; per-opponent anchored calculation over 9 engines after excluding Igel 1.6.0, which underperforms its 2750 label in both gauntlets). The previous ~2870 figure was an extrapolation from the 4B STC SPRT; eval gains shrink at LTC and the old 2580–2788 reference field had miscalibrated labels.

- eval: non-linear mobility - MobilityBonus[pieceType][attackedSquares] lookup tables (rescaled x0.48) replace the linear MobilityStep * (moves - baseline) model. The linear model underpriced the caged end of the curve: going from 2 to 3 knight squares matters far more than going from 7 to 8.
- eval: mobility tables RE-CENTERED - the raw reference tables carry a large positive offset at typical mobility counts (rook +59 eg, queen +63 eg) that the reference engine absorbs in its own tuned piece values; injected as-is it silently inflated NoaChess's texel-tuned material balance (first SPRT run: +2 ± 18 after 870 games, aborted). Each table now has the entry at the old SPRT-validated baseline count (knight 4, bishop 6, rook 7, queen 14) subtracted, keeping the non-linear shape with a ~zero average contribution.
- eval: reference mobility area (Evaluation::initialize) - excludes pawns that are blocked or on the first two relative ranks, the own king and queen, blockers for the own king (pinned pieces) and squares controlled by enemy pawns. Previously: everything not occupied by a friendly piece and not pawn-attacked. Also feeds the KnightOnQueen/SliderOnQueen safe filter in threats, which is now reference-exact.
- eval: x-ray attacks (Evaluation::pieces) - bishops see through queens of both colors; rooks see through queens and own rooks. Batteries now project their real pressure into mobility, threats and king attack accounting.
- eval: pinned-piece attack restriction - a piece that is the single blocker of an enemy slider line to its own king only attacks along the pin line (LineThrough[king][piece] mask). Applies before the attackedBy bookkeeping, so threats and king danger stop counting phantom attacks from pinned pieces.
- infra: LineThrough[64x64] / Between[64x64] static tables and ComputeBlockersForKing (blockers_for_king equivalent: enemy sliders aimed at the king with exactly one piece in between, either color).
- tests: MobilityTests - pinned bishop has zero attacks, pinned rook keeps only the pin line, rook x-ray through own rook but not through a knight, bishop x-ray through queen, mobility-area exclusions (K/Q/low pawns/enemy pawn control/pinned pieces), non-linear curve shape (66 tests green).
- bench: no NPS regression (the blockers computation is offset by the cheaper mobility lookup).

## 2026-07-10 (v2.6.1) - block 4B: the reference engine threat evaluation

**SPRT vs v2.5.0 (tc 10+0.1): +103 ± 35 Elo, llr 2.99, H1 accepted in 243 games, score 109-42-81 [64.4%]** - far above the +25-35 estimate; the largest single evaluation gain of the project (NoaChess had zero threat terms, the biggest gap identified in the reference-engine analysis).

- eval: reference values RESCALED by 100/208 = 0.48 - the reference engine works in internal units where PawnValueEg = 208 equals the 100 cp it reports over UCI, while NoaChess evaluates directly in ~centipawns (PeSTO). The first SPRT run used the raw reference numbers, which made every threat term twice as strong as intended, and trended negative (llr -1.09 after 200 games) before being aborted. Permanent rule: every value ported from the reference evaluation gets the 0.48 factor.
- eval: full the reference engine threat evaluation (evaluate.cpp threats()), all 10 terms. The core concept is "strongly protected" (pawn-defended, or defended twice and not attacked twice) versus "weak" (attacked and not strongly protected) - precisely what the removed v2.4.0 threat attempt lacked, which rewarded attacks on healthily defended pieces and distorted the material judgement.
- eval: ThreatByMinor[victim] / ThreatByRook[victim] - bonuses indexed by the attacked piece type (minors also score against defended pieces; rooks only against weak ones).
- eval: Hanging (weak and undefended, or a non-pawn attacked twice), ThreatByKing (endgame-heavy), WeakQueenProtection (weak piece whose only protector is the queen), RestrictedPiece (enemy moves restricted by our control).
- eval: ThreatBySafePawn (safe pawn attacking a non-pawn) and ThreatByPawnPush (pawn can push next move to a safe square and then attack a non-pawn), with the reference's exact push logic (single + double pushes, safety filters).
- eval: KnightOnQueen / SliderOnQueen - threats on the squares from which the enemy queen can be hit next move, doubled when the enemy queen is the only queen on the board (queen imbalance).
- tests: ThreatsTests - hanging vs defended, safe-pawn threat, minor-on-rook vs minor-on-pawn delta, pawn-push threat probed on the isolated term, doubled-rook battery against the queen, strongly-protected exclusion (59 tests green).
- bench: NPS cost ~3-5%.

## 2026-07-10 (v2.6.0) - block 4A: attackedBy infrastructure

**Evaluation-neutral enabler** (identical node counts on fixed-depth benchmark positions; NPS cost ~2-3%, within the 2-4% budget). Prerequisite for threats (4B), improved mobility (4C) and king safety (4D).

- eval: attackedBy[color][pieceType] + AllPieces union and attackedBy2[color] (squares attacked by two or more friendly units), rebuilt on every evaluate call, mirroring the reference engine's init pass. King and pawn attack sets (including pawn double attacks) seed the tables before the piece loop; each piece then accumulates its attacks into the per-type, the union and the double-attack bitboards.
- eval: x-ray attacks and pinned-piece attack restriction deliberately NOT included yet - they change the attack sets (and therefore mobility) and belong to block 4C, so this change stays strictly evaluation-neutral.
- tests: AttackedByTests - per-type union, pawn double attacks, rook overlap, single-attacker exclusion, king+pawn overlap, no stale state between calls (53 tests green).
- docs: v2.5.0 strength updated to ~2768 CCRL from the 392-game LTC precision gauntlet (67.5% vs 2580-2788 field); old NoaChess_ROADMAP.md removed (superseded by ROADMAP.md).

## 2026-07-10 (v2.5.0) - speed block: staged move generation + lazy legality + PEXT

**SPRT vs v2.4.5 (tc 10+0.1): +101.3 ± 36.8 Elo, LOS 100%, score 91-32-85 [64.2%], H1 accepted in 208 games** - the largest single-version gain since the v2.3.0 search overhaul. Same engine knowledge, dramatically less work per node: depth 15 from the start position now takes 2.9s instead of 4.7s (-39%). **Precision gauntlet (tc=60+0.6, 392 games, 7 rivals rated 2580–2788 CCRL): 67.5% score, +127 Elo over the 2641-average field → ~2768 CCRL-equivalent.**

- search: lazy legality - Negamax generates PSEUDO-legal moves and validates each one at its only make (the scheme quiescence already used). The old up-front legality filter paid a full make/unmake per generated move, and the search loop then paid it again for every move it visited.
- search: staged move generation - the TT move is served first without generating anything (vetted by the new MoveGenerator.IsPseudoLegal), then captures/promotions (sorted, winners first), then quiet moves, with losing captures sinking to the very end. A node that cuts off early never pays for the moves it does not reach. Served order is identical to the previous full-sort ordering.
- movegen: MoveGenerator.IsPseudoLegal(board, move) - exact predicate for "would the generator emit this move", used to vet TT moves before making them (a Zobrist collision could otherwise hand the board a corrupting garbage move). Guarded by an exactness fuzz test (random game paths, 200 random 16-bit probes per position, stale-TT-move scenario) which caught a real bug pre-release: undefined flag encodings (6/7) slipping through as pawn captures.
- movegen: AppendCaptureMoves / AppendQuietMoves staged generators; set-equality with the one-shot generator is fuzz-tested.
- perf: PEXT (BMI2) sliding-piece attack lookup with a CPUID guard - enabled only on Intel and AMD Zen3+ (family 0x19+), where the instruction is fast. On AMD Zen1/Zen+/Zen2 (family 0x17) PEXT is microcoded (~18 cycles) and LOSES to magic lookups, so those CPUs keep the magic path. Decided once at startup via a constant-folded static readonly bool; both paths are cross-validated by tests on any BMI2 machine regardless of which one production uses.
- eval fix: backward pawns no longer ignore a same-rank neighbour - a phalanx member defends the stop square directly, so it is never backward. The old strictly-behind support mask made every phalanx whose front was contested pay the phalanx bonus and the backward penalty at once.
- eval fix: backward is now exclusive of isolated - an isolated pawn trivially has no support and already pays its own (larger) penalty; stacking both double-counted the same weakness.
- note: king safety overhaul (graduated shelter, pawn storm, safe checks) was implemented and REJECTED on this cycle: -77 Elo with pawn-only safe-check masking (phantom checks flooding the quadratic danger curve), statistically zero after fixing the mask and after isolating shelter/storm alone (~900 games total). King-safety units feed a quadratic curve and are not texel-tunable, making each value iteration cost hundreds of games - shelved until after the NNUE block per the pre-agreed decision rule.

## 2026-07-10 (v2.4.5) - phase A eval: tempo + phalanx + backward pawns + retune

**SPRT vs v2.4.0 (tc 10+0.1, 1300 games): +12.2 ± 15.2 Elo, LOS 94.2%, LLR +1.2** - positive trend, SPRT non-conclusive at stop; retune on fresh data confirms the new terms are absorbed cleanly.

- eval: tempo bonus - the side to move receives a flat +14 cp bonus, always positive for the evaluee. Applies after tapering (pure negamax constant, not tunable). Handles initiative asymmetry that the static evaluator cannot otherwise express.
- eval: phalanx (connected pawns) - a pawn with a friendly pawn on the same rank and adjacent file earns a rank-indexed bonus (rank 2: 3/0, rank 5: 44/34, rank 6: 64/54 MG/EG). Computed inside the pawn hash; zero search-speed cost.
- eval: backward pawns - a pawn whose stop square is attacked by an enemy pawn AND has no friendly pawn on adjacent files behind it (no support coming) is penalized (-12, -6). Computed inside the pawn hash; zero search-speed cost.
- tuning: full retune (tools/NoaChess.Tuner) on 2.02M positions from 50K fresh 2.4.5-strength games (seed 20260710); all 3 new scalar/array terms plus 736 PST cells re-optimized jointly. Phalanx and BackwardPawn both moved in the expected direction from hand values.
- tests: Phalanx_BonusIsApplied, Backward_PenaltyIsApplied, Tempo_SideToMoveScoresHigher added (46 tests green). Starting-position balance test updated: symmetric position now correctly evaluates to exactly Tempo (not 0).

## 2026-07-10 (v2.4.0) - evaluation terms + full texel tuning

**SPRT vs v2.3.0 (tc 10+0.1, 2000 games): +13.0 ± 12.6 Elo, LOS 97.8%, score 728-653-619 [51.9%], LLR +1.93** - a real, statistically solid improvement (~2723 CCRL-equivalent estimated; gauntlet pending).

- eval: knight outposts - a knight on relative ranks 4-6, protected by a friendly pawn, on a square no enemy pawn can ever attack, earns a permanent-asset bonus.
- eval: advanced passed-pawn logic - blocked passers (enemy piece on the stop square) give back a third of the rank bonus; connected passers on adjacent files earn an endgame escort bonus; a rook behind its own passer earns the Tarrasch bonus.
- eval: rook on the 7th rank - endgame-heavy bonus per rook on the opponent's second rank (cuts the king off, eats the pawn chain from behind).
- eval: space - per safe central square (files c-f, relative ranks 2-4, not occupied by a friendly pawn, not attacked by enemy pawns).
- eval: threats REMOVED - a bonus for attacking enemy pieces (pawn/minor/rook attack tables) was implemented and rejected after repeated SPRT failures: the term is tempo-blind, rewarding "attacks" the opponent resolves with its very next move, which distorts the material judgement. Hand-tuned SPRT attempts of this block scored between -10 and -2 Elo vs v2.3.0 - hand-picked values are noise at this level; the block's Elo had to come from automated tuning.
- tuning: tools/NoaChess.Tuner - texel tuning by coordinate descent, now covering the full piece-square tables (736 cells) plus all positional terms (776 tunables). Tuned on 2.02M quiet positions sampled from 4.42M records / 50K self-play games generated by the v2.3.0-strength engine at 10K nodes/move (seed 20250709); optimal K = 0.9125, MSE 0.085570 -> 0.083798 over 3 coordinate-descent passes. The old run3/run4 datasets (v2.0-era engine) were discarded as poisoned.
- tuning: mobility is deliberately EXCLUDED from texel tuning - every run, on old and fresh data alike, converges to negative endgame mobility for the minors (spurious correlation: the winning side simplifies and restricts enemy mobility), which plays disastrously. The hand values (SPRT-validated in v2.2.0) stay fixed.
- perf: the new terms initially cost ~13% nps, which silently ate their Elo across six neutral SPRT runs. Fixed: passer bitboards are now cached in the pawn hash (the piece-dependent passer terms no longer rescan every pawn per eval) and the per-call pawn-attack array allocation was removed.
- tests: outpost, rook-on-7th, blocked-passer, rook-behind-passer and connected-passers sanity checks added to the evaluation suite.

## 2026-07-09 (v2.3.0) - search core overhaul

**Measured strength: ~2710 Elo (CCRL-equivalent)** - 231-game LTC precision gauntlet (tc=60+0.6) vs 7 engines rated 2580–2788 CCRL; scored 59.5% (+67 Elo over the ~2642 field average), up from 44.4% for v2.2.0 against the same field (~110 Elo real-play gain). The long-standing Black-side weakness is gone: wins 54 White / 52 Black, losses 32 / 32 - fully symmetric. SPRT vs v2.2.0 had passed H1 earlier: +91 ± 34 Elo, LOS 100%, score 106-43-96 [62.9%] over 245 games at 10+0.1.

- search: counter-move heuristic - the quiet refutation of the opponent's last move is remembered per (piece, destination) and ordered right after the killers.
- search: continuation history - a second history table conditioned on the previous move (prev piece/destination x current piece/destination, ~2.3 MB), blended into quiet-move ordering. Learns "after THIS, THAT reply refutes" - far sharper than the global butterfly history.
- search: history maluses - quiet moves searched before a beta cutoff are punished in both history tables, so failed moves sink in the ordering instead of lingering.
- search: singular extensions - when the TT move's stored score is trustworthy (depth >= 8, entry depth >= depth-3, lower/exact bound), all other moves are verified shallower against a lowered window; if none comes close the TT move is "singular" and searched one ply deeper. Excluded-move searches skip TT cutoffs/stores and prunings.
- search: history-informed LMR - the log-formula reduction is decreased for quiet moves with a good history score (and killers/counter moves) and increased for disliked ones.
- search: progressive aspiration widening - on a fail high/low the window is re-centered on the failing score and doubled instead of jumping straight to a full-width re-search.
- search: Internal Iterative Reductions - nodes at depth >= 4 with no TT move are searched one ply shallower (bad ordering is not worth full depth; a later visit finds the TT move waiting).
- search: ProbCut - at non-PV depth >= 5 nodes, a non-losing capture that beats beta + 150 in a quiescence probe and then in a depth-4 verification search cuts the node immediately.

## 2026-07-09 (v2.2.0) - classical evaluation & search overhaul

**Measured strength: ~2600 Elo (CCRL-equivalent)** - 350-game LTC gauntlet (tc=60+0.6) vs 7 engines rated 2580–2788 CCRL; scored 44.4% overall. SPRT vs v2.1.1 at tc=60+0.6 **passed H1** in 160 games: +429 ± 88 Elo, LOS 100%, score 140–5–15 [92.2%].

- eval: tapered (middlegame/endgame) evaluation - every term now carries a MG and an EG value blended by game phase (PeSTO piece values + two-phase piece-square tables). Replaces the old flat single-phase material+PST.
- eval: king safety - enemy attacks on the king zone accumulate weighted "attack units" (plus a pawn-shield / open-file check) through a quadratic danger curve, applied as a middlegame penalty that tapers away in the endgame.
- eval: piece mobility - each minor/major piece scores by the number of squares it can reach, excluding squares covered by enemy pawns; centered so it does not inflate material.
- eval: bishop pair bonus, rooks on open / semi-open files; pawn structure (doubled/isolated/passed) is now tapered (passers endgame-heavy).
- eval: single-pass design - each piece's attack bitboard is computed once and feeds both mobility and king safety (no repeated piece scans).
- search: logarithmic Late Move Reductions (reduction from a log(depth)*log(moveNo) table) replacing the fixed 1-ply reduction.
- search: reverse futility pruning (static null move), futility pruning and late move pruning added to non-PV shallow nodes.
- tests: color-symmetry, starting-position balance, king-safety and bishop-pair sanity checks for the tapered evaluator.

## 2026-07-08 (v2.1.1-dev)

- fix: NNUE bullet time forfeit - Server GC (background collection instead of blocking stop-the-world pauses) and a synchronous JIT warm-up search on NNUE activation (`SetUseNnue`), so tiered-compilation recompiles happen at setoption time instead of stalling mid-game. Verified with a 3-game bullet (1+0) stress test: no time flags, all moves well under budget.
- models: added noa-v2-run3.noannue (250K-game overnight training run, promoted default).
- tools: NoaChess.DataGen - new `--model` flag lets self-play use a trained NNUE model instead of the classical evaluator, for reinforcement-style data generation (run4 onward).
- tools: overnight_training_run4.bat - self-play labeled by the run3 NNUE model, 350K games.

## 2026-07-07 (v2.1.0-dev)

- uci: pondering ("Ponder" option, `go ponder` / `ponderhit` / `stop`) - the engine now thinks on the opponent's time; on ponderhit the warm transposition table makes the timed re-search nearly free. Late bestmoves from searches interrupted by position/ucinewgame are suppressed.
- uci: startup banner (engine name, version, author, .NET/SIMD/core info) printed before the UCI handshake; engine identity centralized in constants (single place to bump versions).
- build: published output is now exactly one file - Release builds of Core/Engine no longer emit debug symbols, so the single-file exe ships without loose .pdb files.
- tools: overnight_training.bat - one-click chained pipeline (datagen 250K games at 15K nodes -> train -> validate -> export) with fail-fast between stages.

## 2026-07-07 (v2.0.0-dev) - NNUE infrastructure

- engine: complete NNUE evaluation runtime (Evaluation/Nnue/) - HalfKP feature schema (40,960 king-relative features per perspective, schema id 1), incremental accumulators with per-ply stack and lazy king-move refresh, integer-quantized inference with scalar reference and SIMD (Vector<T>) backends selected at startup.
- engine: NOANNUE1 versioned binary model format with strict validation (magic, format/schema/architecture ids, dimensions, payload SHA-256); incompatible or corrupt models are rejected with a descriptive info string and the classical evaluator stays active.
- engine: evaluator selector - IIncrementalEvaluator hooks wired into every make/unmake of the search (root, negamax, quiescence, null move); switching evaluators clears the TT.
- uci: UseNNUE and EvalFile options fully functional; model SHA-256 reported on load for reproducibility.
- tools: NoaChess.DataGen - multi-threaded self-play training data generator writing the NOADATA1 binary format (packed positions + side-to-move score and result labels, quiet-position filters, resign adjudication) with a reproducibility manifest.
- tools: Python training pipeline (tools/training/nnue): dataset reader, PyTorch model (architecture 1: FT 128, L1 32), trainer with lambda-blended score/result targets, quantization-aware export to NOANNUE1, and validation utility reporting quantization error.
- tests: 14 new NNUE tests - golden feature indices, deterministic features, make/unmake feature restoration, incremental == full refresh over random games (castling/en passant/promotions), scalar == SIMD, loader round-trip and corruption rejection.
- validated end to end: model trained in PyTorch loads in the engine (SHA match) and plays legal chess with UseNNUE=true.
- PENDING to close v2.0.0: full-scale training run and SPRT vs the classical evaluator (~2070 Elo baseline). The version does not promote until SPRT passes, per the technical roadmap.

## 2026-07-06 (v1.1.1)

- **Measured strength: ~2070 +/- 50 Elo (CCRL-equivalent)** - 800-game gauntlet at 10+0.1 vs 8 engines with known CCRL ratings (TSCP 1600 ... GreKo 2490). Score 67.1% overall; beat Gaia (2400) 17.5% and GreKo (2490) 7% of the games. Zero crashes, zero illegal moves, zero time forfeits across all 800 games. This is the official baseline the NNUE version (v2.0) must beat.

- fix: TimeManager crashed (Math.Clamp with crossed bounds) when the remaining clock was nearly exhausted - an engine crash at zero clock is a guaranteed time forfeit. Likely contributor to the reported flags in won positions.
- evaluation: mop-up term for converting won endgames (drive the enemy king to the edge, bring the own king closer); fixes endless shuffling with K+R+B vs K (now mates in ~28 moves at 200 ms/move) that burned the clock and risked fifty-move draws.
- engine: instant reply when only one legal move exists (saves the whole budget in forced sequences).
- engine: repetition scan skipped when impossible (fewer than 4 reversible half-moves) - it ran at every node and cost O(halfmove clock), worst exactly in long endgames.
- engine: SEE short-circuit - capturing an equal-or-higher-valued victim can never lose material, so the full exchange computation only runs for "upward" captures (QxP, RxN...).
- time safety: MoveOverhead default raised 30 -> 100 ms and an absolute 150 ms reserve is never spent (GUIs add fixed per-move friction beyond the engine's own accounting).

## 2026-07-06 (v1.1.0)

- core: magic bitboards for sliding-piece attacks - O(1) table lookup replaces ray scanning; magics found deterministically at startup (fixed seed), validated by the full Perft suite.
- core: MoveList - reusable fixed-capacity move container; move generation in hot paths (search, perft) allocates nothing.
- core: captures-only move generation mode for quiescence search (quiet moves are never enumerated).
- engine: search uses one preallocated MoveList per ply; MovePicker sorts in place via the list's parallel score array (zero allocations per node).
- engine: EngineProfile (Default/Bullet) - tunable aspiration window, LMR thresholds and time-manager horizon; Bullet prunes sooner, avoids re-searches and spreads the clock over more moves. Selectable via the UCI "Profile" combo option.
- engine (fix): soft time budget was only checked between iterations, so iterations started near the limit ran up to the 4x hard cap, overspending on nearly every move and flagging in long games (reported: time losses vs TSCP/Grizzly from won positions). Now: predictive cut (no new iteration past half the soft budget) and graceful root-level soft stop that reuses the partially searched iteration; partial iterations are not stored in the TT.
- benchmarks: NoaChess.Benchmarks project (BenchmarkDotNet) - move generation, make/unmake, evaluation and search benchmarks with allocation tracking.
- measured: search speed ~580K -> ~1.6M nps (about 2.5x); bullet 1+0 full-game clock simulation completes with no flag.
- uci: publish produces a single self-contained .exe (no DLLs, no .NET runtime required), like native engines.

## 2026-07-06 (v1.0.0)

- engine: PVS (Principal Variation Search) - null-window probes for non-first moves with re-search on improvement.
- engine: Null Move Pruning with zugzwang guard (disabled without non-pawn material, in check, or twice in a row).
- engine: check extension (positions in check searched one ply deeper).
- engine: SEE (Static Exchange Evaluation) via the swap algorithm with x-ray support; used for capture ordering (losing captures last), pruning losing captures in quiescence and skipping clearly losing captures near the horizon.
- engine: repetition detection - a single repetition scores as a draw inside the search; threefold repetition added to GameState.
- engine: pawn structure evaluation (doubled, isolated, passed pawns) cached under a dedicated pawn hash; evaluation split into EvaluationParams / PieceSquareTables / PawnStructureEvaluator for future tuning.
- engine: TimeManager - soft/hard budgets from the clock (soft: stop starting iterations; hard: abort), MoveOverhead margin; node-limited search (`go nodes N`).
- core: MakeNullMove/UnmakeNullMove, pawn-only Zobrist key (incremental), CountRepetitions, HasNonPawnMaterial.
- uci: full basic protocol - asynchronous `go` (search on a background task), `stop`, `isready` answered while searching, `go infinite`, `setoption` with Hash (resizes TT), Threads, MoveOverhead and UseNNUE options declared and parsed.
- fix: move-ordering history scores could overlap the killer/capture bands; history is now clamped below the killer band.
- tests: null move state restoration, repetition counting, pawn hash consistency, zugzwang material detection, SEE exchanges (incl. x-rays), pawn structure terms.

## 2026-07-06 (v0.2.0)

- engine: quiescence search at the horizon (stand pat + MVV-LVA ordered captures and queen promotions), removing the horizon effect.
- engine: transposition table (Zobrist-keyed, depth-preferred replacement, Exact/LowerBound/UpperBound bounds, mate-score ply normalization).
- engine: aspiration windows around the previous iteration score, with full-window re-search on fail.
- engine: move ordering pipeline - TT move, MVV-LVA captures, killer moves, history heuristic (`MovePicker`, `KillerTable`, `HistoryTable`).
- engine: Late Move Reductions for late quiet moves, with null-window probe and full-depth re-search.
- engine: search limits and basic time management; default depth raised from 4 to 6 plies.
- uci: `go movetime N` and `go wtime/btime/winc/binc` (budget = clock/30 + increment/2); `info` lines include `time` and `nps`; `ucinewgame` clears engine state.
- gui: background analysis deepened to 12 plies and now warms the shared transposition table (real pondering); searches are serialized (cancel + await) so engine state is never mutated concurrently.
- tests: transposition table semantics, mate in two, horizon-blunder avoidance, time-limit compliance.

## 2026-07-05 (v0.1.3)

- core: bitboard+mailbox board, full legal move generation, FEN, Zobrist hashing, incremental make/unmake, game-over detection, Perft-validated.
- engine: alpha-beta (negamax) with iterative deepening and progress reporting; classical evaluation (material + piece-square tables).
- uci: console host with uci, isready, ucinewgame, position, go depth, quit.
- gui: WPF (MVVM) click-click play vs the engine, highlights, promotion dialog, board flip, Cburnett SVG pieces, live status bar with evaluation/depth, background analysis on the user's turn.

## 2025-06-04 (v0.1.0-alpha)

- project: repo created and initial documentation setup (README, LICENSE, CONTRIBUTING, CHANGELOG, CODE_OF_CONDUCT).
- infra: established branch workflow (`main`, `develop`, `feature/*`, `release/*`).
- infra: added LICENSE with legal disclaimer and Spanish notice.
- infra: added CHANGELOG.md, CONTRIBUTING.md, CODE_OF_CONDUCT.md (bilingual).
- infra: added .gitignore (Dotnet).
- doc: initial roadmap and project structure defined in README.
