# 模块8审计报告：数据库层 (Database)

审计时间：2026-05-26
文件数：25 | 核心实现 ~2,400 行（含 14 个 Repository + 15 个实体类）

---

## 发现问题

### 🔴 BUG — 中度

**1. SQL 字符串插值模式（6 处，跨 4 文件）** ✅ 已修复 2026-05-26 (`MemoryRepository.cs:69,128`; `MemoryLinkRepository.cs:25,93`; `TempMemoryRepository.cs:98`; `MessageRepository.cs:123`)
```csharp
// 典型模式：
var idList = string.Join(",", ids);
return await db.QueryAsync<MemoryEntry>($"SELECT * FROM Memories WHERE Id IN ({idList})");
```
全部传入 `List<int>` 目前安全，但模式危险——若将来改为接受外部字符串输入即构成 SQL 注入。6 个位置应统一改为参数化：先生成 `?,?,?` 占位符，再传参。`MessageRepository.GetDistinctDaysByUsersAsync` 是新增发现（之前模块5未覆盖 MessageRepository）。

**2. PersonaMemoryRepository.GetCountAsync 全量加载含 BLOB 列** (`PersonaMemoryRepository.cs:34-38`)
```csharp
public async Task<int> GetCountAsync()
{
    var all = await GetAllAsync();
    return all.Count;
}
```
`GetAllAsync` 加载所有 `PersonaMemoryEntry`，每行含 `Embedding` BLOB（float[1024] ≈ 4KB/条）。100条人设记忆 = 400KB BLOB 数据从 SQLite 解序列化出来只为取 `.Count`。应改为 `SELECT COUNT(*) FROM PersonaMemories`。

**3. UserRepository.FindOrCreateAsync Person+User 不在事务中** ✅ 已修复 2026-05-26 (`UserRepository.cs:42-51`)
```csharp
var person = await persons.CreateAsync();  // Person 已写入 DB
user = new User { PersonId = person.Id, ... };
await db.InsertAsync(user);                // 如果失败，Person 成为孤儿记录
```
Person 创建和 User 创建不是原子的。如果 User 插入失败（UNIQUE 冲突、磁盘满），Person 记录成为孤儿——无任何 User 关联，且无清理路径。

### 🟡 BUG — 轻度

**4. ImageRepository.GetFilteredCountAsync 滥用 ImageRecord 实体接收 COUNT** (`ImageRepository.cs:147-152`)
```csharp
var sql = "SELECT COUNT(*) as Id FROM ImageRecords";
var results = await db.QueryAsync<ImageRecord>(sql, args.ToArray());
return results.Count > 0 ? results[0].Id : 0;
```
将 `COUNT(*)` 别名映射到 `ImageRecord.Id`。虽然能运行（类型匹配），但语义扭曲——sqlite-net 会对 `ImageRecord` 的所有列做映射尝试。`MessageRepository` 使用了专门的 `CountResult` 类，此处应统一。

**5. DreamLogRepository.CreateDetailsAsync 批量插入无事务** ✅ 已修复 2026-05-26 (`DreamLogRepository.cs:27-33`)
```csharp
public Task CreateDetailsAsync(List<DreamFragmentDetail> details)
{
    var tasks = new List<Task>();
    foreach (var d in details)
        tasks.Add(db.InsertAsync(d));
    return Task.WhenAll(tasks);
}
```
多条 detail 用 `Task.WhenAll` 并行插入但无事务包裹。中途失败 → 部分 detail 已写入、部分未写入。Dream 片段详情数据半完整，查询时会看到不完整的结果集。

**6. DbManager.InitAsync 迁移 catch 裸吞异常** ✅ 已修复 2026-05-26 (`DbManager.cs:51-52`)
```csharp
try { await db.ExecuteAsync("ALTER TABLE ModelCallLogs ADD COLUMN IsError INTEGER NOT NULL DEFAULT 0"); }
catch { /* 列已存在则忽略 */ }
```
裸 `catch {}` 吞掉所有异常类型。如果 ALTER TABLE 因磁盘满、表锁定、权限等非"列已存在"原因失败，也会被静默忽略——后续 INSERT 因缺少列而失败，根因被掩盖。应至少检查异常消息包含 "duplicate column"。

**7. EvaluationScoreRepository.GetAsync 使用 ContinueWith 而非 await** (`EvaluationScoreRepository.cs:17-21`)
```csharp
return db.QueryAsync<EvaluationScore>(...)
    .ContinueWith(t => t.Result.Count > 0 ? t.Result[0] : null);
```
`ContinueWith` 不捕获 SynchronizationContext（虽 Repository 层不需要），但更重要的是：如果 `QueryAsync` 内部抛异常，`ContinueWith` 将其包装为 `AggregateException`，调用方的 `catch (Exception)` 捕获不到原始异常。与全项目 async/await 风格不一致。

**8. ImageRepository.IncrementSeenCountAsync 读-改-写竞态** ✅ 已修复 2026-05-26 (`ImageRepository.cs:34-42`)
```csharp
var record = await GetByHashAsync(hash);
if (record != null) {
    record.SeenCount++;
    await db.UpdateAsync(record);
}
```
两个并发请求同时读到 `SeenCount=5`，都写入 `SeenCount=6` → 丢失一次计数。对 SeenCount 这种非关键统计字段影响可忽略，但模式本身存在。应 `UPDATE ImageRecords SET SeenCount = SeenCount + 1 WHERE Hash = ?`。

