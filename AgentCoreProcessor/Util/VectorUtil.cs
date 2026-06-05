using System.Numerics;

namespace AgentCoreProcessor.Util
{
    /// <summary>
    /// 向量计算工具类。提供余弦相似度、float[]/byte[] 序列化等公共方法。
    /// </summary>
    internal static class VectorUtil
    {
        public const string EmbeddingModelTag = "bge-large-zh-v1.5";

        /// <summary>余弦相似度（SIMD 加速）。</summary>
        public static float CosineSimilarity(float[] a, float[] b)
        {
            if (a.Length != b.Length) return 0f;
            float dot = 0f, normA = 0f, normB = 0f;
            int i = 0;

            if (Vector.IsHardwareAccelerated && a.Length >= Vector<float>.Count)
            {
                var dotVec = Vector<float>.Zero;
                var normAVec = Vector<float>.Zero;
                var normBVec = Vector<float>.Zero;
                int simdEnd = a.Length - a.Length % Vector<float>.Count;

                for (; i < simdEnd; i += Vector<float>.Count)
                {
                    var va = new Vector<float>(a, i);
                    var vb = new Vector<float>(b, i);
                    dotVec += va * vb;
                    normAVec += va * va;
                    normBVec += vb * vb;
                }

                dot = Vector.Dot(dotVec, Vector<float>.One);
                normA = Vector.Dot(normAVec, Vector<float>.One);
                normB = Vector.Dot(normBVec, Vector<float>.One);
            }

            for (; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                normA += a[i] * a[i];
                normB += b[i] * b[i];
            }

            var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
            return denom == 0f ? 0f : dot / denom;
        }

        /// <summary>计算余弦相似度。任一向量为 null 时返回 0。</summary>
        public static float ComputeSimilarity(float[]? a, byte[]? bBytes)
        {
            if (a == null || bBytes == null || bBytes.Length == 0) return 0f;
            var b = BytesToFloats(bBytes);
            return CosineSimilarity(a, b);
        }

        /// <summary>float[] 转 byte[]，用于 SQLite BLOB 存储。</summary>
        public static byte[] FloatsToBytes(float[] floats)
        {
            var bytes = new byte[floats.Length * 4];
            Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        /// <summary>byte[] 转 float[]，从 SQLite BLOB 读取。</summary>
        public static float[] BytesToFloats(byte[] bytes)
        {
            if (bytes.Length % 4 != 0)
                throw new ArgumentException($"BLOB 数据长度 {bytes.Length} 不是 4 的倍数，可能已损坏");
            var floats = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
            return floats;
        }
    }
}
