# 模型图工具（N3）

实现提交：节点目录 `70b1b65`；文件管理与工具注册见后续提交。

启动时注册四个非组件工具：

| 工具 | 输入 | 结果 |
|---|---|---|
| wire_describe | 可选 class_id | 不传则列图类、节点端口/参数示例、示例图和当前挂载表；传入则读取完整源码 |
| wire_validate | source（完整图 JSON） | 通过生产 GraphLoader 与 Solver 静态校验，不执行图 |
| wire_apply | source（完整图 JSON） | 校验后保存 Storage/Graphs/{classId}.json，返回审计版本 |
| wire_mount | source（完整挂载表 JSON） | 验证引用图、作用域、重复实例与激活源后替换 mounts.json |

wire_mount 使用完整表：先 describe，保留其他挂载再修改。`{"version":1,"mounts":[]}` 卸载全部挂载。现有作用域为 channel/session/global；触发源为 message/error。外部信源继续走订阅机制。

图源码更新不会直接修改已有运行器；重新写入挂载表触发 GraphManager 原有重载路径，或重启后使用新定义。挂载重载会销毁挂载实例的变量，遵循原有 GraphManager 语义。订阅实例有独立生命周期。

审计文件为 Storage/Graphs/.history/{revision}/change.json，包含 UTC 时间、目标文件名、before/after 全文。写审计成功后才原子替换目标。回滚时可将历史 before 源码重新提交给工具；创建新图的 before 为 null。工具只能写图源码与挂载表，不接受配置文件路径，不修改 caps 或治理配置。

节点目录来自 BuiltInNodeFactory：类别、端口与激活源标志取自探针节点，参数声明与示例显式维护。目录缺少某个已注册类型时失败，避免返回虚假的端口。当前只支持生产加载器已提供的内建节点；模块实现绑定仍由 ModuleHost 管理。

验证：主程序测试 487 项通过（新增 6 项），GraphRuntime 33 项通过（新增 2 项）。覆盖目录完整性、参数示例与必填参数、示例源码、新建→挂载→真实管理器加载→卸载、非法替换保留原文、历史前后版本、路径/保留类名与错误触发源。尚未使用真实模型凭据调用这四个工具；未启动真实适配器。

后续：模块类型默认挂载、图求解阶段数据流日志、内置路径迁移，仍按重构主计划继续。
