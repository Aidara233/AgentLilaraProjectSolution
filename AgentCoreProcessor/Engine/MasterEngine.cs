using System;
using System.IO;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Database;

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
        public ChannelRepository? Channels { get; private set; }
        public TopicRepository? Topics { get; private set; }
        public MessageRepository? Messages { get; private set; }
        public MemoryRepository? Memories { get; private set; }
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
            Users = new UserRepository(db);
            Channels = new ChannelRepository(db);
            Topics = new TopicRepository(db);
            Messages = new MessageRepository(db);
            Memories = new MemoryRepository(db);

            // 初始化 SessionManager
            Session = new SessionManager(Users, Channels, Topics, Messages);
        }

        public async Task HandleMessageAsync(IncomingMessage message)
        {
            // TODO: AuthService 鉴权

            try
            {
                // 会话管理：用户映射、频道映射、话题归类、消息入库
                var context = await Session!.OnMessageAsync(message);

                var worker = new WorkerEngine(message, adapterManager, context);
                var result = await worker.RunAsync();

                await adapterManager.SendMessageAsync(message.Platform, new OutgoingMessage
                {
                    ChannelId = message.ChannelId,
                    Content = result
                });
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
