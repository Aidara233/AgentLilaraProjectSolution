# Plugin.SshTools 设计

> **状态：已完成 (2026-05-27)** — 所有功能已实现

## 概述

为 Agent Lilara 提供 SSH 远程执行和文件传输能力，对接本地 PVE 虚拟机。复用现有 `Storage/SSH/RemoteShellConfig.json` + 密钥。

## 架构

```
Plugins/Plugin.SshTools/
├── Plugin.SshTools.csproj
├── SshConfig.cs              # 配置加载 + 多路复用
├── SshGlobalComponent.cs     # Global: SshClient 单例 + 任务管理
├── SshLoopComponent.cs       # Loop: 工具 + 远端工作目录 + 通知注入
├── Tools/
│   ├── ExecTool.cs           # ssh_exec
│   ├── UploadTool.cs         # ssh_upload
│   ├── DownloadTool.cs       # ssh_download
│   ├── CheckTaskTool.cs      # ssh_check
│   └── KillTaskTool.cs       # ssh_kill
└── SshTask.cs                # 异步任务模型
```

GlobalComponent + LoopComponent 模式，与 Plugin.NetworkTools 一致。

## 连接模型

- GlobalComponent 维护一根 `SshClient`（SSH.NET），全实例共享
- SSH 原生多路复用：一个 TCP 连接内多个 session/channel 并发执行
- 断开自动重连，空闲超时（可配置，默认 300s 无活动关闭）
- 每个 loop 首次调用时在远端自动创建 `/tmp/agent-lilara/{loopId}/` 工作目录

## 文件沙箱

- 本地路径：`ssh_upload` 源路径和 `ssh_download` 目标路径必须位于 `IPluginStorage.WorkspaceDirectory` 下
- 远端路径：默认相对于 `/tmp/agent-lilara/{loopId}/`，不限制越出（远端 root 权限无限制），但每个 loop 只需在自己的工作区活动（君子协定）

## 工具

### ssh_exec

| 参数 | 位置 | 必填 | 说明 |
|---|---|---|---|
| `command` | 0 | 是 | 要执行的 shell 命令 |
| `timeout` | 1 | 否 | 等待秒数，默认 10，0=异步立即返回 task_id |

行为：
- `timeout > 0`：同步等待。超时未完成自动降级为异步，返回 task_id
- `timeout = 0`：直接异步，立即返回 task_id
- 同步完成时返回 stdout（截断 4000 字符）+ stderr + exit_code
- 异步模式：后续通过 `ssh_check` 查询或 `BuildPromptSection` 自动通知

### ssh_upload

| 参数 | 位置 | 必填 | 说明 |
|---|---|---|---|
| `local_path` | 0 | 是 | 本地文件/目录路径（限制在 workspace 内） |
| `remote_path` | 1 | 否 | 远端目标路径，默认 `/tmp/agent-lilara/{loopId}/` |
| `timeout` | 2 | 否 | 等待秒数，默认 30，0=异步 |

行为：同 ssh_exec 的超时/异步逻辑。`local_path` 必须位于 workspace 内，否则拒绝。支持目录递归上传。

### ssh_download

| 参数 | 位置 | 必填 | 说明 |
|---|---|---|---|
| `remote_path` | 0 | 是 | 远端文件/目录路径 |
| `local_path` | 1 | 否 | 本地目标路径（限制在 workspace 内），默认 workspace 根 |
| `timeout` | 2 | 否 | 等待秒数，默认 30，0=异步 |

行为：同上。`local_path` 限制在 workspace 内。

### ssh_check

| 参数 | 位置 | 必填 | 说明 |
|---|---|---|---|
| `task_id` | 0 | 否 | 不传列出全部进行中的任务 |

返回任务状态：`running` / `completed` / `failed` / `killed`，含结果数据。

### ssh_kill

| 参数 | 位置 | 必填 | 说明 |
|---|---|---|---|
| `task_id` | 0 | 是 | 要杀掉的任务 ID |

关闭对应 SSH channel，远端进程收到 SIGTERM。

## 异步任务流

1. 工具返回 `task_id`（如 `"ssh-{loopId}-{seq}"`）
2. GlobalComponent 在 `ConcurrentDictionary` 中注册任务
3. SSH channel 进程结束后执行回调：任务 → done 队列
4. LoopComponent 的 `OnBeforeInvokeAsync` 兑现完成队列
5. `BuildPromptSection` 注入已完成的异步任务结果：

```
[SSH 任务完成]
- ssh-channel:3-2: "npm install" → exit 0 (12.3s)
  [stdout 截断 2000 字符...]
- ssh-channel:3-3: "scp /tmp/..." → 完成 (45.7s)
```

## 提示词注入

LoopComponent 的 `BuildPromptSection` 注入三部分：

1. SSH 连接状态（已连接/断开/重连中）
2. 远端工作目录路径
3. 异步任务完成通知（如有）

## 依赖

- **SSH.NET**（NuGet）- SSH 客户端，支持多路复用
- AgentLilara.PluginSDK

## 配置文件

复用 `Storage/SSH/RemoteShellConfig.json`：

```json
{
  "host": "192.168.123.2",
  "port": 22,
  "username": "root",
  "keyPath": "SSH/pve-ALPAlpine/key",
  "sshPath": "...",
  "maxOutputChars": 4000,
  "maxTimeoutSeconds": 60
}
```

新增字段（如有需要）：
- `idleTimeoutSeconds`：空闲断开秒数，默认 300
- `reconnectDelaySeconds`：重连延迟，默认 5

## LoopApplicability

| 引擎 | 启用 |
|---|---|
| Channel | 是 |
| System | 是 |
| Review | 否 |
| SubAgent | 是（按子 agent 工具白名单控制） |
