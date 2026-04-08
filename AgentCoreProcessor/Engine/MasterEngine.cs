using System;
using System.IO;
using System.Threading.Tasks;
using AgentCoreProcessor.Core;

namespace AgentCoreProcessor.Engine
{
    //主引擎，负责接收用户输入，调用核心进行处理，并返回结果
    internal class MasterEngine
    {
        private static readonly string DefaultDatabasePath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Storage", "Database");

        private string databaseDirectory;

        public string DatabaseDirectory
        {
            get => databaseDirectory;
            set => databaseDirectory = value;
        }

        public MasterEngine(string? databaseDirectory = null)
        {
            this.databaseDirectory = databaseDirectory ?? DefaultDatabasePath;
        }

        public Task EngineMain()
        {
            return Task.CompletedTask;
        }

        public void EngineThread(EngineRequest request)
        {

        }

        /// <summary>
        /// 预处理，主要负责进行分类，判断是否需要调用核心进行处理，或者直接返回结果等逻辑
        /// </summary>
        /// <param name="request">请求体</param>
        /// <returns>无</returns>
        public Task PreProcess(EngineRequest request)
        {
            return Task.CompletedTask;
        }
    }
}
