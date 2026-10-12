using PaddiXiangqi.Core;

namespace PaddiXiangqi.Engine;

public sealed record RuleMoves(string Version, string Fen, IReadOnlyList<string> Allowed,
    IReadOnlyList<string> Excluded);

/// <summary>Validates model candidates against the recorded Xiangqi position and repetition rules.</summary>
public sealed class PikafishRulesClient : IAsyncDisposable
{
    private const int MaximumHistoryPlies = 10_000;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _cachedKey;
    private RuleMoves? _cached;
    private bool _disposed;
    public int QueryCount { get; private set; }

    public Task<RuleMoves> GetAllowedMovesAsync(string startFen, string uciMoves,
        string currentFen, CancellationToken cancellationToken = default)
    {
        if (startFen.IndexOfAny(['\n', '\r', '\t']) >= 0 || uciMoves.IndexOfAny(['\n', '\r', '\t']) >= 0)
            throw new ArgumentException("规则校验的 FEN 和棋谱必须为单行文本。");
        return GetAllowedMovesCoreAsync(startFen, uciMoves, currentFen, cancellationToken);
    }

    private async Task<RuleMoves> GetAllowedMovesCoreAsync(string startFen, string uciMoves,
        string currentFen, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var key = startFen + "\t" + uciMoves;
            if (_cachedKey == key && _cached is not null)
            {
                ValidatePosition(_cached.Fen, currentFen);
                return _cached;
            }

            var result = await Task.Run(
                () => CalculateAllowedMoves(startFen, uciMoves, currentFen, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            QueryCount++;
            _cached = result;
            _cachedKey = key;
            return _cached;
        }
        finally { _gate.Release(); }
    }

    private static RuleMoves CalculateAllowedMoves(string startFen, string uciMoves, string currentFen,
        CancellationToken cancellationToken)
    {
        var history = uciMoves.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (history.Length > MaximumHistoryPlies)
            throw new InvalidOperationException("棋谱超过 10000 回合，无法进行规则校验。");

        var game = new XiangqiGame();
        game.LoadFen(startFen);
        var positions = new List<RulePosition>(history.Length + 2)
        {
            RulePosition.Capture(game, null, null)
        };
        var identities = PieceIdentities.Create(game.Board);
        foreach (var text in history)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!game.TryMoveUci(text, out var move))
                throw new InvalidOperationException("皮卡鱼规则校验失败：棋谱中有非法着法 " + text);
            identities.Move(move.From, move.To);
            positions.Add(RulePosition.Capture(game, move, identities.Clone()));
        }

