using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Client
{
    /// <summary>
    /// SiliconFlow 云端 embedding 实现。使用 BAAI/bge-large-zh-v1.5（1024 维）。
    /// 调用 OpenAI 兼容的 /v1/embeddings 接口。
    /// </summary>
    internal class SiliconFlowEmbeddingProvider : IEmbeddingProvider, IDisposable
    {
        private readonly HttpClient httpClient;
        private readonly string apiKey;
        private readonly string endpoint;
        private readonly string model;

        public SiliconFlowEmbeddingProvider(string apiKey,
            string endpoint = "https://api.siliconflow.cn/v1/embeddings",
            string model = "BAAI/bge-large-zh-v1.5")
        {
            this.apiKey = apiKey;
            this.endpoint = endpoint;
            this.model = model;
            httpClient = new HttpClient();
        }

        public async Task<float[]> GetEmbeddingAsync(string text)
        {
            var results = await GetEmbeddingsAsync(new List<string> { text });
            return results[0];
        }

        public async Task<List<float[]>> GetEmbeddingsAsync(List<string> texts)
        {
            var requestBody = new
            {
                model = this.model,
                input = texts,
                encoding_format = "float"
            };

            var json = JsonConvert.SerializeObject(requestBody);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();
            var responseObj = JObject.Parse(responseJson);
            var dataArray = responseObj["data"] as JArray
                ?? throw new InvalidOperationException("Embedding API 返回格式异常：缺少 data 字段");

            // 按 index 排序，确保与输入顺序一致
            var results = dataArray
                .OrderBy(d => d["index"]?.Value<int>() ?? 0)
                .Select(d =>
                {
                    var embeddingArray = d["embedding"] as JArray
                        ?? throw new InvalidOperationException("Embedding API 返回格式异常：缺少 embedding 字段");
                    return embeddingArray.Select(v => v.Value<float>()).ToArray();
                })
                .ToList();

            return results;
        }

        // ---- 序列化工具方法（委托给 VectorUtil）----

        /// <summary>float[] 转 byte[]，用于 SQLite BLOB 存储。</summary>
        public static byte[] FloatsToBytes(float[] floats) => Util.VectorUtil.FloatsToBytes(floats);

        /// <summary>byte[] 转 float[]，从 SQLite BLOB 读取。</summary>
        public static float[] BytesToFloats(byte[] bytes) => Util.VectorUtil.BytesToFloats(bytes);

        public void Dispose()
        {
            httpClient?.Dispose();
        }
    }
}
