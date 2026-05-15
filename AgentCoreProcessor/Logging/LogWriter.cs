using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace AgentCoreProcessor.Logging;

public class LogWriter : IDisposable
{
    private readonly Channel<LogEvent> _channel;
    private readonly LogDatabase _db;
    private readonly OpenSpanTracker _spanTracker;
    private readonly TokenAggregator _tokenAggregator;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _consumeTask;
    private readonly List<Action<IReadOnlyList<LogEvent>>> _subscribers = new();
    private readonly object _subLock = new();

    public LogWriter(LogDatabase db, OpenSpanTracker spanTracker, TokenAggregator tokenAggregator)
    {
        _db = db;
        _spanTracker = spanTracker;
        _tokenAggregator = tokenAggregator;
        _channel = Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(10000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _consumeTask = Task.Run(ConsumeLoop);
    }

    public void Enqueue(LogEvent evt)
    {
        if (evt.Type == "open")
            _spanTracker.TrackOpen(evt);
        _channel.Writer.TryWrite(evt);
    }

    public void EnqueueClose(LogEvent evt)
    {
        if (evt.SpanId != null)
            _spanTracker.TrackClose(evt.SpanId);
        _channel.Writer.TryWrite(evt);
    }

    public IDisposable Subscribe(Action<IReadOnlyList<LogEvent>> callback)
    {
        lock (_subLock) _subscribers.Add(callback);
        return new Unsubscriber(() => { lock (_subLock) _subscribers.Remove(callback); });
    }

    private async Task ConsumeLoop()
    {
        var batch = new List<LogEvent>(64);
        var reader = _channel.Reader;

        while (!_cts.Token.IsCancellationRequested)
        {
            batch.Clear();
            try
            {
                var evt = await reader.ReadAsync(_cts.Token);
                batch.Add(evt);

                var deadline = Environment.TickCount64 + 100;
                while (batch.Count < 50 && Environment.TickCount64 < deadline && reader.TryRead(out var more))
                {
                    batch.Add(more);
                }

                WriteBatch(batch);
                NotifySubscribers(batch);
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }

        while (reader.TryRead(out var remaining))
        {
            batch.Add(remaining);
            if (batch.Count >= 50) { WriteBatch(batch); batch.Clear(); }
        }
        if (batch.Count > 0) WriteBatch(batch);
    }

    private void WriteBatch(List<LogEvent> batch)
    {
        using var tx = _db.Connection.BeginTransaction();
        try
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO events (signal_id, scope, branch, parent_id, span_id, group_name, level, type, timestamp, name, detail)
                VALUES (@sig, @scope, @branch, @parent, @span, @group, @level, @type, @ts, @name, @detail)
                """;

            var pSig = cmd.Parameters.Add("@sig", SqliteType.Text);
            var pScope = cmd.Parameters.Add("@scope", SqliteType.Text);
            var pBranch = cmd.Parameters.Add("@branch", SqliteType.Integer);
            var pParent = cmd.Parameters.Add("@parent", SqliteType.Text);
            var pSpan = cmd.Parameters.Add("@span", SqliteType.Text);
            var pGroup = cmd.Parameters.Add("@group", SqliteType.Text);
            var pLevel = cmd.Parameters.Add("@level", SqliteType.Integer);
            var pType = cmd.Parameters.Add("@type", SqliteType.Text);
            var pTs = cmd.Parameters.Add("@ts", SqliteType.Integer);
            var pName = cmd.Parameters.Add("@name", SqliteType.Text);
            var pDetail = cmd.Parameters.Add("@detail", SqliteType.Text);

            foreach (var evt in batch)
            {
                pSig.Value = evt.SignalId;
                pScope.Value = evt.Scope;
                pBranch.Value = evt.Branch;
                pParent.Value = evt.ParentId != null ? evt.ParentId : DBNull.Value;
                pSpan.Value = evt.SpanId != null ? evt.SpanId : DBNull.Value;
                pGroup.Value = evt.GroupName;
                pLevel.Value = evt.Level;
                pType.Value = evt.Type;
                pTs.Value = evt.Timestamp;
                pName.Value = evt.Name;
                pDetail.Value = evt.Detail != null ? evt.Detail : DBNull.Value;
                cmd.ExecuteNonQuery();
            }

            _tokenAggregator.ProcessBatch(batch, tx);
            tx.Commit();
        }
        catch
        {
            try { tx.Rollback(); } catch { }
        }
    }

    private void NotifySubscribers(List<LogEvent> batch)
    {
        List<Action<IReadOnlyList<LogEvent>>> subs;
        lock (_subLock) subs = _subscribers.ToList();
        foreach (var sub in subs)
        {
            try { sub(batch); } catch { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _channel.Writer.Complete();
        _consumeTask.Wait(TimeSpan.FromSeconds(3));
        _cts.Dispose();
    }

    private class Unsubscriber(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
