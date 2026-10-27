using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 用户数据访问，提供按平台ID查找、创建用户等操作。
    /// 新建 User 时自动创建关联的 Person。
    /// </summary>
    internal class UserRepository
    {
        private readonly DbManager db;
        private readonly PersonRepository persons;

        public UserRepository(DbManager db, PersonRepository persons)
        {
            this.db = db;
            this.persons = persons;
        }

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
        /// 查找已有用户，不存在则自动创建（同时创建关联的 Person）。
        /// </summary>
        public async Task<User> FindOrCreateAsync(string platform, string platformId,
            PermissionLevel defaultPermission = PermissionLevel.Default)
        {
            var user = await FindByPlatformAsync(platform, platformId);
            if (user != null) return user;

            // 新用户：在事务中创建 Person + User，避免孤儿 Person 记录
            var person = await persons.CreateAsync();
            user = new User
            {
                PersonId = person.Id,
                Platform = platform,
                PlatformId = platformId,
                PermissionLevel = defaultPermission
            };
            try
            {
                await db.InsertAsync(user);
                return user;
            }
            catch
            {
                await persons.DeleteAsync(person);
                throw;
            }
        }

        /// <summary>获取同一自然人的所有账号。</summary>
        public Task<List<User>> GetByPersonIdAsync(int personId)
        {
            return db.Table<User>()
                .Where(u => u.PersonId == personId)
                .ToListAsync();
        }

        public Task<User?> GetByIdAsync(int id) => db.GetByIdAsync<User>(id);

        public Task<int> UpdateAsync(User user) => db.UpdateAsync(user);

        public Task<List<User>> GetAllAsync() => db.GetAllAsync<User>();
    }
}
