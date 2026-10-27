using System.Linq.Expressions;
using System.Reflection;

namespace AgentCoreProcessor.Engine.Session;

/// <summary>部件只采集变量值；不负责重建资源或修改图结构。</summary>
internal interface ISnapshotPort
{
    string Owner { get; }
    void Capture(SnapshotWriter writer);
    void Restore(SessionStateSnapshot snapshot);
}

internal sealed record SnapshotPort(string Owner, Action<SnapshotWriter> CaptureValues,
    Action<SessionStateSnapshot>? RestoreValues = null) : ISnapshotPort
{
    public void Capture(SnapshotWriter writer) => CaptureValues(writer);
    public void Restore(SessionStateSnapshot snapshot) => RestoreValues?.Invoke(snapshot);
}

/// <summary>按字段记录归属，重复采集立即失败。强类型选择器防止字段名和类型漂移。</summary>
internal sealed class SnapshotWriter
{
    private readonly SessionStateSnapshot _snapshot = new();
    private readonly Dictionary<string, string> _owners = new(StringComparer.Ordinal);
    private string _owner = "";

    public void Set<T>(Expression<Func<SessionStateSnapshot, T>> field, T value)
    {
        if (field.Body is not MemberExpression { Member: PropertyInfo property, Expression: ParameterExpression })
            throw new ArgumentException("snapshot field must be a direct property selector", nameof(field));
        if (!_owners.TryAdd(property.Name, _owner))
            throw new InvalidOperationException($"snapshot field '{property.Name}' belongs to both '{_owners[property.Name]}' and '{_owner}'");
        property.SetValue(_snapshot, value);
    }

    public SessionStateSnapshot Capture(IEnumerable<ISnapshotPort> ports)
    {
        foreach (var port in ports)
        {
            _owner = port.Owner;
            port.Capture(this);
        }
        var missing = typeof(SessionStateSnapshot).GetProperties().Select(p => p.Name)
            .Where(name => !_owners.ContainsKey(name)).ToArray();
        if (missing.Length != 0)
            throw new InvalidOperationException("snapshot fields have no owner: " + string.Join(", ", missing));
        return _snapshot;
    }
}
