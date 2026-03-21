using System;
using System.Text;
using AgentCoreProcesser.Client;
using AgentCoreProcesser.Core;
using AgentCoreProcesser.Models;

namespace AgentCoreProcesser  // 建议用你的项目名替换
{
    class Program
    {
        private static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            PreprocessingCore ec = new();
            await ec.GenerateAsync(OnDelta,OnBreak);

            await Task.Delay(1); // 模拟一些异步操作
            return 0;
        }

        public static void OnDelta(ApiStreamResponse response)
        {
            Console.Write(response.Choices[0].Delta?.ReasoningContent);
            Console.Write(response.Choices[0].Delta?.Content);
        }

        public static void OnBreak(ResponseBlock block)
        {
            Console.WriteLine($"\n[Break Detected] Type: {block.name}, Content: {block.content}\n");
        }
    }
}