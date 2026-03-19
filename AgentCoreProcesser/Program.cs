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
            Console.Write(ec.Generate());

            await Task.Delay(1); // 模拟一些异步操作
            return 0;
        }
    }
}