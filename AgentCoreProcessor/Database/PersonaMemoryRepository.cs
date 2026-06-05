using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 人设记忆库数据访问。全量读取（库小），向量检索在 MemoryService 层做。
    /// </summary>
    internal class PersonaMemoryRepository
    {
        private readonly DbManager db;

        public PersonaMemoryRepository(DbManager db) => this.db = db;

        public Task<List<PersonaMemoryEntry>> GetAllAsync()
            => db.GetAllAsync<PersonaMemoryEntry>();

        public async Task<PersonaMemoryEntry> CreateAsync(string content, byte[]? embedding, string? category = null,
            string? embeddingModel = null)
        {
            var entry = new PersonaMemoryEntry
            {
                Content = content,
                Embedding = embedding,
                Category = category,
                EmbeddingModel = embeddingModel
            };
            await db.InsertAsync(entry);
            return entry;
        }

        public Task<int> DeleteAsync(PersonaMemoryEntry entry) => db.DeleteAsync(entry);

        public Task<int> UpdateAsync(PersonaMemoryEntry entry) => db.UpdateAsync(entry);

        public async Task<int> GetCountAsync()
        {
            var result = await db.QueryAsync<CountResult>(
                "SELECT COUNT(*) AS Value FROM PersonaMemories");
            return result.Count > 0 ? result[0].Value : 0;
        }

        /// <summary>获取全部人设记忆的 Id 和 Content（用于重建 embedding）。</summary>
        public Task<List<StubEntry>> GetAllStubsAsync()
            => db.QueryAsync<StubEntry>("SELECT Id, Content FROM PersonaMemories");

        /// <summary>批量更新 embedding 和模型标记。</summary>
        public async Task BatchUpdateEmbeddingsAsync(List<(int Id, byte[] Embedding, string Model)> updates)
        {
            if (updates.Count == 0) return;
            foreach (var (id, embedding, model) in updates)
            {
                await db.ExecuteAsync(
                    "UPDATE PersonaMemories SET Embedding = ?, EmbeddingModel = ? WHERE Id = ?",
                    embedding, model, id);
            }
        }
    }
}
