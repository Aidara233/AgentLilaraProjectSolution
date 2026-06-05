using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AgentCoreProcessor.Client
{
    internal class BertChineseTokenizer
    {
        private readonly Dictionary<string, int> _vocab;
        private readonly Dictionary<int, string> _idToToken;
        private readonly int _unkId;
        private readonly int _clsId;
        private readonly int _sepId;

        public int VocabSize => _vocab.Count;

        public BertChineseTokenizer(string vocabPath)
        {
            _vocab = new Dictionary<string, int>();
            _idToToken = new Dictionary<int, string>();

            var lines = File.ReadAllLines(vocabPath, Encoding.UTF8);
            for (int i = 0; i < lines.Length; i++)
            {
                var token = lines[i].TrimEnd('\r', '\n');
                _vocab[token] = i;
                _idToToken[i] = token;
            }

            _unkId = _vocab.GetValueOrDefault("[UNK]", 100);
            _clsId = _vocab.GetValueOrDefault("[CLS]", 101);
            _sepId = _vocab.GetValueOrDefault("[SEP]", 102);
        }

        public (long[] InputIds, long[] AttentionMask) Encode(string text, int maxLength)
        {
            var tokens = new List<int> { _clsId };
            TokenizeText(text, tokens, maxLength - 2);
            tokens.Add(_sepId);

            int seqLen = tokens.Count;
            var inputIds = new long[maxLength];
            var attentionMask = new long[maxLength];

            for (int i = 0; i < maxLength; i++)
            {
                if (i < seqLen)
                {
                    inputIds[i] = tokens[i];
                    attentionMask[i] = 1;
                }
            }

            return (inputIds, attentionMask);
        }

        public (long[] InputIds, long[] AttentionMask) EncodeBatch(List<string> texts, int maxLength)
        {
            var batchSize = texts.Count;
            var inputIds = new long[batchSize * maxLength];
            var attentionMask = new long[batchSize * maxLength];

            for (int b = 0; b < batchSize; b++)
            {
                var tokens = new List<int> { _clsId };
                TokenizeText(texts[b], tokens, maxLength - 2);
                tokens.Add(_sepId);

                int seqLen = tokens.Count;
                int offset = b * maxLength;

                for (int i = 0; i < maxLength; i++)
                {
                    if (i < seqLen)
                    {
                        inputIds[offset + i] = tokens[i];
                        attentionMask[offset + i] = 1;
                    }
                }
            }

            return (inputIds, attentionMask);
        }

        private void TokenizeText(string text, List<int> tokens, int maxTokens)
        {
            if (string.IsNullOrEmpty(text)) return;

            var basicTokens = BasicTokenize(text);
            foreach (var basicToken in basicTokens)
            {
                if (tokens.Count >= maxTokens) return;
                TokenizeWord(basicToken, tokens, maxTokens);
            }
        }

        private static List<string> BasicTokenize(string text)
        {
            var result = new List<string>();
            var current = new StringBuilder();

            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (current.Length > 0)
                    {
                        result.Add(current.ToString());
                        current.Clear();
                    }
                    result.Add(c.ToString());
                }
                else if (char.IsAsciiLetterOrDigit(c) || c == '-')
                {
                    current.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    if (current.Length > 0)
                    {
                        result.Add(current.ToString());
                        current.Clear();
                    }
                    result.Add(c.ToString());
                }
            }

            if (current.Length > 0)
                result.Add(current.ToString());

            return result;
        }

        private void TokenizeWord(string word, List<int> tokens, int maxTokens)
        {
            if (_vocab.TryGetValue(word, out int id))
            {
                tokens.Add(id);
                return;
            }

            var subTokens = new List<int>();
            int start = 0;
            while (start < word.Length && tokens.Count + subTokens.Count < maxTokens)
            {
                int end = word.Length;
                bool found = false;
                string prefix = start > 0 ? "##" : "";

                while (end > start)
                {
                    var sub = prefix + word[start..end];
                    if (_vocab.TryGetValue(sub, out int subId))
                    {
                        subTokens.Add(subId);
                        found = true;
                        start = end;
                        break;
                    }
                    end--;
                }

                if (!found)
                {
                    subTokens.Add(_unkId);
                    start++;
                }
            }

            tokens.AddRange(subTokens);
        }
    }
}
