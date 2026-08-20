# Icalingua++ Bridge Socket.IO 协议文档（客户端开发精要版）

> 目标程序：`Icalingua-plus-plus`（develop 分支）
> 服务端：`icalingua-bridge-oicq`（Bridge）
> 客户端：`icalingua`（Electron，`adapter: socketIo`）
> 本文档**以客户端实现者视角**编写，足够精细到可以直接照着实现一个 socketIo 客户端。服务端细节从简，聚焦**必须遵守的 socketio 约定**与**消息/媒体 wire 格式**。
>
> 协议版本：客户端约定 `EXCEPTED_PROTOCOL_VERSION = '2.26.3'`；Bridge 握手时下发自身 `protocolVersion`。

---

## 0. 关键源码索引（客户端实现者必读）

| 文件 | 内容 |
|------|------|
| `icalingua/src/main/adapters/socketIoAdapter.ts` | **客户端 socket 封装的唯一权威**：连接、握手、全部事件、媒体上传 |
| `packages/types/Adapter.d.ts` | 抽象协议接口（事件参数的语义来源） |
| `packages/types/Message.d.ts` 等 | 共享类型 |
| `icalingua-bridge-oicq/utils/processMessage.ts` | **服务端→客户端的消息内容编码规则**（`Message.content` mini-language） |
| `icalingua/src/renderer/components/vac-mod/utils/messageFormatting.ts` | **客户端解析该 mini-language 的正面实现** |
| `icalingua/src/renderer/components/vac-mod/utils/messageMarkdown.ts` | Markdown 消息渲染（QQ Bot Markdown） |
| `icalingua/src/renderer/components/vac-mod/utils/forwardMessage.ts` | 合并转发卡片的解码 |
| `icalingua-bridge-oicq/utils/messageMediaOrder.ts` | `media[].order` 的切分约定 |
| `icalingua/src/utils/getImageUrlByMd5.ts` | 图片 URL 由 md5 生成规则 |

---

## 1. 传输层与连接

### 1.1 单条连接、单一命名空间

- 客户端使用 **`socket.io-client`**，`io(serverUrl, { transports: ['websocket'] })`，**默认命名空间 `/`**，不使用房间（除服务端把已认证连接 `join('authed')` 用于广播）。
- `serverUrl` 来自客户端配置 `server`（如 `http://127.0.0.1:6789`）。
- 缺省端口 `6789`；Bridge 监听 `host`/`port`（`config.yaml`）。
- 服务端 CORS：`origin:'*'`, `allowedHeaders:['GET','POST']`。
- 客户端仅在“初始化登录”时**创建连接**；已登录的远端登录复用同一 socket。

### 1.2 连接生命周期（客户端侧）

```
socket = io(server, {transports:['websocket']})
  ├─ connect_error → 致命，弹框后退出
  ├─ connect       → ui.setOnline()
  ├─ disconnect    → ui.setOffline()
  └─ requireAuth(salt, {version, protocolVersion})  见 §2
```

---

## 2. 认证握手（ed25519 challenge-response）

**完全确定的握手字节流：**

1. Bridge 端：`salt = md5(String(Date.now()))`，`socket.emit('requireAuth', salt, {version, protocolVersion})`。
2. 客户端收到后：
   - 先比对 `protocolVersion !== '2.26.3'`（可被 `disableBridgeVersionCheck` 关闭）。
   - `socket.emit('auth', await sign(salt, privateKey))`。
     - `sign` 来自 `@noble/ed25519`：**ed25519 签名**，输入 = `salt` 字符串（UTF-8 字节），输出为 **64 字节原始二进制**（非 hex）。客户端只传一个参数（`role` 省略时为 undefined，服务端默认 `'main'`）。实测：signature 作为 Socket.IO 参数直接以 Buffer/二进制发送。
3. Bridge 用 `config.pubKey` 验证：`verify(sign, salt, pubKey)`（同样原始字节）。
   - 成功（main）：`socket.emit('authSucceed')` → `socket.join('authed')` → `emit('dbUpgradeProgress', ...)` → 若已登录 `sendOnlineData()`，否则 `emit('requestSetup', userConfig.account)`。
   - 失败：`emit('authFailed')` 然后 `disconnect()`。
4. 客户端只监听**一次** `authSucceed`（`socket.once`）→ 触发 `attachSocketEvents`（注册所有业务事件监听）；`authFailed`（once）→ 弹框退出。

> ⚠️ 流量默认明文；公网务必 HTTPS 反向代理。此认证仅验证"持有私钥"，不加密数据。

### 2.1 双角色

`auth(sign, role)`：

| role | 用途 | 验签方式 | 成功事件 |
|------|------|----------|----------|
| `main`（默认） | 主客户端 | `verify(sign, salt, pubKey)` | `authSucceed`（无参） |
| `fileMgr` | 群文件网页端 | `gfsTokenManager.verify(sign)` | `authSucceed(gin, group)` |

`fileMgr` 的 `sign` 实为 **GFS token**（§8.1），非签名。

---

## 3. 通讯模型

