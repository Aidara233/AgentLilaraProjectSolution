using System;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 断言槽位（subject/predicate/object）。以 JSON 存入 Claims 表。
    /// { type, index, ref? }：type 决定槽位性质，index 存表面词，ref 用于 assertion 引用。
    /// 实体身份 = 槽位组合，不做实体归并。
    /// </summary>
    public class ClaimSlot
    {
        /// <summary>槽位类型：entity | keyword | attribute | literal | assertion</summary>
        public string Type { get; set; } = "entity";

        /// <summary>表面词（index）</summary>
        public string Index { get; set; } = "";

        /// <summary>引用 Id（assertion 槽位引用断言 Id；其余类型可空）</summary>
        public int? Ref { get; set; }

        public string ToJson() => JsonConvert.SerializeObject(this);

        public static ClaimSlot? FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json) || json == "{}") return null;
            try { return JsonConvert.DeserializeObject<ClaimSlot>(json); }
            catch { return null; }
        }
    }
}
