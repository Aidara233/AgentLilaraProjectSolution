namespace AgentCoreProcessor.Engine.Session;

internal partial class SessionEngine
{
    /// <summary>仅用于尚未发布外部资源的预定。不是 CreateSession 事务。</summary>
    internal sealed class PoolReservation
    {
        internal readonly SessionEngine Owner;
        internal readonly SessionPool Pool;
        internal readonly string OldId;
        internal readonly string NewId;
        internal readonly long Version;
        internal readonly bool HadMember;
        internal readonly bool HadIndex;
        internal bool Closed;

        internal PoolReservation(SessionEngine owner, SessionPool pool, string oldId, string newId,
            long version, bool hadMember, bool hadIndex)
        {
            Owner = owner; Pool = pool; OldId = oldId; NewId = newId;
            Version = version; HadMember = hadMember; HadIndex = hadIndex;
        }

        public void MarkPublished() => Owner.MarkReservationPublished(this);
    }

    internal PoolReservation? BeginPoolReservation(string oldId, string newId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newId);
        lock (_poolLock)
        {
            if (!_poolByMember.TryGetValue(oldId, out var pool) || pool.ActiveSessionId != oldId) return null;
            var hadIndex = _poolByMember.TryGetValue(newId, out var indexed);
            if (hadIndex && !ReferenceEquals(indexed, pool)) return null;
            var hadMember = pool.Members.Contains(newId);
            pool.TakeOver(newId);
            _poolByMember[newId] = pool;
            return new PoolReservation(this, pool, oldId, newId, pool.ActiveVersion, hadMember, hadIndex);
        }
    }

    private void MarkReservationPublished(PoolReservation ticket)
    {
        lock (_poolLock) ticket.Closed = true;
    }

    internal bool TryCancelReservation(PoolReservation ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        lock (_poolLock)
        {
            if (!ReferenceEquals(ticket.Owner, this) || ticket.Closed) return false;
            var pool = ticket.Pool;
            if (pool.ActiveVersion != ticket.Version || pool.ActiveSessionId != ticket.NewId
                || !_poolByMember.TryGetValue(ticket.NewId, out var current) || !ReferenceEquals(current, pool)
                || !_poolByMember.TryGetValue(ticket.OldId, out var old) || !ReferenceEquals(old, pool)) return false;
            // Reclaimed/replaced roots must never be resurrected by an old ticket.
            if (pool.IsRoot && (!_pools.TryGetValue(pool.ChannelId, out var root) || !ReferenceEquals(root, pool))) return false;
            pool.TakeOver(ticket.OldId);
            if (!ticket.HadMember) pool.Members.Remove(ticket.NewId);
            if (!ticket.HadIndex) _poolByMember.TryRemove(ticket.NewId, out _);
            ticket.Closed = true;
            return true;
        }
    }
}
