using System;
using System.Text;
using AgentCoreProcessor.Client;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Models;

namespace AgentCoreProcessor
{
    class Program
    {
        private static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            PreprocessingCore ec = new();
            await ec.GenerateAsync(OnDelta, OnBreak);

            return 0;
        }

        public static void OnDelta(ApiResponse response)
        {
            Console.Write(response.Choices[0].Delta?.ReasoningContent);
            Console.Write(response.Choices[0].Delta?.Content);
        }

        public static void OnBreak(ResponseBlock block)
        {
            Console.WriteLine($"\n[Break Detected] Type: {block.Name}, Content: {block.Content}\n");
        }
    }
}