### 3.1 请求/响应（ack 回调）
客户端 `socket.emit(evt, ...args, cb)`；服务端 handler 最后一个参数是可执行的 ack 回调。客户端用一个 `new Promise(resolve => socket.emit(evt, ...args, resolve))` 包裹每个 RPC。**参数顺序即协议约定**（见 §5 表），顺序错误会让 Bridge 侧收到错位参数。

### 3.2 服务端推送
`broadcast(channel, data) = io.to('authed').emit(channel, data)`，客户端收到后转给 `ui`（渲染层）。无事件名以外的信封。

### 3.3 分页 / 批量返回
`fetchMessages` 等 handler 会把**请求来源 socket** 传入 adapter：除经 ack 返回 `Message[]` 外，还能对**该 socket** 单独补发 `setShutUp` / `message` 等事件（`client.emit`）。单页默认 20 条；图片 30 条；按发送者 20 条；搜索 20 条。

---

## 4. 消息内容 Wire 格式（客户端务必精通）

`Message.content` 是 **Bridge 编码后的扁平文本**，其中混有少量“伪 Markdown”标记，客户端须按 `messageFormatting.ts` 的规则**扫描解析**成结构化 parts，再做链路化 / 渲染。这是最容易错的地方。

### 4.1 特殊标记（PSEUDO_MARKDOWN_RULES）

| 类型 | 起始符 | 结束符 | 内容约束 | 正则含义 |
|------|--------|--------|----------|----------|
| `face` | `[Face: ` | `]` | `^\d+$`（纯数字） | 表情 ID |
| `forward` | `[Forward: ` | `]` | `[^\r\n]+` | 合并转发 resId |
| `nestedforward` | `[NestedForward: ` | `]` | `[^\r\n]+` | 嵌套转发 fileName |
| `at` | `<IcalinguaAt qq=` | `</IcalinguaAt>` | `[^\r\n]+` | @某人 |
| `breakLine` | `\n` | （无） | — | 换行 |

扫描规则（`parseMessageText`）：按起始符正则整段扫描，匹配标记则视为一个 part，剩余作为普通文本（`linkify` 把其中的 URL 拆成 `url` part）。**未匹配到结束符或内容不符约束则当作普通文本**（例如 `[Face: invalid]` 原样显示）。

**@ 标记内部格式**：`<IcalinguaAt qq=<QQ或1>><encodeURIComponent(名字)，且 `.` 被替换为 `%2E`></IcalinguaAt>`。
- 例子：`<IcalinguaAt qq=42>Alice%20Chen</IcalinguaAt>`
- 解码：客户端 `formatAtPart` 取 `qq`，`name = decodeURIComponent(编码部分)`，`href = icalingua://at?name=<编码名>&qq=<qq>`。`qq === 1` 表示 `@全体成员`。
- 解析约束：`^\d+$`（content 约束其实允许含编码名），实际客户端按 `>` 分隔 `qq` 与名字。

### 4.2 各类型消息的 content 编码（服务端 processMessage 输出）

**QQ 原始段 → `Message` 的映射：**

| OICQ 段类型 | 写入 `content` | `file`/`files` 追加 | 备注 |
|------|------|------|------|
| `text` | 追加文本（`\r\n`→`\n`，去 `\x00`） | — | 若紧跟回复且是同一人，去掉行首空格 |
| `at` | `<IcalinguaAt qq=..>..</IcalinguaAt>`（`qq=all`→`1`） | — | 同步设 `message.at`（`true` / `'all'`） |
| `image` | （不追加文本） | `{type:'image/jpeg', url, order, isFace, height, width}` | `order = content.length`（UTF-16）；`lastMessage.content += '[Image]'` 或 prompt |
| `bface`（大表情/贴纸） | （无） | `{type:'image/webp', url, order, isFace:true}` | url 由 `gxh.vip.qq.com/...` 构造；`lastMessage += '[Sticker]'+text` |
| `flash`（闪图） | — | 同 image | `message.flash=true` |
| `file` | 追加文件名 | `{type:mime(ext), size, url, name, fid}` | `lastMessage += '[File]'+name` |
| `record`（语音） | `[无法处理的语音]undefined`（无 url 时） | `{type:'audio/ogg', url, name, fid?}` | 见 4.4 异步解码 |
| `video` | 清空 content | `{type:'video/mp4', url, fid}` | `lastMessage += '[Video]'` |
| `face`（小表情） | `[Face: <id>]`；若带 qlottie：`[QLottie: <pack>,<id>[,<resultId>]]` | — | `lastMessage += '[<text>]'` |
| `share`（链接卡片） | 追加 `url` | — | `lastMessage += '[Link]'+title` |
| `reply` | （无） | — | 填 `message.replyMessage`（见 4.3） |
| `json`（小程序/转发卡片等） | 复杂，见 4.5 | 视 app 类型追加图片 | `message.code = 原始 json` |
| `xml` | 复杂，见 4.5 | 视内容追加图片 | `message.code = 原始 xml` |
| `shake` | `[窗口抖动]` | — | |
| `rps` | `[猜拳]<石头\|剪刀\|布>` | — | |
| `dice` | `[随机骰子]点数<N>` | — | |
| `poke` | `[<动作名>]` | — | 动作名查表（回戳/戳一戳/比心…） |
| `sface` | `[sFace: <text>(<id>)]` | — | 超级表情 |
| `markdown` | 累加 markdown 源 | 提取 `![..](url)` 为图片 | `message.markdown = true` |
| `button` | （无） | — | 填 `button_rows`（见 4.6） |
| `forward`（合并转发段） | `[Forward: <id>]` | — | 内层消息序列化进 `message.code`（JSON 数组） |
| `mirai`（TG 转发） | 见下 | — | 解析 `message.mirai.eqq`，剥离发送者前缀 |
| `unknown` | 忽略（`console.log`） | — | |

