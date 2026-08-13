using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Adapter
{
    /// <summary>
    /// 合并转发展开的单条消息节点。嵌套转发通过 Nested 展开成子树。
    /// </summary>
    public class ForwardNode
    {
        public string? SenderId { get; set; }
        public string? SenderName { get; set; }

        /// <summary>渲染文本（不含图片标记；图片由 RenderFullText 按序编 [图片N]）。</summary>
        public string? Text { get; set; }

        /// <summary>本节点内图片数（含无 url 的）。</summary>
        public int ImageCount { get; set; }

        /// <summary>本节点内图片 URL（按出现顺序，与 [图片N] 全局编号对应）。</summary>
        public List<string> ImageUrls { get; set; } = new();

        /// <summary>嵌套转发的 id（node.Nested 非空=已展开；为空但此项非空=获取失败或过深截断）。</summary>
        public string? NestedForwardId { get; set; }

        /// <summary>嵌套转发展开的子树节点。</summary>
        public List<ForwardNode>? Nested { get; set; }
    }

    /// <summary>
    /// 合并转发解析树 + 递归统计。解析、统计、渲染均为纯逻辑，递归拉取通过注入的 fetch 委托完成，
    /// 以便单测（传内存字典模拟 get_forward_msg 返回）。
    /// </summary>
    public class ForwardMessageTree
    {
        public string ForwardId { get; set; } = "";
        public List<ForwardNode> Roots { get; set; } = new();

        /// <summary>已识别的嵌套转发个数（含未展开的，解析时累计）。</summary>
        public int NestedForwardCount { get; set; }

        /// <summary>是否因深度超限截断了某些嵌套子树。</summary>
        public bool Truncated { get; set; }

        // ── 汇总统计（ComputeStats 填充）──

        /// <summary>顶层直接消息条数。</summary>
        public int DirectCount { get; set; }

        /// <summary>嵌套内消息总条数（递归，不含顶层直接消息）。</summary>
        public int NestedCount { get; set; }

        public int TotalCount => DirectCount + NestedCount;

        /// <summary>总字数（文本段字符数，含嵌套）。</summary>
        public int CharCount { get; set; }

        /// <summary>总图片数（含嵌套）。</summary>
        public int ImageCount { get; set; }

        /// <summary>参与者人数（distinct sender id，含嵌套）。</summary>
        public int ParticipantCount { get; set; }

        /// <summary>最大嵌套深度（顶层=1）。</summary>
        public int MaxDepth { get; set; } = 1;

        /// <summary>
        /// 构建转发树。顶层拉取失败返回 null；嵌套拉取失败/过深时对应子树降级为文本标记。
        /// </summary>
        /// <param name="fetchMessages">按 forwardId 拉取 get_forward_msg 的 data.messages 数组，失败返回 null。</param>
        public static async Task<ForwardMessageTree?> BuildAsync(
            string forwardId, Func<string, Task<JArray?>> fetchMessages, int maxDepth)
        {
            var tree = new ForwardMessageTree { ForwardId = forwardId };
            var roots = await BuildLevelAsync(forwardId, fetchMessages, maxDepth, 1, tree);
            if (roots == null) return null;
            tree.Roots = roots;
            tree.ComputeStats();
            return tree;
        }

        private static async Task<List<ForwardNode>?> BuildLevelAsync(
            string forwardId, Func<string, Task<JArray?>> fetchMessages, int maxDepth, int depth,
            ForwardMessageTree tree)
        {
            var messages = await fetchMessages(forwardId);
            if (messages == null) return null;

            var nodes = new List<ForwardNode>();
            foreach (var token in messages)
            {
                if (token is not JObject msg) continue;
                nodes.Add(await ParseMessageAsync(msg, fetchMessages, maxDepth, depth, tree));
            }
            return nodes;
        }

        private static async Task<ForwardNode> ParseMessageAsync(
            JObject msg, Func<string, Task<JArray?>> fetchMessages, int maxDepth, int depth,
            ForwardMessageTree tree)
        {
            var node = new ForwardNode();
            (node.SenderId, node.SenderName) = ExtractSender(msg);

            // message 数组两种位置：OB11 完整对象在顶层 message，node 格式在 data.message
            var segs = msg["message"] as JArray ?? msg["data"]?["message"] as JArray;
            if (segs == null)
            {
                // 无 message 数组时退化为 raw_message（CQ 码）原文
                node.Text = msg["raw_message"]?.ToString() ?? "";
                return node;
            }

            var sb = new StringBuilder();
            foreach (var seg in segs)
            {
                var type = seg["type"]?.ToString();
                if (seg["data"] is not JObject segData) continue;

                switch (type)
                {
                    case "text":
                        sb.Append(segData["text"]?.ToString() ?? "");
                        break;

                    case "at":
                        var qq = segData["qq"]?.ToString();
                        var name = segData["name"]?.ToString();
                        sb.Append($"@{(!string.IsNullOrEmpty(name) ? name : qq)} ");
                        break;

                    case "image":
                        node.ImageCount++;
                        var url = segData["url"]?.ToString();
                        if (!string.IsNullOrEmpty(url)) node.ImageUrls.Add(url);
                        break;

                    case "forward":
                        var subId = segData["id"]?.ToString();
                        if (string.IsNullOrEmpty(subId)) break;
                        tree.NestedForwardCount++;
                        if (depth < maxDepth)
                        {
                            var subNodes = await BuildLevelAsync(subId, fetchMessages, maxDepth, depth + 1, tree);
                            if (subNodes != null)
                            {
                                node.Nested = subNodes;
                                node.NestedForwardId = subId;
                                sb.Append($"[合并转发 id={subId}]");
                            }
                            else
                            {
                                sb.Append($"[合并转发 id={subId}：获取失败]");
                            }
                        }
                        else
                        {
                            tree.Truncated = true;
                            sb.Append($"[合并转发 id={subId}：嵌套过深未展开]");
                        }
                        break;
                }
            }
            node.Text = sb.ToString();
            return node;
        }

        private static (string? Id, string? Name) ExtractSender(JObject msg)
        {
            // node 格式：data.user_id / data.nickname
            if (msg["data"] is JObject data)
            {
                var uid = data["user_id"]?.ToString();
                var nick = data["nickname"]?.ToString();
                if (!string.IsNullOrEmpty(uid) || !string.IsNullOrEmpty(nick))
                    return (uid, nick);
            }

            // OB11 完整对象：sender.card / sender.nickname / sender.user_id
            var sender = msg["sender"] as JObject;
            var userId = sender?["user_id"]?.ToString() ?? msg["user_id"]?.ToString();
            var card = sender?["card"]?.ToString();
            var nickname = sender?["nickname"]?.ToString();
            var displayName = !string.IsNullOrWhiteSpace(card) ? card : nickname;
            return (userId, displayName);
        }

        // ── 统计 ──

        private void ComputeStats()
        {
            DirectCount = Roots.Count;
            CharCount = 0;
            ImageCount = 0;
            ParticipantCount = 0;
            NestedCount = 0;
            MaxDepth = 1;

            var participants = new HashSet<string>();
            foreach (var root in Roots)
                Accumulate(root, 1, participants);

            ParticipantCount = participants.Count;
        }

        private void Accumulate(ForwardNode node, int depth, HashSet<string> participants)
        {
            if (!string.IsNullOrEmpty(node.SenderId))
                participants.Add(node.SenderId);

            CharCount += node.Text?.Length ?? 0;
            ImageCount += node.ImageCount;
            if (depth > MaxDepth) MaxDepth = depth;

            if (node.Nested != null)
            {
                NestedCount += node.Nested.Count;
                foreach (var sub in node.Nested)
                    Accumulate(sub, depth + 1, participants);
            }
        }

        // ── 渲染 ──

        /// <summary>入站自动统计用的紧凑统计行（拆分版：直接/嵌套拆分 + 字数/图数/人数 + 嵌套个数与深度）。</summary>
        public string RenderSummary()
        {
            var sb = new StringBuilder();
            sb.Append($"[合并转发 id={ForwardId}：\n");
            if (NestedForwardCount > 0)
            {
                sb.AppendLine($"  直接{DirectCount}条 + 嵌套{NestedCount}条 = 共{TotalCount}条");
                sb.AppendLine($"  | 约{CharCount}字 | {ImageCount}图 | {ParticipantCount}人");
                sb.Append($"  | 嵌套{NestedForwardCount}个(最深{MaxDepth}层)");
            }
            else
            {
                sb.AppendLine($"  共{TotalCount}条");
                sb.Append($"  | 约{CharCount}字 | {ImageCount}图 | {ParticipantCount}人");
            }
            if (Truncated) sb.AppendLine("\n  | 更深层未展开");
            sb.Append("]");
            return sb.ToString();
        }

        /// <summary>完整展开文本（含嵌套缩进）。ImageUrls 与文本中的 [图片N] 一一对应。</summary>
        public (string FullText, List<string> ImageUrls) RenderFullText()
        {
            var sb = new StringBuilder();
            var urls = new List<string>();

            sb.Append($"[合并转发 id={ForwardId}：共{TotalCount}条 | 约{CharCount}字 | {ImageCount}图 | {ParticipantCount}人");
            if (NestedForwardCount > 0) sb.Append($" | 嵌套{NestedForwardCount}个(最深{MaxDepth}层)");
            sb.AppendLine("]");
            if (Truncated) sb.AppendLine("[部分嵌套转发过深，未完全展开]");

            int imgIndex = 0;
            int idx = 0;
            foreach (var root in Roots)
            {
                idx++;
                AppendNode(sb, urls, root, idx, 0, ref imgIndex);
            }
            return (sb.ToString().TrimEnd(), urls);
        }

        private static void AppendNode(
            StringBuilder sb, List<string> urls, ForwardNode node, int index, int indent, ref int imgIndex)
        {
            var pad = new string(' ', indent * 2);
            var text = node.Text ?? "";

            if (node.ImageUrls.Count > 0)
            {
                foreach (var url in node.ImageUrls)
                {
                    imgIndex++;
                    urls.Add(url);
                    text += $" [图片{imgIndex}]";
                }
            }

            var sender = !string.IsNullOrEmpty(node.SenderName) ? node.SenderName
                : !string.IsNullOrEmpty(node.SenderId) ? node.SenderId
                : "?";
            sb.Append(pad).AppendLine($"[{index}] {sender}: {text}");

            if (node.Nested != null)
            {
                int subIdx = 0;
                foreach (var sub in node.Nested)
                {
                    subIdx++;
                    AppendNode(sb, urls, sub, subIdx, indent + 1, ref imgIndex);
                }
            }
        }
    }
}
