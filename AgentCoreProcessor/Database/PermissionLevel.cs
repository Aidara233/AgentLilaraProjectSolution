namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 权限等级（User/账号级）。
    /// 纯框架控制，只能通过管理员指令修改。
    /// </summary>
    public enum PermissionLevel
    {
        Blocked = -1,       // 黑名单，完全不响应
        Restricted = 0,     // 受限，消息入库但不触发响应（静默观察）
        Default = 1,        // 默认，正常交互
        Elevated = 2,       // 提升，可执行敏感工具
        Admin = 3,          // 管理员，可执行管理指令
    }
}
