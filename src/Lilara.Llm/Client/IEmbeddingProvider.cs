using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Client
{
    /// <summary>
    /// 向量嵌入提供者接口。起步用云端 API，后续可切 ONNX Runtime 本地推理。
    /// </summary>
    internal interface IEmbeddingProvider
    {
        /// <summary>获取单条文本的向量嵌入。</summary>
        Task<float[]> GetEmbeddingAsync(string text);

        /// <summary>批量获取向量嵌入。</summary>
        Task<List<float[]>> GetEmbeddingsAsync(List<string> texts);
    }
}