### 4.3 回复消息（replyMessage）

客户端在 `Message.replyMessage` 中会收到一条"引用消息摘要"：
```ts
{
  _id: 原始消息 id,
  username: 发送者名（自己=You，群=card||nickname，否则 UA），
  content: 被引用消息的 content（同样含上述标记，可继续解析），
  files: MessageFile[],
  file?: MessageFile,   // 若被引用含单媒体
  senderId: number,
  date?, timestamp?
}
```
找不到原消息时：`content = '无法找到原消息(<id>)(<time>)'`，`username` 为 `You` 或发送者 uin。

### 4.4 语音异步解码（record）

- 收到新语音消息：`file = {type:'audio/ogg', url:'decoding', name:'decoding', fid?}`，`message.content` 若被占位则可能带 `[语音解码中]`。
- 后台把 silk 解码为 ogg 后，服务端通过 `renewMessage({roomId,messageId,message:{file, files, content}})` 推送更新（此时 `file.url` 为**实际文件名**）；同时 `replaceMessage` 落库。
- 解码失败：`content = '[语音转换失败]<err>\n<url>'`。
- 客户端应把 `file.url` 当作 "文件名" 并通过 `GET {server}/records/<文件名>`（§8.3）取音频，或使用 `file.fid`（协议资源 id，发送语音时用）。

### 4.5 JSON / XML 富内容（message.code）

`message.code` 保留**原始** JSON 或 XML 字符串，`content` 为供展示/排序的摘要。客户端常做：保存 `code`，在 UI 按类型渲染。

- **`com.tencent.multimsg`（合并转发）**：`content = '<预览多行>\n[Forward: <resId>]'` 或 `[NestedForward: <fileName>]`。预览由 `meta.detail.source`（标题）+ `news[].text`（各行）构成，结尾带 `\n`。客户端可用 `parseForwardResource(code)` 取 `resId` / `fileName` / `messages`，用 `parseForwardCard(code)` 渲染卡片，再用 `stripForwardPreview(content, card)` 去掉预览前缀得到纯 `[Forward: resId]`。
- **`com.tencent.mannounce`（群公告）**：解码 `meta.mannounce.title`/`text`（base64）进 content，图片追加 `type:'image/jpeg'`。
- **普通小程序/链接卡片**：`content = '<title>\n\n<desc>\n\n<url>'`，`file` 为 `preview` 图；应用 `meta.detail_1 || meta.news`。
- **群相册 `com.tencent.groupphoto` / `qzone.albumShare`**：`content += '[群相册]'+prompt`，图片来自 `meta.albumData.pics[].url`。
- **其它 JSON**：`content = '[JSON]'+(prompt||'') + '\n\n' + <jumpUrl|pcJumpUrl>`，可含 `preview` 图。
- **XML 带 `action="viewMultiMsg"`**：`[Forward multiple messages]` → 提取 `m_resid` → `[Forward: resid]` / `m_fileName` → `[NestedForward: fileName]`。
- **XML 二维码/链接**：`content = url`（`url="..."`, `&amp;`→`&`）。
- **XML 图片（`md5=".."`）**：`content += '[Image]'`，`file.url = getImageUrlByMd5(md5)`。
- **其它 XML**：`[XML]` + `brief`。

### 4.6 按钮（QQ Bot Markdown 按钮）

`Message.button_rows: MessageButton[][]`，每行一组按钮：
```ts
type MessageButton = { label: string; visited_label?: string; url: string }
```
`url` 是**客户端行为编码**：
- action.type 0（URL）：`url = 目标链接`
- action.type 1（callback）：`icalingua://button/callback?command=..&id=..&appid=..&group_id=..&msg_seq=..`
- action.type 2（inline command）：`mqqapi://aio/inlinecmd?command=..&enter=..&reply=..`

客户端按下 callback 按钮时构造 `sendButtonCallback(groupId, msgSeq, appid, id, data)`（§5.3），其中 `command` 即 data，`msgSeq` 从消息 id 解析（§9.3）。

### 4.7 渲染（客户端侧语义，非 socket 协议但决定展示）

- 普通文本：`linkify` 拆 URL；中文明文前用 `pangu` 加空格（可选配置）。
- Markdown：`message.markdown === true` → `content` 按 `renderMessageMarkdown` 渲染为富文本（标题/加粗/斜体/删除线/代码块/引用/列表/LaTeX `$..$` 与 `$$..$$` / 图片）。首行 `[](%7B%22version%22%3A2%7D)` 是元数据行，渲染时隐藏。
- `[QLottie: pack,face[,result]]` 为 GIF/动态大表情，按 `faceIdToLottie` 映射本地资源路径；`disableQLottie` 时降级为 `[Face: face]`。
- `[Face: N]` 为 QQ 表情，按 N 渲染表情图片。

