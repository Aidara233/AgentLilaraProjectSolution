using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Database
{
    internal class ReviewLogRepository
    {
        private readonly DbManager db;

        public ReviewLogRepository(DbManager db) => this.db = db;

        public async Task<ReviewSession> CreateSessionAsync(ReviewSession session)
        {
            await db.InsertAsync(session);
            return session;
        }

        public Task UpdateSessionAsync(ReviewSession session)
            => db.UpdateAsync(session);

        public Task CreateActionAsync(ReviewAction action)
            => db.InsertAsync(action);

        public Task<List<ReviewSession>> GetRecentSessionsAsync(int limit = 20)
            => db.QueryAsync<ReviewSession>(
                "SELECT * FROM ReviewSessions ORDER BY StartTime DESC LIMIT ?", limit);

        public Task<List<ReviewAction>> GetActionsBySessionAsync(int sessionId)
            => db.QueryAsync<ReviewAction>(
                "SELECT * FROM ReviewActions WHERE SessionId = ? ORDER BY SeqIndex ASC", sessionId);

        public Task<int> GetSessionCountAsync()
            => db.Table<ReviewSession>().CountAsync();
    }
}
