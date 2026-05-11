using System;
using System.Collections.Generic;
using System.Text;

namespace AgentCoreProcessor.Engine.Modules
{
    /// <summary>
    /// 系统通知模块。频道循环专用。
    /// 消费系统循环注入的通知，注入 prompt 让模型感知并自行决定如何回应。
    /// </summary>
    internal class SystemNotificationModule : EngineModule
    {
        public override string Name => "系统通知";
        public override int PromptPriority => 41;

        private readonly Func<List<string>> drainNotifications;

        public SystemNotificationModule(Func<List<string>> drainNotifications)
        {
            this.drainNotifications = drainNotifications;
        }

        public override void Attach(ILoopBus bus) { }

        public override string? BuildPromptSection(EngineMode mode)
        {
            var notifications = drainNotifications();
            if (notifications.Count == 0) return null;

            var sb = new StringBuilder("[系统通知]\n");
            sb.AppendLine("以下是系统循环发来的通知，请根据内容和上下文自行决定是否需要回应频道中的用户：");
            foreach (var n in notifications)
            {
                sb.AppendLine($"- {n}");
            }
            return sb.ToString();
        }
    }
}