### 4.8 图片 / 文件 URL 规则（客户端须理解）

`file.url` 可能出现这些形式，客户端直接可用/需转换：

| 形式 | 处理 |
|------|------|
| `https://...`（含 `c2cpicdw.qpic.cn` 等） | 直接用（可能需要 rkey 替换，见下） |
| `base64://...` | 服务端已在接收时转成 md5 后的 gchat URL，不会下发给客户端 |
| `gchat.qpic.cn` / `multimedia.nt.qq.com.cn/download?appid=..&fileid=..` | 服务端在 fetch 请求时会做 rkey 替换：appid 1406/1407 通过 `getNTPicURLbyFileid` 换成真实 URL |
| 空 `url`（`file` 段无 url） | 群文件用 `getGroupFileMeta(gin, fid)` 取 `url`；私聊用 `getPrivateFileUrl(fid)`；无 url 则无法展示，客户端可显示名称 |
| 语音 `file.fid` | 通过 `records` 接口或 fid 定位 |
| `IMAGE_URL_BY_MD5` | `https://gchat.qpic.cn/gchatpic_new/0/0-0-<大写MD5>/0`（服务端对 `c2cpicdw.qpic.cn?md5=` 或 `base64://` / 无 url 的老消息用此规则） |

---

## 5. 客户端 → 服务器 请求事件（参数顺序即协议）

以下为 `socketIoAdapter` 的实际 `socket.emit` 签名。`→` 表示 ack 回调返回类型。

### 5.1 鉴权 / 登录
| 事件 | 参数 | 回调 |
|------|------|------|
| `auth` | `sign, [role]` | — |
| `login` | `form: LoginForm` | —（远端登录时复用连接） |
| `submitSmsCode` | `smsCode`（`'sendSmsCode'`=重发） | — |
| `randomDevice` | `username: number` | — |
| `reLogin` / `login-verify-reLogin` | — | — |
| `login-slider-ticket` | `ticket` | — |
| `setOnlineStatus` | `status: number`（OnlineStatusType） | — |

### 5.2 消息获取
| 事件 | 参数 | 回调 |
|------|------|------|
| `fetchMessages` | `(roomId, options?: MessagePageOptions, cb)` | `Message[]` |
| `fetchImageMessages` | `(roomId, offset, endTime?, cb)` | `Message[]` |
| `fetchMessagesAround` | `(roomId, messageId, before, after, cb)` | `Message[]` |
| `fetchMessagesBySender` | `(roomId, senderId, offset, cb)` | `Message[]` |
| `searchMessages` | `(roomId, keyword, offset, senderId?, startTime?, endTime?, cb)` | `Message[]` |
| `resolveUnreadTargetMessageId` | `(roomId, unreadCount, cb)` | `string\|null` |
| `markMessageUnread` | `(roomId, messageId, cb)` | `number` |
| `fetchHistory` | `(messageId, roomId?, currentLoadedCount)` | — |
| `fetch7DaysHistory` | — | — |
| `stopFetchingHistory` | — | — |
| `reportRead` | `messageId` | — |
| `getForwardMsg` | `(resId, fileName?, cb)` | `Message[]` |
| `makeForward` | `(fakes, dm?, origin?, target?)` | — |
| `getMsgNewURL` | `(id, cb)` | `string` |
| `getNTPicURLbyFileid` | `(fileId, appid, cb)` | `string` |
| `getGroupFileMeta` | `(gin, fid, cb)` | `FileElem.data` |

`MessagePageOptions = { before?: {time,id}, after?: {time,id} }`（游标，严格更旧/更新）。

### 5.3 操作（无回调）
| 事件 | 参数 |
|------|------|
| `sendMessage` | `SendMessageParams`（§6） |
| `addRoom` | `Room` |
| `removeChat` | `roomId` |
| `addChatGroup` / `updateChatGroup` / `removeChatGroup` | `(name, chatGroup?)` |
| `updateRoom` | `(roomId, room: object)` |
| `updateMessage` | `(roomId, messageId, message: object)` |
| `renewMessage` / `renewMessageURL` | `(roomId, messageId, message\|URL)` |
| `hideMessage` / `revealMessage` / `deleteMessage` | `(roomId, messageId)` |
| `pinRoom` | `(roomId, pin)` |
| `setRoomPriority` | `(roomId, priority:1..5)` |
| `setRoomAutoDownload` | `(roomId, autoDownload)` |
| `setRoomAutoDownloadPath` | `(roomId, downloadPath)` |
| `ignoreChat` / `removeIgnoredChat` | `(data\|roomId)` |
| `sendButtonCallback` | `(groupId, msgSeq, appid, id, data)` |
| `sendGroupPoke` | `(gin, uin)` |
| `sendGroupSign` | `(gin)` |
| `setGroupKick` `setGroupBan(gin,uin,dur?)` `setGroupAnonymousBan(gin,flag,dur?)` `setGroupLeave` | 见 §5.5 |
| `handleRequest` | `(type:'friend'\|'group', flag, accept?)` |
| `randomDevice` / `submitSmsCode` | 见 5.1 |

