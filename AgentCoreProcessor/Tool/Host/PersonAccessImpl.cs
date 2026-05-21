using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Tool.Host
{
    internal class PersonAccessImpl : IPersonAccess
    {
        private readonly ISystemContext _ctx;

        public PersonAccessImpl(ISystemContext ctx)
        {
            _ctx = ctx;
        }

        public async Task<List<PersonSummary>> GetAllAsync()
        {
            var persons = await _ctx.Session.GetAllPersonsAsync();
            return persons.Select(ToSummary).ToList();
        }

        public async Task<PersonDetail?> GetByIdAsync(int id)
        {
            var person = await _ctx.Session.GetPersonByIdAsync(id);
            if (person == null) return null;

            var detail = new PersonDetail
            {
                Id = person.Id,
                Name = person.Name,
                Aliases = person.Aliases,
                FastMemory = person.FastMemory,
                TrustLevel = (int)person.TrustLevel,
                TrustProgress = person.TrustProgress,
                AlertLevel = person.AlertLevel
            };

            var users = await _ctx.Session.GetUsersByPersonIdAsync(id);
            detail.Accounts = users.Select(u => new UserAccount
            {
                Platform = u.Platform,
                PlatformId = u.PlatformId,
                DisplayName = u.DisplayName
            }).ToList();

            return detail;
        }

        public async Task UpdateNameAsync(int id, string name, string? aliases = null)
        {
            var person = await _ctx.Session.GetPersonByIdAsync(id);
            if (person == null) return;
            person.Name = name;
            if (aliases != null) person.Aliases = aliases;
            await _ctx.Session.UpdatePersonAsync(person);
        }

        public async Task UpdateFastMemoryAsync(int id, string fastMemory)
        {
            var person = await _ctx.Session.GetPersonByIdAsync(id);
            if (person == null) return;
            person.FastMemory = fastMemory;
            await _ctx.Session.UpdatePersonAsync(person);
        }

        public async Task<List<PersonSummary>> GetByChannelAsync(int channelId)
        {
            var messages = await _ctx.Session.GetContextByChannelAsync(channelId, limit: 50);
            var userIds = messages
                .Where(m => !m.IsFromBot && m.UserId > 0)
                .Select(m => m.UserId)
                .Distinct()
                .ToList();

            var result = new List<PersonSummary>();
            var seen = new HashSet<int>();
            foreach (var uid in userIds)
            {
                var user = await _ctx.Session.GetUserByIdAsync(uid);
                if (user == null || user.PersonId <= 0 || !seen.Add(user.PersonId)) continue;
                var person = await _ctx.Session.GetPersonByIdAsync(user.PersonId);
                if (person != null) result.Add(ToSummary(person));
            }
            return result;
        }

        private static PersonSummary ToSummary(Database.Person p) => new()
        {
            Id = p.Id,
            Name = p.Name,
            Aliases = p.Aliases,
            FastMemory = p.FastMemory,
            TrustLevel = (int)p.TrustLevel,
            TrustProgress = p.TrustProgress,
            AlertLevel = p.AlertLevel
        };
    }
}
