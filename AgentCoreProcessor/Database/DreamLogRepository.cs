using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Database
{
    internal class DreamLogRepository
    {
        private readonly DbManager db;

        public DreamLogRepository(DbManager db) => this.db = db;

        public async Task<DreamSession> CreateSessionAsync(DreamSession session)
        {
            await db.InsertAsync(session);
            return session;
        }

        public async Task<DreamFragment> CreateFragmentAsync(DreamFragment fragment)
        {
            await db.InsertAsync(fragment);
            return fragment;
        }

        public Task CreateDetailAsync(DreamFragmentDetail detail)
            => db.InsertAsync(detail);

        public Task CreateDetailsAsync(List<DreamFragmentDetail> details)
        {
            var tasks = new List<Task>();
            foreach (var d in details)
                tasks.Add(db.InsertAsync(d));
            return Task.WhenAll(tasks);
        }

        public Task<List<DreamSession>> GetRecentSessionsAsync(int limit = 20)
            => db.QueryAsync<DreamSession>(
                "SELECT * FROM DreamSessions ORDER BY StartTime DESC LIMIT ?", limit);

        public Task<List<DreamFragment>> GetFragmentsBySessionAsync(int sessionId)
            => db.QueryAsync<DreamFragment>(
                "SELECT * FROM DreamFragments WHERE SessionId = ? ORDER BY SeqIndex ASC", sessionId);

        public Task<List<DreamFragmentDetail>> GetDetailsByFragmentAsync(int fragmentId)
            => db.QueryAsync<DreamFragmentDetail>(
                "SELECT * FROM DreamFragmentDetails WHERE FragmentId = ? ORDER BY Id ASC", fragmentId);

        public Task<int> GetSessionCountAsync()
            => db.Table<DreamSession>().CountAsync();
    }
}