### 5.4 群成员 / 联系人（带回调）
| 事件 | 参数 | 回调 |
|------|------|------|
| `getFriend` | `uin` | `FriendInfo` |
| `getGroup` | `gin` | `GroupInfo` |
| `getFriendsFallback` | — | `SearchableFriend[]` |
| `getGroups` | — | `SearchableGroup[]` |
| `getGroupMembers` | `group` | `MemberInfo[]` |
| `getGroupMemberInfo` | `(group, member, noCache?)` | `MemberInfo` |
| `setGroupNick` | `(group, nick)` | — |
| `setGroupRemark`/`setFriendRemark` | `(id, remark)` | — |

### 5.5 其它查询（带回调）
| 事件 | 参数 | 回调 |
|------|------|------|
| `getCookies` | `domain` | `string` |
| `getIgnoredChats` | — | `IgnoreChatInfo[]` |
| `getRoamingStamp` | `no_cache?` | `RoamingStamp[]` |
| `getSystemMsg` | — | `{flag: item}` |
| `getDisabledFeatures` | — | `SpecialFeature[]` |
| `getLoginDevices` | — | 设备列表 |
| `deleteLoginDevice` | `flag` | — |
| `sendPacket` | `(type, cmd, body)` | `Buffer` |
| `validateMessageSearchIndex` | — | `{ok,error?}` |
| `getUnreadCount` | — | `number` |
| `getFirstUnreadRoom` | `priority` | `Room` |
| `getRoom` | `roomId` | `Room` |

---

## 6. 发送消息协议（SendMessageParams → QQ 段）

客户端构造 `SendMessageParams` 后 `emit('sendMessage', params)`。服务端 `oicqAdapter.sendMessage` 按以下顺序消费，**客户端必须正确产出各字段**：

```ts
type ImageAttachment { b64?: string; url?: string; type?: string; fid?: string; order?: number }
type SendMessageParams {
  content: string          // 普通文本 + [Face: N] + <IcalinguaAt>（不能含媒体占位）
  roomId?: number          // >0 私聊 / <0 群聊（-group_id）
  file?: { type, path, size }   // 上传后 path 为文件哈希（§8.2）
  replyMessage?: Message   // 引用消息：取 _id 与 content
  room?: Room
  media?: ImageAttachment[]// 图片/语音列表
  at: AtCacheItem[]        // [{text, id:number|'all'}]
  sticker?: boolean        // 真=表情（face）/假=图片
  messageType?: string     // 'text'|'json'|'xml'|'forward'|'rps'|'dice'|'shake'|'anonymous'|'raw'
}
```

### 6.1 服务端转换流程（决定客户端如何拼 content）

1. **纯文件消息**（`file.type` 非 image 且非 audio，或无 type）：走文件上传流程（群：`sendFile`；私聊：GFS `upload`）。此时 `file.path` 必须已通过 §8.2 分片上传替换为哈希。
2. 构造 `chain: MessageElem[]`。
3. **匿名**：`messageType==='anonymous'`（仅群聊）→ 前置 `{type:'anonymous'}`。
4. **回复**：前置 `{type:'reply', data:{id: replyMessage._id, text: replyMessage.content}}`；群聊除匿名外再补 `{type:'at',qq:replyUin}` + `{type:'text',text:' '}`。`replyUin` 从 `_id` base64 解析（§9.3）。
5. **解析 `<IcalinguaAt qq=N>名字</IcalinguaAt>`**（content 内联的 @）：
   - 每个匹配 `at.push({id: N===1?'all':N, text: decodeURIComponent(名字)})`；
   - 把标记替换为纯文本名字，并调整 `media[].order`（因为名字长度改变）。
6. **切段**：`text` 类型用 `splitContentByMediaOrder(content, media)` 把媒体按 `order` 位置切成 `[文本, \0icalingua-media:<i>, 文本, ...]`；非 text 不分。
7. **再切 @**：把 `at[]` 中每个 `text` 从内容中独立成段（用于精确 @）。
8. **再切 `[Face: N]`**：把表情标记独立成段。
9. 逐段生成 `MessageElem`：
   - 媒体段 → `appendMedia(i)`：按 `media[i]` 生成 `image`/`record` 段（见 6.2）。
   - `atInfo` 段 → `{type:'at', qq:id, text}`。
   - `[Face: N]` 段 → `{type:'face', id:N}`。
   - 其余 → `{type:'text', text: 段}`（或按 messageType 特殊生成 json/xml/forward/rps/dice/shake/raw）。
10. 媒体未出现在 content 中的（`media` 存在但未被 order 切出）→ 依 `media.forEach` 依次 `appendMedia` 追加到链尾。
11. 若只有 `file` 且无 media：追加上传后的图片段（`file: file.path`, `type: sticker?'face':'image'`）。
12. **QLottie**：content 形如 `[QLottie: pack,face[,result]]` 且全串匹配 → 生成 `{type:'face', data:{id:face, qlottie:pack, extra:{lottieType,packId,resultId,...}}}`。
13. **发送**：`roomId>0 ? sendPrivateMsg(roomId, chain, true) : sendGroupMsg(-roomId, chain, true)`。
14. 失败：`notifyError` + `addMessageText(content)`。

