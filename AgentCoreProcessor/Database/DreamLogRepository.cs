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

        public async Task CreateDetailsAsync(List<DreamFragmentDetail> details)
        {
            foreach (var d in details)
                await db.InsertAsync(d);
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

        public async Task<DreamFragment?> GetFragmentByIdAsync(int fragmentId)
        {
            var results = await db.QueryAsync<DreamFragment>(
                "SELECT * FROM DreamFragments WHERE Id = ? LIMIT 1", fragmentId);
            return results.Count > 0 ? results[0] : null;
        }

        public Task<int> GetSessionCountAsync()
            => db.Table<DreamSession>().CountAsync();

        public async Task<DreamSession?> GetSessionByIdAsync(int id)
        {
            var results = await db.QueryAsync<DreamSession>(
                "SELECT * FROM DreamSessions WHERE Id = ? LIMIT 1", id);
            return results.Count > 0 ? results[0] : null;
        }

        public Task UpdateSessionAsync(DreamSession session)
            => db.UpdateAsync(session);
    }
}
