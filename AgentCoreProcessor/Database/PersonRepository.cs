using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 自然人数据访问，提供创建、查询、合并等操作。
    /// </summary>
    internal class PersonRepository
    {
        private readonly DbManager db;

        public PersonRepository(DbManager db) => this.db = db;

        /// <summary>创建新 Person（默认 TrustLevel=Unknown）。</summary>
        public async Task<Person> CreateAsync(string name = "")
        {
            var person = new Person
            {
                Name = name,
                TrustLevel = TrustLevel.Unknown,
                TrustProgress = 0f,
                CreatedAt = DateTime.Now
            };
            await db.InsertAsync(person);
            return person;
        }

        public Task<Person?> GetByIdAsync(int id) => db.GetByIdAsync<Person>(id);

        public Task<int> UpdateAsync(Person person) => db.UpdateAsync(person);

        public Task<List<Person>> GetAllAsync() => db.GetAllAsync<Person>();

    }
}
