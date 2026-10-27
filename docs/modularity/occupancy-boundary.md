# C12 占用唯一权威与资源池协作边界

当前状态：频道 Full 占用由顶层 SessionPool.ActiveSessionId 决定，ListenSessionIds 为同池旁听列表；子池 ChannelId=0 的 ActiveSessionId 是子会话身份，不是频道 claim。Members 和 _poolByMember 负责 compact 历史引用及父子寻址，Parent/ChildPools 负责资源树，不能整体迁给频道模块。

已核对写入点：

| 动作 | 当前边界 |
|---|---|
| ClaimChannel | _poolLock 内创建顶层池、Claim/Listen、写 _poolByMember |
| ReleaseChannel | _poolLock 内释放 Full/Listen、空池回收及历史成员索引清理 |
| ReleaseSessionClaims | _poolLock 内按成员索引定位、仅释放当前活跃 Full、回收空池 |
| CreateSubPool | _poolLock 内创建无频道子池、Claim 子会话身份、挂父树和成员索引 |
| TakeOverChannelPool（升级） | _poolLock 内改 ActiveSessionId/Members 与新成员索引 |
| CreateFromCompact | FindPoolBySession 仅查找持锁，随后 TakeOver 在锁外，且未立即写新成员索引 |
| RemoveEmptyPool | 调用者持 _poolLock，顶层移除频道索引/子池移除父边，清理所有历史成员索引 |
| OnChildCompleted | _poolLock 内摘子树与成员索引，并更新父会话持久化子列表 |

第一步必须修正 compact 接管原子边界：用按旧成员 id 定位的统一操作，在 _poolLock 内同时 TakeOver 和更新 _poolByMember，新旧成员引用立即解析同一活跃 id；支持无频道子池，不经过仅顶层有效的 TakeOverChannelPool。没有池时保留旧行为继续创建。此步不改变 claim 规则，不迁移状态。

后续状态提纯方案：Lilara.Channel 拥有 ChannelOccupancyState（Full holder 与 Listen 集合），顶层池仅持有该对象的引用并委托占用读写，不再另存同一频道的 active/listen 字段；子池仍使用独立会话身份。频道 registry 和顶层池关联必须在同一个协调锁中创建/删除。SessionPool.Members/Parent/ChildPools 及成员索引仍归 Session 模块。禁止为“迁移”复制当前占用到另一张字典后保留原字段可写。

原子操作定义：Acquire(channel,session)、Listen、Release、TakeOver(old,new)、RemoveEmpty 都由同一协调边界调用占用状态和池树更新；ChannelOccupancyState 不自行取得第二把锁，避免锁顺序反转。图 acquire/release 最终只产出意图，由协调边界提交，不在 Evaluate 中直接修改池树或占用。

实施顺序：

1. compact 接管与新成员索引原子化，顶层/子池/旧会话释放/幂等重入回归。
2. 提纯占用状态，由顶层池唯一持有并委托，行为不变；先验证没有双权威。
3. 频道 registry 统一持有占用对象并与顶层池引用共享，创建/回收仍保持同一协调锁，停止暴露可变列表。
4. 通过 SDK 只读快照与连接端口消费唯一状态；池树/占用并发、compact/escalate失败回滚和重启恢复对拍通过后，再移出 SessionEngine 协调实现。

当前尚未验证：接管预定新 id 后创建失败的回滚、同会话多频道 Listen 与单成员索引语义、跨线程 ResolveActiveSession/EffectiveParent 的无锁读取、Listen 释放后的索引清理。必须逐项明确语义并补测试，不能凭现有全绿测试宣告 C12 完成。占用状态不落图结构，恢复仍按 spec/状态重建；D1全路径对拍与D5不在本文完成范围。
