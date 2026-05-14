# WebUI 卡片式数据驱动系统设计

## 目标

将现有硬编码 Blazor 页面重构为卡片式数据驱动架构。页面由卡片组成，卡片绑定数据源，Provider 声明页面结构。Shell 负责渲染引擎和布局，Provider 可热更新。

## 核心概念

- **Shell** — 不可热更新的内核：侧边栏、卡片渲染引擎、网格布局、认证、页面管理
- **Provider** — 可热更新的页面声明者：实现 `IWebUIProvider`，声明页面+卡片+数据源
- **Page** — 网格容器，由卡片组成，绑定一组数据源
- **Card** — 最小渲染单元，绑定数据源，声明布局约束
- **DataSource** — 页面级共享数据池，卡片各取所需

## 导航结构

```
总览

频道
  ├── 频道列表
  └── 模拟对话

系统循环
  ├── 概览
  ├── 任务队列
  ├── 子agent
  └── 睡觉请求

做梦
  ├── 状态
  ├── 配置
  └── 历史

视觉
  ├── 引擎状态
  └── 图片库

记忆
  ├── 记忆库（主库+临时，tab切换）
  ├── 关联图谱
  ├── 人物
  └── 消息历史

能力 ▸（折叠）
  ├── 工具管理
  ├── 组件配置
  └── MCP 服务器

管理 ▸（折叠）
  ├── 适配器
  ├── 实时日志
  ├── 模型调用
  ├── Token 统计
  └── 配置

──── 侧边栏底部 ────
页面管理（Shell 内置，不可卸载）
主题/退出
```

## SDK 接口定义

### IWebUIProvider

```csharp
namespace AgentLilara.PluginSDK;

/// <summary>WebUI 页面提供者。插件实现此接口声明页面。</summary>
public interface IWebUIProvider
{
    string Id { get; }
    string DisplayName { get; }
    IReadOnlyList<PageDefinition> Pages { get; }
}
```

### PageDefinition

```csharp
public class PageDefinition
{
    /// <summary>页面唯一标识，用于 URL 路由（如 "dream/status"）</summary>
    public required string Route { get; init; }

    /// <summary>导航元数据</summary>
    public required PageMeta Meta { get; init; }

    /// <summary>页面包含的卡片定义</summary>
    public required IReadOnlyList<CardDefinition> Cards { get; init; }

    /// <summary>页面级数据源定义</summary>
    public required IReadOnlyList<DataSourceDefinition> DataSources { get; init; }
}

public class PageMeta
{
    public required string Title { get; init; }
    public string? Icon { get; init; }
    public string? Group { get; init; }        // 导航分组（如 "做梦"、"记忆"）
    public int Order { get; init; }            // 组内排序
    public bool DefaultCollapsed { get; init; } // 所属组是否默认折叠
}
```

### CardDefinition

```csharp
public class CardDefinition
{
    public required string Id { get; init; }
    public required CardType Type { get; init; }
    public required string DataSourceId { get; init; }
    public required CardSchema Schema { get; init; }
    public CardLayout Layout { get; init; } = new();
    public string? Title { get; init; }
}

public enum CardType
{
    Table,
    Status,
    Form,
    Stream,
    Chat,
    Tree,
    Detail,
    Custom
}
```

### CardLayout

```csharp
public class CardLayout
{
    /// <summary>最小宽度（CSS 值，如 "300px" 或 "50%"）</summary>
    public string? MinWidth { get; init; }

    /// <summary>期望占据的列数（12列网格中）</summary>
    public int PreferredCols { get; init; } = 12;

    /// <summary>固定高度（CSS 值），null 为自适应</summary>
    public string? Height { get; init; }

    /// <summary>排列顺序（同行内）</summary>
    public int Order { get; init; }
}
```

### CardSchema（混合：C# 强类型 + JSON 可序列化）

