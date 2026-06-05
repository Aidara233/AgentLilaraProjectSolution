using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;

namespace AgentCoreProcessor.Client
{
    internal class OnnxEmbeddingProvider : IEmbeddingProvider, IDisposable
    {
        private readonly InferenceSession _session;
        private readonly BertChineseTokenizer _tokenizer;
        private readonly int _maxLength;
        private readonly int _hiddenDim;

        public OnnxEmbeddingProvider(EmbeddingProviderConfig config)
        {
            _maxLength = config.MaxLength;
            _tokenizer = new BertChineseTokenizer(config.TokenizerVocabPath);

            var options = new Microsoft.ML.OnnxRuntime.SessionOptions();
            options.RegisterOrtExtensions();
            if (config.ExecutionProvider == "DirectML")
            {
                try { options.AppendExecutionProvider_DML(); }
                catch { /* 降级到 CPU */ }
            }

            _session = new InferenceSession(config.OnnxModelPath, options);

            var outputInfo = _session.OutputMetadata.First().Value;
            _hiddenDim = outputInfo.Dimensions[outputInfo.Dimensions.Length - 1];
        }

        public Task<float[]> GetEmbeddingAsync(string text)
        {
            var (inputIds, attentionMask) = _tokenizer.Encode(text, _maxLength);
            var tokenTypeIds = new long[_maxLength];
            var embedResult = RunInference(inputIds, attentionMask, tokenTypeIds, 1);
            var vec = new float[_hiddenDim];
            Array.Copy(embedResult, vec, _hiddenDim);
            L2Normalize(vec);
            return Task.FromResult(vec);
        }

        public Task<List<float[]>> GetEmbeddingsAsync(List<string> texts)
        {
            int batchSize = texts.Count;
            var (inputIds, attentionMask) = _tokenizer.EncodeBatch(texts, _maxLength);
            var tokenTypeIds = new long[batchSize * _maxLength];
            var embedResult = RunInference(inputIds, attentionMask, tokenTypeIds, batchSize);

            int stride = _maxLength * _hiddenDim;
            var embeddings = new List<float[]>(batchSize);
            for (int b = 0; b < batchSize; b++)
            {
                var vec = new float[_hiddenDim];
                Array.Copy(embedResult, b * stride, vec, 0, _hiddenDim);
                L2Normalize(vec);
                embeddings.Add(vec);
            }

            return Task.FromResult(embeddings);
        }

        private float[] RunInference(
            long[] inputIds, long[] attentionMask, long[] tokenTypeIds, long batchSize)
        {
            var memInfo = OrtMemoryInfo.DefaultInstance;
            long[] shape = { batchSize, _maxLength };

            OrtValue? inputIdsValue = null;
            OrtValue? attentionMaskValue = null;
            OrtValue? tokenTypeIdsValue = null;

            try
            {
                inputIdsValue = CreateTensorValue(memInfo, inputIds, shape);
                attentionMaskValue = CreateTensorValue(memInfo, attentionMask, shape);
                tokenTypeIdsValue = CreateTensorValue(memInfo, tokenTypeIds, shape);

                var namedInputs = new Dictionary<string, OrtValue>
                {
                    ["input_ids"] = inputIdsValue,
                    ["attention_mask"] = attentionMaskValue,
                    ["token_type_ids"] = tokenTypeIdsValue,
                };

                using var results = _session.Run(new RunOptions(), namedInputs, _session.OutputNames);
                return results[0].GetTensorDataAsSpan<float>().ToArray();
            }
            finally
            {
                inputIdsValue?.Dispose();
                attentionMaskValue?.Dispose();
                tokenTypeIdsValue?.Dispose();
            }
        }

        private static OrtValue CreateTensorValue(OrtMemoryInfo memInfo, long[] data, long[] shape)
        {
            return OrtValue.CreateTensorValueFromMemory<long>(memInfo, data.AsMemory(), shape);
        }

        private static void L2Normalize(float[] vec)
        {
            float norm = 0f;
            for (int i = 0; i < vec.Length; i++)
                norm += vec[i] * vec[i];
            norm = MathF.Sqrt(norm);
            if (norm > 1e-12f)
            {
                for (int i = 0; i < vec.Length; i++)
                    vec[i] /= norm;
            }
        }

        public void Dispose()
        {
            _session?.Dispose();
        }
    }
}
