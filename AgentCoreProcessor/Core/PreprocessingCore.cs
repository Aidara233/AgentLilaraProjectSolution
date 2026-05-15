using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Client;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Util;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 预处理核心。基于 Embedding 相似度的二分类：聊天 / 任务。
    /// 不使用 LLM，通过与预设锚点向量的余弦距离判断。
    /// </summary>
    internal class PreprocessingCore
    {
        private readonly IEmbeddingProvider embedding;
        private List<float[]>? anchorVectors;

        /// <summary>任务类锚点句子。</summary>
        private static readonly string[] TaskAnchors =
        {
            "帮我读取这个文件",
            "写一段代码",
            "把A文件内容复制到B",
            "分析这批数据",
            "帮我创建一个新文件",
            "搜索一下相关内容",
            "执行这个操作",
            "帮我修改一下这个",
            "查一下这个信息",
            "帮我整理这些内容"
        };

        /// <summary>判定为任务的相似度阈值。</summary>
        private const float TaskThreshold = 0.55f;

        public PreprocessingCore(IEmbeddingProvider embedding)
        {
            this.embedding = embedding;
        }

        /// <summary>
        /// 初始化锚点向量（首次分类时自动调用，也可提前调用）。
        /// </summary>
        public async Task InitAnchorsAsync()
        {
            if (anchorVectors != null) return;
            anchorVectors = await embedding.GetEmbeddingsAsync(new List<string>(TaskAnchors));
        }

        /// <summary>
        /// 对用户消息进行二分类。
        /// 返回 true 表示任务（需要工具），false 表示聊天。
        /// </summary>
        public async Task<bool> IsTaskAsync(string content)
        {
            await InitAnchorsAsync();

            float[] queryVec;
            try
            {
                queryVec = await embedding.GetEmbeddingAsync(content);
            }
            catch
            {
                // embedding 不可用时默认为聊天
                return false;
            }

            // 取与所有锚点的最高相似度
            var sims = anchorVectors!
                .Select(anchor => VectorUtil.CosineSimilarity(queryVec, anchor))
                .ToList();
            float maxSim = sims.Max();
            int bestIdx = sims.IndexOf(maxSim);


            return maxSim > TaskThreshold;
        }
    }
}