```csharp
/// <summary>卡片 Schema 基类，各卡片类型继承实现</summary>
[JsonDerivedType(typeof(TableSchema), "table")]
[JsonDerivedType(typeof(StatusSchema), "status")]
[JsonDerivedType(typeof(FormSchema), "form")]
[JsonDerivedType(typeof(StreamSchema), "stream")]
[JsonDerivedType(typeof(ChatSchema), "chat")]
[JsonDerivedType(typeof(TreeSchema), "tree")]
[JsonDerivedType(typeof(DetailSchema), "detail")]
public abstract class CardSchema { }

public class TableSchema : CardSchema
{
    public required List<ColumnDef> Columns { get; init; }
    public bool Searchable { get; init; } = true;
    public bool Paginated { get; init; } = true;
    public int DefaultPageSize { get; init; } = 20;
    public List<RowAction>? RowActions { get; init; }
}

public class ColumnDef
{
    public required string Field { get; init; }    // JsonNode 中的字段路径
    public required string Header { get; init; }   // 显示名
    public bool Sortable { get; init; } = true;
    public string? Width { get; init; }
    public ColumnFormat Format { get; init; } = ColumnFormat.Text;
}

public enum ColumnFormat { Text, DateTime, Badge, Link, Image, Custom }

public class RowAction
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string? Icon { get; init; }
    public string? Confirm { get; init; }  // 确认提示文本，null 不确认
    public bool Danger { get; init; }
}

public class StatusSchema : CardSchema
{
    public required List<StatusField> Fields { get; init; }
    public List<ActionButton>? Actions { get; init; }
}

public class StatusField
{
    public required string Field { get; init; }
    public required string Label { get; init; }
    public StatusFieldType Type { get; init; } = StatusFieldType.Text;
}

public enum StatusFieldType { Text, Badge, Progress, Indicator, DateTime }

public class ActionButton
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string? Icon { get; init; }
    public string? Confirm { get; init; }
    public bool Danger { get; init; }
}

public class FormSchema : CardSchema
{
    public required List<FormField> Fields { get; init; }
    public List<FormGroup>? Groups { get; init; }
    public bool ShowReset { get; init; } = true;
}

public class FormField
{
    public required string Field { get; init; }
    public required string Label { get; init; }
    public FormFieldType Type { get; init; } = FormFieldType.Text;
    public string? Placeholder { get; init; }
    public string? Description { get; init; }
    public bool Required { get; init; }
    public List<SelectOption>? Options { get; init; }  // Select/Radio 用
    public string? Group { get; init; }                // 分组名
}

public enum FormFieldType { Text, Number, TextArea, Select, Toggle, Radio, Password, Json }

public class FormGroup
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool DefaultCollapsed { get; init; }
}

public class SelectOption
{
    public required string Value { get; init; }
    public required string Label { get; init; }
}

public class StreamSchema : CardSchema
{
    public int MaxLines { get; init; } = 500;
    public bool AutoScroll { get; init; } = true;
    public bool ShowPauseButton { get; init; } = true;
    public bool ShowFilter { get; init; } = true;
}

public class ChatSchema : CardSchema
{
    public bool ShowSenderSwitch { get; init; } = true;
    public bool ShowInput { get; init; } = true;
    public List<string>? Senders { get; init; }
}

public class TreeSchema : CardSchema
{
    public required string NodeIdField { get; init; }
    public required string NodeLabelField { get; init; }
    public string? ParentIdField { get; init; }
    public string? ChildrenField { get; init; }
    public bool Expandable { get; init; } = true;
}

public class DetailSchema : CardSchema
{
    public required List<DetailSection> Sections { get; init; }
}

public class DetailSection
{
    public required string Title { get; init; }
    public required List<DetailField> Fields { get; init; }
    public bool DefaultCollapsed { get; init; }
}

public class DetailField
{
    public required string Field { get; init; }
    public required string Label { get; init; }
    public ColumnFormat Format { get; init; } = ColumnFormat.Text;
    public bool Editable { get; init; }
}
```

### DataSource 接口

```csharp
/// <summary>数据源定义（声明式，Provider 提供）</summary>
public class DataSourceDefinition
{
    public required string Id { get; init; }
    public required IDataSource Source { get; init; }
}

/// <summary>数据源实现接口</summary>
public interface IDataSource
{
    /// <summary>拉取数据（分页/筛选/排序）</summary>
    Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default);

    /// <summary>提交操作（保存/删除/自定义动作）</summary>
    Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default);

    /// <summary>是否支持推送。支持时框架自动调用 Subscribe。</summary>
    bool SupportsPush { get; }

    /// <summary>订阅实时更新。payload 为 null 表示"数据变了，请重新 Fetch"。</summary>
    IDisposable? Subscribe(Action<JsonNode?> callback);
}

/// <summary>统一查询模型</summary>
public class DataQuery
{
    public int? Page { get; init; }
    public int? PageSize { get; init; }
    public string? Search { get; init; }
    public string? SortBy { get; init; }
    public bool SortDesc { get; init; }
    public List<DataFilter>? Filters { get; init; }
    public JsonNode? Extra { get; init; }
}

public class DataFilter
{
    public required string Field { get; init; }
    public required string Operator { get; init; }  // eq, neq, contains, gt, lt, in
    public required string Value { get; init; }
}

/// <summary>Fetch 返回结果</summary>
public class DataResult
{
    public required JsonNode Data { get; init; }    // 数组或对象
    public int? TotalCount { get; init; }           // 分页时的总数
    public JsonNode? Meta { get; init; }            // 额外元数据
}

/// <summary>Submit 返回结果</summary>
public class ActionResult
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public JsonNode? Data { get; init; }
}
```

