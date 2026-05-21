using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentLilara.PluginSDK.Services
{
    public interface IPersonAccess
    {
        Task<List<PersonSummary>> GetAllAsync();
        Task<PersonDetail?> GetByIdAsync(int id);
        Task UpdateNameAsync(int id, string name, string? aliases = null);
        Task UpdateFastMemoryAsync(int id, string fastMemory);
        Task<List<PersonSummary>> GetByChannelAsync(int channelId);
    }

    public class PersonSummary
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Aliases { get; set; }
        public string? FastMemory { get; set; }
        public int TrustLevel { get; set; }
        public float TrustProgress { get; set; }
        public int AlertLevel { get; set; }
    }

    public class PersonDetail : PersonSummary
    {
        public List<UserAccount> Accounts { get; set; } = new();
    }

    public class UserAccount
    {
        public string Platform { get; set; } = "";
        public string PlatformId { get; set; } = "";
        public string? DisplayName { get; set; }
    }
}
