using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Database
{
    internal class PersonTraitRepository
    {
        private readonly DbManager db;

        public PersonTraitRepository(DbManager db) => this.db = db;

        public Task<List<PersonTrait>> GetByPersonAsync(int personId)
            => db.QueryAsync<PersonTrait>(
                "SELECT * FROM PersonTraits WHERE PersonId = ? ORDER BY Confidence DESC", personId);

        public Task<List<PersonTrait>> GetByCategoryAsync(int personId, string category)
            => db.QueryAsync<PersonTrait>(
                "SELECT * FROM PersonTraits WHERE PersonId = ? AND Category = ? ORDER BY Confidence DESC",
                personId, category);

        public async Task<PersonTrait> UpsertAsync(int personId, string category, string key,
            string value, float confidence, string sourceHint = "")
        {
            var existing = await db.QueryAsync<PersonTrait>(
                "SELECT * FROM PersonTraits WHERE PersonId = ? AND Category = ? AND Key = ? LIMIT 1",
                personId, category, key);
            var trait = existing.FirstOrDefault();
            if (trait != null)
            {
                trait.Value = value;
                trait.Confidence = confidence;
                if (!string.IsNullOrEmpty(sourceHint))
                    trait.SourceHint = sourceHint;
                trait.UpdatedAt = System.DateTime.Now;
                await db.UpdateAsync(trait);
            }
            else
            {
                trait = new PersonTrait
                {
                    PersonId = personId,
                    Category = category,
                    Key = key,
                    Value = value,
                    Confidence = confidence,
                    SourceHint = sourceHint
                };
                await db.InsertAsync(trait);
            }
            return trait;
        }

        public Task<int> DeleteAsync(PersonTrait trait) => db.DeleteAsync(trait);
    }
}
