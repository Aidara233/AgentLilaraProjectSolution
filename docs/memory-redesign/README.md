# 记忆重构计划（Memory Redesign）

> **状态：设计中，存储/经历/情绪/引擎骨架已定稿** — 2026-08-02
> 历史草案：`docs/superpowers/specs/2026-07-22-memory-redesign.md`（brainstorming 工作区，本目录为其正式化）

## 这是什么

Lilara 记忆系统的**整体推倒重来**。旧系统是纯"事实仓库"——碎片化第三人称命题，缺三个东西：

1. **"我"**：Lilara 作为观察者、参与者、运行者的主体视角
2. **模式**：跨记忆的规律感，"习惯/节奏/因果"
3. **计数/频次**：防重复机制压制了重复信号

能回答"关于 X 你知道什么"，回答不了"有什么规律"和"什么被反复强化过"。

## 核心决策

- **两张表统一记忆** — Claims（断言，三元组）+ Journals（经历，正文式）本就是两类，各用一张；不区分临时库/永久库
- **单库模式** — 写入即正式条目，靠状态字段区分处理阶段，不走 Temp→Main 迁移
- **无关联图** — 关键词库承担关联，时间轴承担序列，不需要 MemoryLinks 边表
- **几乎不覆盖** — 只有同一来源明确更正自己时才覆盖；新信息=新增碎片，不是替换
- **窄命题优先** — 不写"xxx最强"，拆成"用户声称xxx最强"+"xxx在A榜得分X"
- **情绪是真实内部状态** — 核心效果是框架调控，用户感知为辅；唯一写入者 EmotionService
- **产生端收敛进记忆引擎** — 提取、事件、善后统一入口，Writer 是唯一写入口
- **session 善后 = 复活** — end_session 成功即真结束，记忆引擎从存档复活它做整理

## 架构总览

```
                 ┌─────────────────────────────────────────┐
                 │              MemoryEngine               │
                 │  唯一写入口 + 唯一情绪写入者               │
  Express 伴随   │  ┌─────────┐   ┌────────────────────┐    │
  Session 复活   │  │ Writer  │   │   EmotionService   │◄───┼── EmotionState.json
  框架事件       │  │(原子批次)│   │ (锁 + 实时 + 快照)  │    │
  wait/remember  │  └────┬────┘   └────────┬───────────┘    │
                 │       │                 │                │
                 │  ┌────▼────┐  ┌─────────▼────────┐       │
                 │  │ Claims  │  │  Journals        │       │
                 │  │ Keywords│  │  EmotionSnapshot │       │
                 │  │ Fragment│  │  FrameworkEvents │       │
                 │  └─────────┘  └──────────────────┘       │
                 └─────────────────────────────────────────┘
```

- 数据流：产生端（Express/Session/框架/工具）→ Writer（原子批次）→ 落库 + 情绪应用
- 检索：RetrieveService（关键词 + 时间窗口），印证度派生
- 维护：MaintenanceEngine（标定/密度/归档）

## 文件索引

| 文件 | 内容 | 状态 |
|---|---|---|
| [README.md](README.md) | 计划总纲（本文件） | 定稿 |
| [01-storage-model.md](01-storage-model.md) | 存储模型：6 表 + 1 JSON、矛盾表达、类型行为、记录层级、检索 | 定稿 |
| [02-emotion-model.md](02-emotion-model.md) | 情绪模型：五轴、运动公式、框架调控、SDK 边界 | 定稿 |
| [03-memory-engine.md](03-memory-engine.md) | **记忆引擎整体规划**：模块结构、Writer 写协议、产生端、复活善后 | 定稿（骨架） |
| [04-migration-todos.md](04-migration-todos.md) | 旧系统对照、废弃清单、待定项、实施路线 | 待定为主 |

## 关联文档

- **《断言存储系统设计决定.md》**（`docs/断言存储系统设计决定.md`）— Claims 侧唯一权威：三元组结构、无 polarity/confidence、实体不归并、不自动仲裁、同源 utterance_id。本计划所有断言相关规则以其为准。
- `docs/architecture-map.md` — 当前系统架构全景（本计划将替换其中记忆/做梦/复盘部分）。

## 状态

**已定稿**：
- 存储表结构（6 表 + 1 JSON）
- 记录层级 L0-L3 + 升级认领
- 情绪模型（五轴 + 公式 + 框架调控 + SDK）
- 记忆引擎模块结构 + Writer 写协议
- 产生端全链路（Express 伴随提取 + wait + Session 复活善后 + 框架事件启停）
- 提取协议 JSON 契约

**待定**：见 [04-migration-todos.md](04-migration-todos.md) — 数值类配置、事件映射表、维护引擎、念头系统、检索细节等。
