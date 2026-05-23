using System;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Logging;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 报警处理器。递增惩罚机制（记录→扣分→限制+通知管理员）。
    /// </summary>
    internal static class AlertHandler
    {
        public static async Task HandleAsync(
            Person person, SessionContext sc, string reason, ISystemContext ctx)
        {
            person.AlertLevel = Math.Min(person.AlertLevel + 1, 4);
            person.LastAlertTime = DateTime.Now;


            switch (person.AlertLevel)
            {
                case 1:
                    await ctx.ReviewHints.CreateAsync($"[警报] {reason}", person.Id, sc.Channel.Id);
                    break;
                case 2:
                    person.TrustProgress -= 1.0f;
                    await ctx.ReviewHints.CreateAsync($"[警报升级] {reason}", person.Id, sc.Channel.Id);
                    break;
                case 3:
                    person.TrustProgress -= 3.0f;
                    await ctx.ReviewHints.CreateAsync($"[警报严重] {reason}", person.Id, sc.Channel.Id);
                    break;
                default:
                    person.TrustProgress -= 10.0f;
                    var users = await ctx.Session.GetAllUsersAsync();
                    foreach (var u in users.Where(u => u.PersonId == person.Id))
                    {
                        u.PermissionLevel = PermissionLevel.Restricted;
                        await ctx.Session.UpdateUserAsync(u);
                    }
                    await ctx.ReviewHints.CreateAsync(
                        $"[警报-已限制] {reason}", person.Id, sc.Channel.Id);
                    try
                    {
                        var admins = users.Where(u => u.PermissionLevel == PermissionLevel.Admin).ToList();
                        if (admins.Count > 0)
                        {
                            var admin = admins[0];
                            var channelId = $"private_{admin.PlatformId}";
                            await ctx.Adapters.SendMessageAsync(admin.Platform, new OutgoingMessage
                            {
                                ChannelId = channelId,
                                Content = $"[框架警报] Person [{person.Id}] 已被临时限制（AlertLevel={person.AlertLevel}）\n原因: {reason}"
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Signal.Error(LogGroup.Engine, "管理员告警通知失败", new { personId = person.Id, error = ex.Message });
                    }
                    break;
            }

            await ctx.Session.UpdatePersonAsync(person);
        }
    }
}