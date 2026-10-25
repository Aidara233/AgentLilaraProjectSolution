namespace AgentLilara.Kernel.Graph;

/// <summary>节点类别。对齐对象模型：激活源=生命周期、激活汇=方法、数据源=属性读、数据汇=属性写。</summary>
public enum NodeKind
{
    Source,
    Logic,
    Sink,
}

/// <summary>图类别。Static = 静态类（唯一实例、无状态约束下的求解）；Dynamic = 动态类（多实例、时钟沿快照）。</summary>
public enum GraphKind
{
    Static,
    Dynamic,
}

/// <summary>
/// 一次求解中的节点。输入口必须被恰好一条连线喂满；输出口在一次求解中只写一次。
/// </summary>
public interface INode
{
    string Id { get; }

    NodeKind Kind { get; }

    IReadOnlyList<string> InputPorts { get; }

    IReadOnlyList<string> OutputPorts { get; }

    /// <summary>
    /// 是否为"激活源"（生命周期入口）。每图恰好一个；数据源（属性读）为 false。
    /// </summary>
    bool IsActivationSource => false;

    IReadOnlyDictionary<string, Cell> Evaluate(IReadOnlyDictionary<string, Cell> inputs, EvalContext context);
}

/// <summary>连线：某个节点的某个输出口 → 另一个节点的某个输入口。</summary>
public sealed record Link(string FromNode, string FromPort, string ToNode, string ToPort);

/// <summary>变量声明（内部存储）。初值必须是常量。</summary>
public sealed record VariableDecl(string Name, Cell InitialValue, bool Mutable = true, string? Description = null);

/// <summary>图类（源码），由节点与连线构成。</summary>
public sealed class GraphClass
{
    public GraphClass(
        string classId,
        GraphKind kind,
        IReadOnlyList<INode> nodes,
        IReadOnlyList<Link> links,
        IReadOnlyList<VariableDecl>? variables = null)
    {
        ClassId = classId;
        Kind = kind;
        Nodes = nodes;
        Links = links;
        Variables = variables ?? Array.Empty<VariableDecl>();
    }

    public string ClassId { get; }

    public GraphKind Kind { get; }

    public IReadOnlyList<INode> Nodes { get; }

    public IReadOnlyList<Link> Links { get; }

    public IReadOnlyList<VariableDecl> Variables { get; }

    public INode? FindNode(string id) => Nodes.FirstOrDefault(n => n.Id == id);
}

/// <summary>图定义静态校验失败（连通性/绑定/唯一激活源等）。</summary>
public sealed class GraphValidationException : Exception
{
    public GraphValidationException(string message) : base(message)
    {
    }
}
