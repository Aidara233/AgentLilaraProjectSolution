using System.Threading.Tasks;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 用户数据访问，提供按平台ID查找、创建用户等操作。
    /// </summary>
    internal class UserRepository
    {
        private readonly DbManager db;

        public UserRepository(DbManager db) => this.db = db;

        /// <summary>
        /// 按平台和平台用户ID查找内部用户。
        /// 用于将 Adapter 层的外部用户映射到内部 User 记录。
        /// </summary>
        public async Task<User?> FindByPlatformAsync(string platform, string platformId)
        {
            var results = await db.Table<User>()
                .Where(u => u.Platform == platform && u.PlatformId == platformId)
                .ToListAsync();
            return results.Count > 0 ? results[0] : null;
        }

        /// <summary>
        /// 查找已有用户，不存在则自动创建（默认 TrustLevel = Unknown）。
        /// </summary>
        public async Task<User> FindOrCreateAsync(string platform, string platformId)
        {
            var user = await FindByPlatformAsync(platform, platformId);
            if (user != null) return user;

            user = new User
            {
                Platform = platform,
                PlatformId = platformId,
                TrustLevel = TrustLevel.Unknown,
                FastMemory = ""
            };
            await db.InsertAsync(user);
            return user;
        }

        public Task<User?> GetByIdAsync(int id) => db.GetByIdAsync<User>(id);

        public Task<int> UpdateAsync(User user) => db.UpdateAsync(user);
    }
}