        var actualFen = game.CurrentFen();
        ValidatePosition(actualFen, currentFen);
        var positionKeys = positions.Select(PikafishRepetitionJudge.PositionKey).ToArray();
        var allowed = new List<string>();
        var excluded = new List<string>();
        foreach (var move in game.AllControllerLegalMoves())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = RulePosition.After(positions[^1], move);
            if (PikafishRepetitionJudge.LosesByRepetition(positions, positionKeys, next, move))
                excluded.Add(move.Uci);
            else
                allowed.Add(move.Uci);
        }

        return new("Paddi-CSharp-Rules-1", actualFen,
            Array.AsReadOnly(allowed.ToArray()), Array.AsReadOnly(excluded.ToArray()));
    }

    private static void ValidatePosition(string actual, string expected)
    {
        if (!actual.Split(' ').Take(2).SequenceEqual(expected.Split(' ').Take(2)))
            throw new InvalidOperationException("规则校验的棋谱与当前棋盘不一致，未请求模型。");
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _disposed = true; _cached = null; _cachedKey = null; }
        finally { _gate.Release(); }
    }

    private sealed class PieceIdentities
    {
        private readonly int[,] _ids;

        private PieceIdentities(int[,] ids) => _ids = ids;

        public static PieceIdentities Create(char[,] board)
        {
            var ids = new int[10, 9];
            var red = 0;
            var black = 0;
            for (var rank = 0; rank < 10; rank++)
            for (var file = 0; file < 9; file++)
            {
                var piece = board[rank, file];
                ids[rank, file] = piece == '\0' ? -1 : XiangqiGame.IsRed(piece) ? red++ : black++;
            }
            return new(ids);
        }

        public void Move(Square from, Square to)
        {
            _ids[to.Rank, to.File] = _ids[from.Rank, from.File];
            _ids[from.Rank, from.File] = -1;
        }

        public PieceIdentities Clone() => new((int[,])_ids.Clone());
        public int At(Square square) => _ids[square.Rank, square.File];
    }

    private sealed record RulePosition(char[,] Board, int[,] PieceIds, bool RedToMove, ChessMove? LastMove)
    {
        public static RulePosition Capture(XiangqiGame game, ChessMove? lastMove, PieceIdentities? ids)
        {
            var identities = ids ?? PieceIdentities.Create(game.Board);
            return new((char[,])game.Board.Clone(), CopyIds(identities), game.RedToMove, lastMove);
        }

        public static RulePosition After(RulePosition before, ChessMove move)
        {
            var board = (char[,])before.Board.Clone();
            var ids = (int[,])before.PieceIds.Clone();
            board[move.To.Rank, move.To.File] = move.Piece;
            board[move.From.Rank, move.From.File] = '\0';
            ids[move.To.Rank, move.To.File] = ids[move.From.Rank, move.From.File];
            ids[move.From.Rank, move.From.File] = -1;
            return new(board, ids, !before.RedToMove,
                move with { IsCheck = XiangqiGame.IsInCheck(board, !before.RedToMove) });
        }

        private static int[,] CopyIds(PieceIdentities identities)
        {
            var ids = new int[10, 9];
            for (var rank = 0; rank < 10; rank++)
            for (var file = 0; file < 9; file++)
                ids[rank, file] = identities.At(new Square(file, rank));
            return ids;
        }
    }

    private static class PikafishRepetitionJudge
    {
        public static bool LosesByRepetition(IReadOnlyList<RulePosition> history,
            IReadOnlyList<string> historyKeys, RulePosition current, ChessMove candidate)
        {
            var currentKey = PositionKey(current);
            var repetitions = historyKeys.Select((key, ply) => (key, ply))
                .Where(item => item.key == currentKey)
                .Select(item => item.ply)
                .TakeLast(2)
                .ToArray();
            if (repetitions.Length < 2) return false;

            var cycleStart = repetitions[0];
            var cycleEnd = history.Count;

            var redChecks = true;
            var blackChecks = true;
            var anyCheck = false;
            for (var ply = cycleStart; ply < cycleEnd; ply++)
            {
                var move = ply + 1 < history.Count ? history[ply + 1].LastMove : current.LastMove;
                if (move is null) return false;
                anyCheck |= move.Value.IsCheck;
                if (XiangqiGame.IsRed(move.Value.Piece)) redChecks &= move.Value.IsCheck;
                else blackChecks &= move.Value.IsCheck;
            }

            if (cycleEnd - cycleStart > 120 && !anyCheck) return false;
            if (redChecks ^ blackChecks)
                return XiangqiGame.IsRed(candidate.Piece) == redChecks;
            if (redChecks || anyCheck) return false;

            var redChases = ChasesThroughoutCycle(history, current, cycleStart, cycleEnd, true);
            var blackChases = ChasesThroughoutCycle(history, current, cycleStart, cycleEnd, false);
            return redChases.Count > 0 ^ blackChases.Count > 0
                ? (redChases.Count > 0) == XiangqiGame.IsRed(candidate.Piece)
                : false;
        }

        private static HashSet<int> ChasesThroughoutCycle(IReadOnlyList<RulePosition> history,
            RulePosition current, int cycleStart, int cycleEnd, bool red)
        {
            HashSet<int>? common = null;
            for (var ply = cycleStart + 1; ply <= cycleEnd; ply++)
            {
                var position = ply < history.Count ? history[ply] : current;
                if (position.RedToMove == red) continue;
                var targets = ChasedTargets(position, red);
                if (common is null) common = targets;
                else common.IntersectWith(targets);
                if (common.Count == 0) break;
            }
            return common ?? [];
        }

        private static HashSet<int> ChasedTargets(RulePosition position, bool red)
        {
            var board = position.Board;
            var targets = new HashSet<int>();
            for (var rank = 0; rank < 10; rank++)
            for (var file = 0; file < 9; file++)
            {
                var attacker = board[rank, file];
                if (attacker == '\0' || XiangqiGame.IsRed(attacker) != red ||
                    char.ToUpperInvariant(attacker) is 'K' or 'P')
                    continue;

                var from = new Square(file, rank);
                foreach (var to in Attacks(board, from))
                {
                    var target = board[to.Rank, to.File];
                    if (target == '\0' || XiangqiGame.IsRed(target) == red ||
                        char.ToUpperInvariant(target) == 'K' ||
                        (char.ToUpperInvariant(target) == 'P' && !HasCrossedRiver(target, to, red)))
                        continue;
                    if (!MoveKeepsKingSafe(board, from, to, red)) continue;

                    var attackerType = char.ToUpperInvariant(attacker);
                    var targetType = char.ToUpperInvariant(target);
                    if ((attackerType is 'N' or 'C' && targetType == 'R') ||
                        (attackerType is 'A' or 'B' && targetType is 'R' or 'C' or 'N'))
                    {
                        targets.Add(position.PieceIds[to.Rank, to.File]);
                        continue;
                    }

                    var afterCapture = (char[,])board.Clone();
                    afterCapture[to.Rank, to.File] = attacker;
                    afterCapture[from.Rank, from.File] = '\0';
                    var defended = false;
                    for (var defenderRank = 0; defenderRank < 10 && !defended; defenderRank++)
                    for (var defenderFile = 0; defenderFile < 9 && !defended; defenderFile++)
                    {
                        var defender = afterCapture[defenderRank, defenderFile];
                        if (defender == '\0' || XiangqiGame.IsRed(defender) == red) continue;
                        var defenderFrom = new Square(defenderFile, defenderRank);
                        if (Attacks(afterCapture, defenderFrom).Contains(to) &&
                            MoveKeepsKingSafe(afterCapture, defenderFrom, to, !red))
                            defended = true;
                    }
                    if (!defended)
                    {
                        if (attackerType != targetType ||
                            !Attacks(board, to).Contains(from) ||
                            !MoveKeepsKingSafe(board, to, from, !red))
                            targets.Add(position.PieceIds[to.Rank, to.File]);
                    }
                }
            }
            return targets;
        }

        private static bool HasCrossedRiver(char pawn, Square square, bool attackerRed) =>
            attackerRed ? !XiangqiGame.IsRed(pawn) && square.Rank >= 5
                         : XiangqiGame.IsRed(pawn) && square.Rank <= 4;

        private static bool MoveKeepsKingSafe(char[,] board, Square from, Square to, bool red)
        {
            var next = (char[,])board.Clone();
            next[to.Rank, to.File] = next[from.Rank, from.File];
            next[from.Rank, from.File] = '\0';
            return !XiangqiGame.IsInCheck(next, red);
        }

        private static IEnumerable<Square> Attacks(char[,] board, Square from)
        {
            var piece = board[from.Rank, from.File];
            var red = XiangqiGame.IsRed(piece);
            var type = char.ToUpperInvariant(piece);
            var f = from.File;
            var r = from.Rank;
            bool Inside(int file, int rank) => file is >= 0 and < 9 && rank is >= 0 and < 10;
            switch (type)
            {
                case 'R':
                case 'C':
                    foreach (var (df, dr) in Orthogonal)
                    {
                        var screen = false;
                        for (int file = f + df, rank = r + dr; Inside(file, rank); file += df, rank += dr)
                        {
                            var target = board[rank, file];
                            if (type == 'R')
                            {
                                yield return new(file, rank);
                                if (target != '\0') break;
                            }
                            else if (!screen)
                            {
                                if (target != '\0') screen = true;
                            }
                            else if (target != '\0')
                            {
                                yield return new(file, rank);
                                break;
                            }
                        }
                    }
                    break;
                case 'N':
                    foreach (var (df, dr, lf, lr) in HorseJumps)
                    {
                        var file = f + df;
                        var rank = r + dr;
                        if (Inside(file, rank) && board[r + lr, f + lf] == '\0')
                            yield return new(file, rank);
                    }
                    break;
                case 'B':
                    foreach (var (df, dr) in Diagonal)
                    {
                        var file = f + 2 * df;
                        var rank = r + 2 * dr;
                        if (Inside(file, rank) && (red ? rank >= 5 : rank <= 4) &&
                            board[r + dr, f + df] == '\0')
                            yield return new(file, rank);
                    }
                    break;
                case 'A':
                    foreach (var (df, dr) in Diagonal)
                    {
                        var file = f + df;
                        var rank = r + dr;
                        if (Inside(file, rank) && InPalace(file, rank, red))
                            yield return new(file, rank);
                    }
                    break;
                case 'K':
                    foreach (var (df, dr) in Orthogonal)
                    {
                        var file = f + df;
                        var rank = r + dr;
                        if (Inside(file, rank) && InPalace(file, rank, red))
                            yield return new(file, rank);
                    }
                    for (var rank = r + (red ? -1 : 1); rank is >= 0 and < 10; rank += red ? -1 : 1)
                    {
                        if (board[rank, f] == '\0') continue;
                        if (board[rank, f] == (red ? 'k' : 'K')) yield return new(f, rank);
                        break;
                    }
                    break;
                case 'P':
                    var nextRank = r + (red ? -1 : 1);
                    if (Inside(f, nextRank)) yield return new(f, nextRank);
                    if (red ? r <= 4 : r >= 5)
                    {
                        if (Inside(f - 1, r)) yield return new(f - 1, r);
                        if (Inside(f + 1, r)) yield return new(f + 1, r);
                    }
                    break;
            }
        }

        private static bool InPalace(int file, int rank, bool red) =>
            file is >= 3 and <= 5 && (red ? rank is >= 7 and <= 9 : rank is >= 0 and <= 2);

        public static string PositionKey(RulePosition position)
        {
            var chars = new char[91];
            var index = 0;
            for (var rank = 0; rank < 10; rank++)
            for (var file = 0; file < 9; file++)
                chars[index++] = position.Board[rank, file];
            chars[index] = position.RedToMove ? 'w' : 'b';
            return new(chars);
        }

        private static readonly (int df, int dr)[] Orthogonal = [(1, 0), (-1, 0), (0, 1), (0, -1)];
        private static readonly (int df, int dr)[] Diagonal = [(1, 1), (-1, 1), (1, -1), (-1, -1)];
        private static readonly (int df, int dr, int lf, int lr)[] HorseJumps =
        [
            (2, 1, 1, 0), (2, -1, 1, 0), (-2, 1, -1, 0), (-2, -1, -1, 0),
            (1, 2, 0, 1), (-1, 2, 0, 1), (1, -2, 0, -1), (-1, -2, 0, -1)
        ];
    }
}