> 结论：**客户端发送普通文本/图片时，content 里写 `[Face: N]` 表情与 `<IcalinguaAt qq=..>名字</IcalinguaAt>` 即可**；媒体用 `media[]` 且（可选）填 `order = 在 content 的 UTF-16 偏移 `（配合行内图片布局）。`at[]` 客户端通常空数组由服务端从 content 推导，但也可显式传入。

### 6.2 appendMedia 的判定（客户端填充 media 的规矩）
对 `media[i]`：
- `type` 以 `audio/` 开头且 `fid` 存在 → `record` 段（`file: fid`，复用协议资源，语音 +1）。
- `b64` 以 `data:audio` 开头 → `record` 段（`file: Buffer.from(去前缀的base64)`）。
- 其余 `b64` → `image` 段（`file:'base64://'+raw`, `type: sticker?'face':'image'`, `url: img.url||img.b64`）。
- `url` → `image` 段（`file:url`, `url: url.replace(/\\/g,'/')`）。

> 注意：**带 `b64`（图片/语音）的发送走 HTTP，不走 `sendMessage`**（见 §8.4）。仅 `url`/纯文本/已上传文件走 `sendMessage`。

---

## 7. 服务器 → 客户端 事件（客户端消费行为）

### 7.1 连接 / 登录门
| 事件 | 载荷 | 客户端行为 |
|------|------|------|
| `requireAuth` | `(salt, {version,protocolVersion})` | §2 握手 |
| `authSucceed` | — | (once) 注册业务监听 |
| `authFailed` | — | 报错退出 |
| `dbUpgradeProgress` | `{active,step,total,message}` | 显示升级进度 |
| `requestSetup` | `LoginForm` | 弹登录窗口（显示 Bridge 未登录） |
| `fatal` | `message` | 弹框退出 |
| `setOnline` / `setOffline(msg)` | — / `msg` | 在线状态 |
| `onlineData` | `{online,nick,uin,sysInfo,bkn}` | **视为已登录**：设本地 uin/bkn/nick，加载主界面，随后 `setAllRooms`/`setAllChatGroups` 推送 |
| `login-verify` | `url` | 开新窗加载验证页；关闭时发 `login-verify-reLogin` |
| `login-qrcodeLogin` | `url` | 展示二维码 |
| `login-smsCodeVerify` | `DeviceEventData` | 短信验证界面 |
| `login-error` | `message` | 显示错误 |
| `login-slider` | `url` | 注入滑块脚本处理 |

### 7.2 消息 / 会话实时
| 事件 | 载荷 | 客户端行为 |
|------|------|------|
| `addMessage` | `{roomId, message}` | 插入消息；`_id` 为 string 且是当前选中会话且窗口聚焦 → 自动 `reportRead(_id)` |
| `renewMessage` | `{roomId,messageId,message}` | 更新消息（撤回/媒体 URL/语音解码完成） |
| `renewMessageURL` | `{messageId, URL}` | 更新单媒体 URL |
| `deleteMessage` | `messageId` | 删除 |
| `setMessages` | `{roomId, messages}` | 整页替换（历史/搜索）；房间须等于当前选中才渲染 |
| `addMessageText(text)` | `text` | 追加一段本地文本（发送失败兜底） |
| `syncRead` | `roomId` | 清空该会话未读 |
| `updateRoom` | `Room` | 更新会话（未读/最后消息） |
| `setAllRooms` | `Room[]` | 全量替换会话列表 |
| `setAllChatGroups` | `ChatGroup[]` | 全量替换分组 |
| `sendAddRequest` | `{flag,data}` | 好友/群申请通知 |
| `setShutUp` | `bool` | 禁言状态 |
| `notify` | `{priority,roomId,at,data:{title,body,hasReply,replyPlaceholder},isSelfMsg,image}` | 桌面通知（客户端自行决定是否弹出，含去抖） |
| `notifyMessage` | `{title,message}` | 通知 |
| `notifyError` | `{title,message}` | 错误通知 |
| `message`/`messageSuccess`/`messageError` | `msg` | 气泡提示 |
| `closeLoading` | — | 关加载态 |

---

## 8. Token / 上传 / HTTP 补充接口

### 8.1 Token 事件（带回调）
| 事件 | 参数 | 返回 |
|------|------|------|
| `requestToken` | — | 一次性 `sendImg` token（大写随机，用于 §8.4） |
| `requestGfsToken` | `gin` | GFS token（用于 fileMgr 角色 + 群文件民） |

### 8.2 大文件分片上传（requestUpload / uploadFile）
客户端把本地文件按 512 KiB 分片传给 Bridge 暂存，成功后以哈希代替本地路径：
1. 计算整文件 `sha256` = `fileHash`。
2. `emit('requestUpload', fileName, fileHash, fileSize, cb)` → cb `{allSuccess, uploaded:number[]}`（已上传偏移）。
3. 对每个缺失分片（`offset = i*512k`，末片取余）：
   `emit('uploadFile', fileHash, offset, chunk<Buffer>, chunkSha256, cb)` → cb `boolean`；失败重试至多 3 次。