### 页面上下文（卡片间联动）

```csharp
/// <summary>页面级共享上下文，卡片间通信</summary>
public interface IPageContext
{
    /// <summary>发布事件</summary>
    void Emit(string eventName, JsonNode? payload = null);

    /// <summary>订阅事件</summary>
    IDisposable On(string eventName, Action<JsonNode?> handler);

    /// <summary>共享状态字典</summary>
    JsonNode? GetState(string key);
    void SetState(string key, JsonNode? value);

    /// <summary>触发导航</summary>
    void Navigate(string route);
}
```

### 生命周期

框架为每个卡片实例管理以下生命周期回调（通过 CardDefinition 可选声明）：

```csharp
public class CardLifecycle
{
    /// <summary>页面加载时</summary>
    public Func<IPageContext, Task>? OnMount { get; init; }

    /// <summary>进入视口时（懒加载）</summary>
    public Func<IPageContext, Task>? OnVisible { get; init; }

    /// <summary>手动刷新时</summary>
    public Func<IPageContext, Task>? OnRefresh { get; init; }

    /// <summary>离开页面时</summary>
    public Func<IPageContext, Task>? OnDispose { get; init; }
}
```

## Shell 架构

### 职责

1. **侧边栏渲染器** — 读取所有已注册 Provider 的 PageMeta，按 Group/Order 构建导航树
2. **卡片渲染引擎** — 7 种内置渲染器（独立 Blazor Component），按 CardType 分发
3. **网格布局系统** — CSS Grid，根据 CardLayout 约束自动排列，响应式
4. **认证** — Cookie Authentication（现有方案保留）
5. **页面管理** — 查看/加载/卸载 Provider（侧边栏底部，不可卸载）
6. **路由** — 根据 Provider 注册的 Route 动态匹配

### 渲染流程

```
URL 变化 → Shell Router 匹配 PageDefinition
  → 实例化 IPageContext
  → 创建 DataSource 实例池
  → 遍历 Cards:
      → 按 CardLayout 放入 Grid
      → 按 CardType 选择渲染器 Component
      → 传入 Schema + DataSource 引用 + PageContext
  → 卡片 OnMount → 首次 Fetch → 渲染
  → Subscribe 注册（SupportsPush=true 的数据源）
  → 用户交互 → Submit / Emit / Navigate
  → 离开页面 → OnDispose + Unsubscribe
```

### 错误处理

- Fetch 失败 → 卡片显示错误态（红色边框 + 错误信息 + 重试按钮）
- 自动重试：指数退避（1s → 2s → 4s），3 次后停止
- Submit 失败 → toast 提示，不清空表单/状态
- DataSource 构造失败 → 整个卡片显示 "数据源不可用"

### 推送封装

- 框架提供 `IPushChannel`（内部走 SignalR Hub）
- DataSource 实现 Subscribe 时可注入 `IPushChannel`
- 回调触发时框架自动 `StateHasChanged` 对应卡片
- 卡片不感知 SignalR

## Provider 注册与热重载

### 发现机制

复用现有 PluginLoader 管道：
- 启动时扫描主程序集 + Plugins/ 目录 DLL
- 发现实现 `IWebUIProvider` 的类 → 实例化 → 注册到 ProviderRegistry
- 标记 `[WebUIProvider(BuiltIn = true)]` 的为内置，不可卸载

### 热重载流程

```
PluginLoader 卸载旧 ALC → ProviderRegistry 反注册旧 Provider 的所有页面
  → 加载新 DLL → 扫描 IWebUIProvider → 注册新页面
  → 侧边栏自动刷新（SignalR 推送导航变更事件）
  → 当前页面若属于被替换的 Provider → 自动刷新
```

### WebUIProviderAttribute

```csharp
[AttributeUsage(AttributeTargets.Class)]
public class WebUIProviderAttribute : Attribute
{
    public bool BuiltIn { get; set; }  // true = 不可卸载
}
```

## 迁移策略

### Phase 1: Shell 基础设施
- ProviderRegistry（注册/反注册/查询）
- 动态路由器（根据注册的 Route 匹配）
- 网格布局组件
- 7 种卡片渲染器 Component（空壳 + 基本渲染）

### Phase 2: 数据层
- IDataSource 运行时管理
- DataQuery → Fetch 管线
- Submit 管线
- 错误处理 + 重试
- SignalR 推送通道

### Phase 3: 迁移现有页面
- 逐页迁移为 Provider（从简单页面开始：Dashboard → Logs → Config）
- 保留旧页面作为 fallback 直到迁移完成
- 每迁移一个页面验证功能等价

### Phase 4: 高级特性
- IPageContext 卡片间联动
- 懒加载（OnVisible）
- Custom 卡片类型支持
- 外部 Provider 热重载验证
