# 专用核心（已删除）

`SleepTalkCore` 与 `PreprocessingCore` 两个专用核心已随死代码清理删除（2026-08-24）。

- **`SleepTalkCore`（梦话生成）**：`Core/SleepTalkCore.cs` + `Storage/Core/SleepTalkCore.json` 已删除。原为做梦系统（DreamEngine）的一部分，DreamEngine 已随记忆重构移除，类无任何实例化点；`SleepTalkCore.json` 的 `promptFiles` 还挂着一个从不存在的 `Persona.txt`。
- **`PreprocessingCore`（消息分类器）**：`Core/PreprocessingCore.cs` 已删除。基于 Embedding 相似度的聊天/任务二分类，全仓无任何调用点（原设计供频道循环决策，频道循环已随 express 会话化移除）。

若将来需要消息二分类能力，需重新接线到 express/working 会话并新建配置。