4. `allSuccess` 后 `data.file.path = fileHash` 再 `emit('sendMessage', data)`。
- 服务端 30 分钟过期、每 5 分钟清理；`chunk` 可为 `Buffer`/`Uint8Array`/`number[]`。

### 8.3 GFS 群文件（fileMgr 角色，网页端）
`fileMgr` 认证（带 `gin` 的 GFS token）后注册事件：
`ls(fid,start,cb)` · `download(fid,cb)->{name,url}` · `stat(fid,cb)` · `mkdir(name,cb)` · `mv(fid,dirId,cb)` · `rm(fid,cb)` · `rename(fid,name,cb)`。
`ls` 返回列表每条含 `user_name`；需 `start` 分页直到 <100 条。

### 8.4 HTTP 接口（ExpressProvider，同端口）
| 方法路径 | 说明 |
|------|------|
| `POST /api/:token/sendMessage` | 带 `b64` 媒体（图片/语音）的消息发送；`token` 来自 `requestToken`（一次性）；body=`SendMessageParams`（`media[].b64` 已内嵌）；成功 `202`，非法 token `403`；body 上限 `100mb` |
| `GET /file-manager/*` | 群文件管理前端（静态） |
| `GET /records/*` | 语音记录音频（`file.url` 文件名） |
| `GET /ping` | `{code:'200',status:'success',data:'pong'}` |

> 客户端发送媒体路径：
> - 图片/语音（有 `b64`）→ **HTTP** `/api/:token/sendMessage`。
> - 音频仅 URL/fid → `sendMessage`（record 段）。
> - 普通文件 → §8.2 分片上传后 `sendMessage`。
> - 纯文本/url 图片 → `sendMessage`。

---

## 9. 关键编码细节

### 9.1 roomId 语义
- **私聊**：`roomId = 对方 uin`（正数）。
- **群聊**：`roomId = -group_id`（负数）。
- 群文件 GFS：`gin = 群号`（正），与消息 roomId 相反。

### 9.2 uid / 时间戳
`message.time`/`room.utime` 单位毫秒；`timestamp`/`date` 为格式串（`hh:mm:ss` / `yyyy/MM/dd`）。`_id` 为协议消息 id。

### 9.3 message_id（base64）解析
`_id`（群/私聊）为 base64 编码的二进制，长度与字段偏移不同：
- **21 字节（群聊）**：`readUInt32BE(4)` = 发送者 user_id；`readUInt32BE(8)` = msg_seq；`readUInt32BE(16)` = 时间。
- **17 字节（私聊 C2C）**：`readUInt32BE(0)` = 发送者 user_id；`readUInt32BE(4)` = msg_seq；`readUInt32BE(12)` = 时间。
- 用于：回复（取 replyUin / msg_seq）、按钮回调（取 msg_seq）、以及服务端在接收侧生成 `time` 的辅助。

服务端接收消息时若同一秒多条，用自增 `id` 修正 `time` 保证排序（`time = data.time*1000 + lastReceivedMessageInfo.id`）。

### 9.4 在线状态枚举（OnlineStatusType）
`Online=11, Afk=31, Hide=41, Busy=50, Qme=60, DontDisturb=70`。

### 9.5 SpecialFeature 枚举
`'IdLogin'|'WebApps'|'RemoteStickers'|'GroupFiles'|'OnlineStatus'`（Bridge 可能提示某些功能不可用）。

### 9.6 道具 / 其它小表
- 猜拳：`[猜拳]<石头/剪刀/布>`（id 1/2/3）。
- 骰子：`[随机骰子]点数<N>`。
- poke 动作名查表（回戳/戳一戳/比心/点赞/心碎/666/放大招/敲门/抓一下/碎屏/勾引/手雷/结印/召唤术/玫瑰花/让你皮/宝贝球）。

---

## 10. 登录流程状态机（客户端侧）

```
连上 → requireAuth → auth(签名)
   ├─ authSucceed → requestSetup? → 显示登录窗
   │                （否则等 onlineData）
   ├─ onlineData → 已登录，加载主界面，接收 setAllRooms/setAllChatGroups
   ├─ login-* 事件 → 走验证流程（扫码/短信/滑块/设备验证）
   └─ fatal → 退出
客户端初始化(无已保存账号)也走"登录窗"：用户填 LoginForm → emit('login', form) → 服务端 createBot → login
已登录会话切换账号（远端登录）：socket.emit('login', form)
```
`LoginForm`：
```ts
{ username, password, protocol:number,
  storageType:'mdb'|'redis'|'sqlite'|'mysql'|'pg',
  mdbConnStr?, rdsHost?, sqlHost?, sqlUsername?, sqlPassword?, sqlDatabase?,
  signAPIAddress?, signAPIKey?, forceAlgoT544?, useNT?, apkInfo?, autologin?, onlineStatus? }
```

---

## 11. 客户端实现 checklist（对照自测）

