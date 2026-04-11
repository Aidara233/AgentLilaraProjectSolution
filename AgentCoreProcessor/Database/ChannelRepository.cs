using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 频道数据访问。
    /// </summary>
    internal class ChannelRepository
    {
        private readonly DbManager db;

        public ChannelRepository(DbManager db) => this.db = db;

        /// <summary>按名称查找频道。</summary>
        public async Task<Channel?> FindByNameAsync(string name)
        {
            var results = await db.Table<Channel>()
                .Where(c => c.Name == name)
                .ToListAsync();
            return results.Count > 0 ? results[0] : null;
        }

        /// <summary>查找已有频道，不存在则自动创建。</summary>
        public async Task<Channel> FindOrCreateAsync(string name)
        {
            var channel = await FindByNameAsync(name);
            if (channel != null) return channel;

            channel = new Channel { Name = name };
            await db.InsertAsync(channel);
            return channel;
        }

        public Task<Channel?> GetByIdAsync(int id) => db.GetByIdAsync<Channel>(id);

        public Task<List<Channel>> GetAllAsync() => db.GetAllAsync<Channel>();
    }
}
