# Icalingua 适配器

Icalingua++ Bridge（底层 oicq）的 Socket.IO 适配器，作为项目第二条 QQ 通道（**并行/备用**，与 OneBot/NapCat 共存）。

协议细节见 `docs/ICALINGUA_BRIDGE_SOCKETIO_PROTOCOL.md`；本文档记录项目侧对接方式。

## 配置

配置文件：`Storage/Adapter/{id}.json`（运行时实际路径由 exe 的 `paths.json` 决定，当前 `E:/Tools/Lilara/Storage/Adapter/`）。

```jsonc
{
  "Id": "icalingua-main",
  "Type": "icalingua",
  "Enabled": true,
  "AutoStart": true,
  "AutoStartDebug": false,
  "Settings": {
    "server": "http://127.0.0.1:6789",  // Bridge 地址，缺省端口 6789
    "privateKey": "...",                 // ed25519 私钥 seed（与 Bridge config.yaml 的 pubKey 配对）
    "filterMode": "whitelist",           // 并行分流的关键
    "whitelist": ["private_1664093638", "group_616565017"],
    "blacklist": [],
    "botNames": ["Lilara", "莉尔拉"]       // 无 @ 时按名字判定 IsMentioned
  }
}
```

- `privateKey` 支持 **base64 / hex / 原始 32 字节** 三种形式（`IcalinguaProtocol.ParseSeed` 宽松解析）。
- 缺省 `filterMode` 为 `whitelist`。**并行分流**：它实现 `IChannelAwareAdapter.OwnsChannel`，只有命中其 whitelist/blacklist 的频道才归它出向；与 OneBot 的频道互不抢（同 `group_`/`private_` 命名）。

## 技术栈（新增 NuGet）

- `SocketIOClient` 4.0.5（Socket.IO 客户端，非原生 WebSocket）
- `BouncyCastle.Cryptography` 2.4.0（ed25519 签名；纯托管，对 win-x64 self-contained 发布零原生依赖）
  - .NET 8 **无内置** `System.Security.Cryptography.Ed25519`；NSec 26.x 仅支持 net9+，故选 BouncyCastle。

## 连接 / 握手

`IcalinguaAdapter.StartAsync` → `SocketIO`（`Transport=WebSocket`、`EIO=V4`、`Reconnection=true`）：

1. `requireAuth(salt)` → 用私钥对 salt 的 UTF-8 字节做 ed25519 签名（64 字节原始）→ `emit("auth", [签名])`
2. `authSucceed` / `authFailed` → 认证结果
3. `onlineData {uin,...}` → 记录 `selfId`（供 `BotPlatformId` / @bot 判定 / 发送者过滤），视为已登录
4. 断线自动指数退避重连（SocketIO 库自带）

## 入站（addMessage → IncomingMessage）

`IcalinguaMessageParser.HandleAddMessage(roomId, msg)`：
- roomId `>0` → `private_{uin}`；`<0` → `group_{-gin}`
- `msg.content` 伪标记扫描：`<IcalinguaAt qq=N>`（@：qq==selfId → IsMentioned）、`[Face:N]`/`[QLottie]`（表情）、`[Forward:]`/`[NestedForward:]`（合并转发占位，首版不展开）
- `msg.files[]`：图片 → `ImageStorage.DownloadAndSaveAsync` 下载（isFace → sticker/image）；audio/video/其它 → 记录 Attachment + `[语音/视频/文件]` 占位
- `msg.replyMessage` → `Quoted*` 字段；引用到 bot 自身 → IsMentioned
- 发送者：优先 `senderId/sender_id/user_id`；缺省从 `_id` base64 解析（群 21 字节 / 私聊 17 字节，§9.3）
- `msg.time`（毫秒）→ Time

## 出站（OutgoingMessage → SendMessageParams）

`IcalinguaActions.SendMessageAsync`：
- ChannelId → roomId（`private_`→正、`group_`→负）
- **优先消费 `Segments`**（文本/@/图片/回复）：
  - Text → content 追加
  - At → content 追加 `<IcalinguaAt qq={id}>`（`all`→`1`）
  - Image → 读文件 base64 → 入 `media[]`
  - Reply → `replyMessage._id`（= ReplyTo）
- **旧路径兼容**（Content+Mentions+Attachments，含 `\x01AT:qq\x01` 占位解析）
- 发送分流：
  - 含 b64 媒体 → 先 `emit("requestToken")` 拿一次性 token，再 `POST /api/{token}/sendMessage`（§8.4，成功 202）
  - 纯文本/@ → `emit("sendMessage", params)`
- 返回 `"0"` 哨兵表发出（sendMessage 无 message_id 回执）

## 动作（ExecuteActionAsync / GetAvailableActions）

首版提供：`get_groups`、`get_group_members`、`delete_message`(撤回)、`send_group_poke`、`send_group_sign`。WebUI 的适配器页从 `GetAvailableActions()` **动态**生成动作卡，无需额外硬编码。

## WebUI

`AdapterProvider` 已加：
- `adapters/icalingua` 平台页（实例/状态/连接配置表单）
- AdapterConfigSource 字段映射：`server`→`server`、`private_key`→`privateKey`（filterMode/botNames/filter_list 通用）
- "新建适配器"类型下拉加 `icalingua`

## 验证

- 单元测试：`IcalinguaProtocolTests`（ParseSeed 三格式 / SignSalt 64 字节可验签 / roomId 映射）——全量 `dotnet test` 通过
- 编译：解决方案 `dotnet build`（0 错误 0 警告）
- 启动：`--dev` 常驻，含 icalingua-test 配置（连不可达地址）不炸启动链
- **连真实 Bridge 的端到端（握手→收→回→图片）由用户实测**

## 待实测 / 风险

- 握手二进制签名：`emit("auth", [64字节])` 到 JS 服务端收为 Buffer，oicq `verify` 应可直接消费；若不被识别需调优
- SocketIO 协议版本：默认 EIO=V4，若 Bridge 版本不兼容可下调
- `renewMessage`（撤回/语音解码）暂只记日志，不更新已入库消息
