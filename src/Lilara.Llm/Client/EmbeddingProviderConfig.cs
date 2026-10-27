using Newtonsoft.Json;

namespace AgentCoreProcessor.Client
{
    internal class EmbeddingProviderConfig
    {
        [JsonProperty("provider")]
        public string Provider { get; set; } = "siliconflow";

        [JsonProperty("apiKey")]
        public string ApiKey { get; set; } = "";

        [JsonProperty("endpoint")]
        public string Endpoint { get; set; } = "https://api.siliconflow.cn/v1/embeddings";

        [JsonProperty("model")]
        public string Model { get; set; } = "BAAI/bge-large-zh-v1.5";

        [JsonProperty("onnxModelPath")]
        public string OnnxModelPath { get; set; } = "Models/bge-large-onnx/model.onnx";

        [JsonProperty("tokenizerVocabPath")]
        public string TokenizerVocabPath { get; set; } = "Models/bge-large-onnx/vocab.txt";

        [JsonProperty("maxLength")]
        public int MaxLength { get; set; } = 512;

        [JsonProperty("executionProvider")]
        public string ExecutionProvider { get; set; } = "CPU";
    }
}
