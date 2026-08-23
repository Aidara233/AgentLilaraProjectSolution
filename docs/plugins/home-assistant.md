# Home Assistant 插件

> 智能家居集成：状态查询、服务调用、实体搜索、自动化触发、历史查询。
> 接入 `homeassistant.lan`（192.168.123.65，HAOS 2026.8.2）。

## 组件

| 组件 | 作用域 | 说明 |
|------|--------|------|
| `ha-global` | Global | 共享 HttpClient + `HaConfig` 连接配置（Static Accessor Bridge 提供 `HaToolsAccessor.Global`） |
| `home-assistant` | Loop | 每会话暴露 6 个工具 + BuildPromptSection 轻量提示 |

## 工具

| 工具 | 参数 | 说明 |
|------|------|------|
| `ha_get_states` | `entity_id?`, `domain?` | 查单个实体 / 按域查全部 / 全局摘要 |
| `ha_call_service` | `domain`, `service`, `entity_id?`, `data?` | 调任意服务（如 `climate.set_temperature` 调温） |
| `ha_search_entities` | `query` | 按 entity_id / 中文名模糊搜索 |
| `ha_list_devices` | `domain?` | 列出设备/实体（按域过滤或全部概览） |
| `ha_trigger` | `entity_id` | 触发 `script.*` / `automation.*` |
| `ha_get_history` | `entity_id`, `hours?` | 查实体历史状态变化（本地时区时间序列） |

## 配置

**`Storage/PluginData/ha/HaConfig.json`**（运行时 `E:/Tools/Lilara/Storage/PluginData/ha/HaConfig.json`，**仓库外，不进 git**）：

```json
{
  "Host": "homeassistant.lan",
  "Port": 80,
  "UseHttps": false,
  "Token": "eyJ...（HA 长期访问令牌）",
  "MaxResponseBytes": 20480,
  "TimeoutSeconds": 15,
  "HistoryHours": 6
}
```

- `Token` 在 HA 后台「个人资料 → 安全 → 长期访问令牌」生成；**不要提交到 git / 暴露在命令行历史**。
- Host 默认 `homeassistant.lan`（主机名直连）；Port 默认 **80**（实测该 HA 8123 关闭、80 反代开放）。
- 首次运行时若配置缺失会自动生成默认值文件（Token 为空），需手动填入。

## 会话类型接入

`home-assistant` 组件已加进 **channel** 会话类型的 `Components`（`Storage/SessionTypes/channel.json`）。

> ⚠ **三处同步**：`AgentCoreProcessor/templates/SessionTypes/`（git 内）+ 工作区 `E:/Workspace/AgentLilaraProject/Storage/SessionTypes/` + 运行时 `E:/Tools/Lilara/Storage/SessionTypes/`。改配置时三处都改。

## 验证

- `--dev` 起 HttpBridge(9876)+WebUI(5000)；signal 日志（`E:/Tools/Lilara/Storage/Database/logs.db`，`signalconfig.json` MinLevel=0）查「插件已加载 home-assistant」。
- `--dev`/`--test` 投递只孵 express 会话（channel 组件不装配），验证 HA 工具需先 escalate 升 channel 或连真实 QQ。
- 手工端点验证（token 不落命令行历史，用 `--config`/配置文件）：
  ```
  GET  /api/states/{entity_id}
  GET  /api/states?domain={domain}
  POST /api/services/{domain}/{service}   body: {"entity_id": "...", "data": {...}}
  GET  /api/history/period/{start}?filter_entity_id={id}
  ```