**9. ChannelRepository.FindByNameAsync 多余 ToList** (`ChannelRepository.cs:17-21`)
```csharp
var results = await db.Table<Channel>().Where(c => c.Name == name).ToListAsync();
return results.Count > 0 ? results[0] : null;
```
频道名基本唯一（`FindOrCreateAsync` 保证），`ToListAsync` 加载全部匹配行。应用 `FirstOrDefaultAsync()` 省去多余的对象分配。

### 🟠 设计问题 — 中度

**10. MemoryRepository.DeleteExpiredAsync + MemoryLinkRepository.DeleteOrphanedAsync 逐条 N+1** (MemoryRepository:109-121, MemoryLinkRepository:65-78)
两个低频清理方法都采用"全查 → foreach 逐条删"模式。与模块5已发现的 `TempMemoryRepository.ClearAllAsync` 相同问题。应直接用 SQL：
- `DELETE FROM Memories WHERE IsPersistent = 0 AND ExpiresAt IS NOT NULL AND ExpiresAt < ?`
- `DELETE FROM MemoryLinks WHERE NOT EXISTS (SELECT 1 FROM Memories...)`

**11. DbManager 迁移方案无版本号** (`DbManager.cs:34-56`)
`InitAsync` 是线性的 `CreateTableAsync` 序列 + 硬编码的 try-catch ALTER TABLE。没有版本号追踪、没有迁移顺序保证。如果未来需要多个 ALTER（加索引、改列类型、创建视图），`InitAsync` 会积累大量 try-catch 块，且无法回滚失败的迁移。应引入 `PRAGMA user_version` 或迁移表。

**12. 热点路径全量扫描无 Repository 层缓存** — `MemoryRepository.GetAllAsync` 被 `GetAllWithMatchScoreAsync` 和 `FindSimilarAsync` 每次调用，这两个方法在每个频道循环中都被 MemoryService 调用。SQLite 的页缓存（WAL 模式下有帮助）和 C# 层完全不缓存查询结果。短期 TTL 缓存（如 30s）可显著减少热点路径上的内存分配和反序列化开销。

### 🟢 ISSUE — 轻度

**13. MemoryRepository 与 TempMemoryRepository GetAllWithMatchScoreAsync 评分逻辑重复** (MemoryRepository.cs:22-43, TempMemoryRepository.cs:49-66)
两份完全相同的评分逻辑（person匹配+1, channel匹配+1, Knowledge类型+1）。其中一个修改后另一个容易忘记同步。

**14. MessageRepository.GetContextAroundAsync 三个独立查询可合并** (`MessageRepository.cs:41-59`)
取前后上下文用 3 次独立 `QueryAsync`（before + target + after），返回后在内存中拼接。可用一个 UNION ALL 查询一次完成。

**15. ImageRepository.GetPagedAsync 和 GetFilteredCountAsync 过滤条件重复** — 两个方法有 ~30 行完全相同的 WHERE 子句构建代码。其中一个改过滤逻辑时容易漏掉另一个。

---

## 正面发现

- **DbManager WAL 模式设置正确**：用独立 `SqliteConnection` 设 PRAGMA，避免 sqlite-net-pcl 的误抛异常；`synchronous=NORMAL` 平衡性能和安全
- **DbManager 封装层次清晰**：泛型 CRUD 方法 + `QueryAsync` + `Table<T>()` 链式查询，Repository 全部委托给 DbManager
- **实体设计一致**：所有实体用 `[Table]` + `[PrimaryKey, AutoIncrement]` 标准模式，列名/类型与业务含义对应清晰
- **MessageRepository 查询设计丰富**：分页搜索/前后上下文/锚点范围/关键词搜索，覆盖了频道循环的查询需求
- **ImageRepository 参数化动态查询正确**：`GetPagedAsync` 和 `GetFilteredCountAsync` 用 `List<object>` 动态构建 WHERE，避免了手动拼 SQL 的注入风险
- **ReviewHintRepository 读写分离清晰**：`GetUnprocessedAsync` + `MarkProcessedAsync` + `DeleteProcessedAsync` 生命周期明确
- **MemoryLinkRepository 双向匹配正确**：`CreateOrUpdateAsync` 检查 `(a,b) OR (b,a)`，`DeleteOrphanedForMemoryAsync` 清理双向
- **ModelCallLogRepository 聚合查询设计好**：`GetByCoreAsync`/`GetByModelAsync` GROUP BY 查询 token 用量统计，支持时间范围过滤
- **DreamLog 三表关联完整**：Session → Fragment → Detail 层次清晰，Fragment 的 `InputMemoryIds` 用逗号分隔 ID 列表（简单有效）
- **ReviewLog 双表结构合理**：Session（会话摘要）+ Action（操作明细），`ChannelsVisited`/`PersonsEncountered` JSON 数组存储灵活
- **所有 Repository 通过构造函数注入 DbManager**，无静态依赖，便于测试替换

---

## 判定

数据库层整体结构规整，Repository 模式贯彻一致，实体设计清晰。最大的问题是 **SQL 字符串插值模式蔓延**（6 处，跨 4 文件）——虽当前入参安全，但模式危险且传染性强。`PersonaMemoryRepository.GetCountAsync` 全量加载 BLOB 计数是实打实的性能浪费。`FindOrCreateAsync` 缺事务可能产生孤儿 Person 记录。迁移方案无版本号会导致未来数据库升级困难。建议优先修复 SQL 插值和 GetCountAsync。