- [ ] 连接 `io(server,{transports:['websocket']})`，监听 `connect/disconnect/connect_error/requireAuth/authSucceed/authFailed/dbUpgradeProgress/requestSetup/fatal/onlineData`。
- [ ] 握手：`auth(await ed25519Sign(salt, privateKey))`（原始 64 字节；客户端与协议版本比对 2.26.3）。
- [ ] `onlineData` 后：设 uin/bkn/nick，加载主界面，停止加载态。
- [ ] 全量同步：`setAllRooms` / `setAllChatGroups` 覆盖本地面板。
- [ ] 消息渲染：实现 `parseMessageText` 的 5 类伪标记扫描 + linkify + `[Face:N]` / `[QLottie]` / `[Forward:]` / `<IcalinguaAt>` 视觉化。
- [ ] 发送文本：content 含 `[Face: N]`/`<IcalinguaAt>`；媒体：
   - 文件 → `requestUpload`+`uploadFile`（512KiB, sha256, 断点续传）→ path=hash → `sendMessage`。
   - 图片b64/语音b64 → HTTP `/api/:token/sendMessage`。
   - 语音有 fid → `sendMessage` 的 record 段。
- [ ] 拉取：`fetchMessages(roomId, {before|after: {time,id}}, ack)`；`fetchImageMessages`；`fetchMessagesAround`；`fetchMessagesBySender`；`searchMessages`；`fetchHistory` 系列。
- [ ] 实时：`addMessage` → 插入 + 自动 `reportRead`；`renewMessage`（语音解码完成/撤回）；`updateRoom` 未读；`setMessages` 整页；`deleteMessage`。
- [ ] 已读：`syncRead`(push) / `markMessageUnread`/`resolveUnreadTargetMessageId`(req)。
- [ ] 会话/分组：addRoom/removeChat/getRoom/getUnreadCount/getFirstUnreadRoom/pinRoom/setRoomPriority/ignoreChat/addChatGroup/updateChatGroup/removeChatGroup。
- [ ] 联系人/群：getFriend/getGroup/getFriendsFallback/getGroups/getGroupMembers/getGroupMemberInfo/setGroupNick/setGroupKick/setGroupBan/setGroupAnonymousBan/setGroupLeave/setGroupRemark/setFriendRemark/sendGroupPoke/sendGroupSign/handleRequest。
- [ ] 其它：getCookies/getIgnoredChats/getRoamingStamp/getSystemMsg/getDisabledFeatures/getLoginDevices/deleteLoginDevice/sendPacket/getForwardMsg/makeForward/getGroupFileMeta/validateMessageSearchIndex。
- [ ] 媒体 URL：兼容 gchat/multimedia/appid fileid、`records` 语音、md5→gchatpic 规则。
- [ ] 回复：`replyMessage` 摘要展示；发送时带上 `replyMessage._id`。

---

### 附：全部事件名速查

**Server→Client**：`requireAuth` `authSucceed` `authFailed` `dbUpgradeProgress` `requestSetup` `fatal` `setOnline` `setOffline` `onlineData` `login-verify` `login-qrcodeLogin` `login-smsCodeVerify` `login-error` `login-slider` `updateRoom` `addMessage` `renewMessage` `renewMessageURL` `deleteMessage` `setMessages` `addMessageText` `syncRead` `setAllRooms` `setAllChatGroups` `sendAddRequest` `setShutUp` `notify` `notifyMessage` `notifyError` `message` `messageSuccess` `messageError` `closeLoading`

**Client→Server**：`auth` `login` `submitSmsCode` `randomDevice` `reLogin` `login-verify-reLogin` `login-slider-ticket` `setOnlineStatus` `sendMessage` `fetchMessages` `fetchImageMessages` `fetchMessagesAround` `fetchMessagesBySender` `searchMessages` `resolveUnreadTargetMessageId` `markMessageUnread` `fetchHistory` `fetch7DaysHistory` `stopFetchingHistory` `reportRead` `getForwardMsg` `makeForward` `getMsgNewURL` `getNTPicURLbyFileid` `getGroupFileMeta` `requestToken` `requestGfsToken` `requestUpload` `uploadFile` `getPrivateFileUrl` `addRoom` `removeChat` `getRoom` `getUnreadCount` `getFirstUnreadRoom` `addChatGroup` `updateChatGroup` `removeChatGroup` `updateRoom` `updateMessage` `renewMessage` `renewMessageURL` `hideMessage` `revealMessage` `deleteMessage` `pinRoom` `setRoomPriority` `setRoomAutoDownload` `setRoomAutoDownloadPath` `ignoreChat` `removeIgnoredChat` `getIgnoredChats` `getFriend` `getGroup` `getFriendsFallback` `getGroups` `getGroupMembers` `getGroupMemberInfo` `setGroupNick` `setGroupKick` `setGroupLeave` `setGroupBan` `setGroupAnonymousBan` `setGroupRemark` `setFriendRemark` `sendGroupPoke` `sendGroupSign` `sendButtonCallback` `handleRequest` `getCookies` `getRoamingStamp` `getSystemMsg` `getDisabledFeatures` `getLoginDevices` `deleteLoginDevice` `sendPacket` `validateMessageSearchIndex`
