using System;
using System.IO;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Client;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Memory;

namespace AgentCoreProcessor.Engine
{
    internal class MasterEngine
    {
        private static string DefaultDatabasePath => PathConfig.DatabasePath;

        private string databaseDirectory;
        private readonly AdapterManager adapterManager;
        private DbManager? db;

        /// <summary>数据库管理器，InitAsync 后可用。</summary>
        public DbManager Db => db ?? throw new InvalidOperationException("数据库尚未初始化，请先调用 InitAsync。");

        // 各 Repository，供 Engine 内部使用
        public UserRepository? Users { get; private set; }
        public PersonRepository? Persons { get; private set; }
        public ChannelRepository? Channels { get; private set; }
        public TopicRepository? Topics { get; private set; }
        public MessageRepository? Messages { get; private set; }
        public MemoryRepository? Memories { get; private set; }
        public TempMemoryRepository? TempMemories { get; private set; }
        public MemoryLinkRepository? MemoryLinks { get; private set; }
        public MemoryService? MemorySvc { get; private set; }
        public SessionManager? Session { get; private set; }

        public string DatabaseDirectory
        {
            get => databaseDirectory;
            set => databaseDirectory = value;
        }

        public MasterEngine(AdapterManager adapterManager, string? databaseDirectory = null)
        {
            this.adapterManager = adapterManager;
            this.databaseDirectory = databaseDirectory ?? DefaultDatabasePath;
        }

        /// <summary>
        /// 初始化数据库连接和所有 Repository。
        /// 应在处理任何消息之前调用一次。
        /// </summary>
        public async Task InitAsync()
        {
            // 确保数据库目录存在
            Directory.CreateDirectory(databaseDirectory);

            var dbPath = Path.Combine(databaseDirectory, "lilara.db");
            db = new DbManager(dbPath);
            await db.InitAsync();

            // 初始化各 Repository
            Persons = new PersonRepository(db);
            Users = new UserRepository(db, Persons);
            Channels = new ChannelRepository(db);
            Topics = new TopicRepository(db);
            Messages = new MessageRepository(db);
            Memories = new MemoryRepository(db);
            TempMemories = new TempMemoryRepository(db);
            MemoryLinks = new MemoryLinkRepository(db);

            // 初始化 Embedding 提供者（从 Base.json 读取 ApiKey）
            var baseConfigPath = Path.Combine(PathConfig.CoreConfigPath, "Base.json");
            var baseConfig = ApiClientCfg.FromJson(File.ReadAllText(baseConfigPath));
            var embeddingProvider = new SiliconFlowEmbeddingProvider(apiKey: baseConfig.ApiKey);

            // 初始化记忆服务
            MemorySvc = new MemoryService(Memories, TempMemories, MemoryLinks, embeddingProvider);

            // 初始化 SessionManager
            Session = new SessionManager(Users, Persons, Channels, Topics, Messages);
        }

        public async Task HandleMessageAsync(IncomingMessage message)
        {
            try
            {
                // 会话管理：用户映射、频道映射、话题归类、消息入库
                var context = await Session!.OnMessageAsync(message);

                // 权限检查
                switch (context.User.PermissionLevel)
                {
                    case PermissionLevel.Blocked:
                        FrameworkLogger.LogPermission("MasterEngine", context.User.PlatformId, "Blocked", false);
                        return;
                    case PermissionLevel.Restricted:
                        FrameworkLogger.LogPermission("MasterEngine", context.User.PlatformId, "Restricted", false);
                        return;
                }
                FrameworkLogger.Log("MasterEngine", $"消息处理: user={context.User.PlatformId} person={context.Person.Id} channel={context.Channel.Id} topic={context.Topic.Id}");

                var worker = new WorkerEngine(message, adapterManager, context, MemorySvc);
                var result = await worker.RunAsync();

                // result 为 null 时表示 Agent 循环已通过说话工具实时回复，无需再推送
                if (result != null)
                {
                    await adapterManager.SendMessageAsync(message.Platform, new OutgoingMessage
                    {
                        ChannelId = message.ChannelId,
                        Content = result
                    });
                }
            }
            catch (Exception ex)
            {
                await adapterManager.SendMessageAsync(message.Platform, new OutgoingMessage
                {
                    ChannelId = message.ChannelId,
                    Content = $"[错误] 处理消息时发生异常：{ex.Message}"
                });
            }
        }
    }
}
