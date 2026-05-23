using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AgentLilara.PluginSDK.WebUI;

[JsonDerivedType(typeof(TableSchema), "table")]
[JsonDerivedType(typeof(StatusSchema), "status")]
[JsonDerivedType(typeof(FormSchema), "form")]
[JsonDerivedType(typeof(StreamSchema), "stream")]
[JsonDerivedType(typeof(ChatSchema), "chat")]
[JsonDerivedType(typeof(TreeSchema), "tree")]
[JsonDerivedType(typeof(DetailSchema), "detail")]
[JsonDerivedType(typeof(ActionCardSchema), "action")]
public abstract class CardSchema { }

// --- Table ---

public class TableSchema : CardSchema
{
    public required List<ColumnDef> Columns { get; init; }
    public bool Searchable { get; init; } = true;
    public bool Paginated { get; init; } = true;
    public int DefaultPageSize { get; init; } = 20;
    public List<RowAction>? RowActions { get; init; }
    public List<FilterDef>? Filters { get; init; }
}

public class FilterDef
{
    public required string Field { get; init; }
    public required string Label { get; init; }
    public required List<SelectOption> Options { get; init; }
    public bool AllowEmpty { get; init; } = true;
}

public class ColumnDef
{
    public required string Field { get; init; }
    public required string Header { get; init; }
    public bool Sortable { get; init; } = true;
    public string? Width { get; init; }
    public ColumnFormat Format { get; init; } = ColumnFormat.Text;
}

public enum ColumnFormat { Text, DateTime, Badge, Link, Image, Custom }

public class RowAction
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string? Icon { get; init; }
    public string? Confirm { get; init; }
    public bool Danger { get; init; }
}

// --- Status ---

public class StatusSchema : CardSchema
{
    public required List<StatusField> Fields { get; init; }
    public List<ActionButton>? Actions { get; init; }
}

public class StatusField
{
    public required string Field { get; init; }
    public required string Label { get; init; }
    public StatusFieldType Type { get; init; } = StatusFieldType.Text;
    public bool IsMultiline { get; init; }
}

public enum StatusFieldType { Text, Badge, Progress, Indicator, DateTime }

public class ActionButton
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string? Icon { get; init; }
    public string? Confirm { get; init; }
    public bool Danger { get; init; }
}

// --- Form ---

public class FormSchema : CardSchema
{
    public required List<FormField> Fields { get; init; }
    public List<FormGroup>? Groups { get; init; }
    public bool ShowReset { get; init; } = true;
}

public class FormField
{
    public required string Field { get; init; }
    public required string Label { get; init; }
    public FormFieldType Type { get; init; } = FormFieldType.Text;
    public string? Placeholder { get; init; }
    public string? Description { get; init; }
    public bool Required { get; init; }
    public List<SelectOption>? Options { get; init; }
    public string? Group { get; init; }
}

public enum FormFieldType { Text, Number, TextArea, Select, Toggle, Radio, Password, Json }

public class FormGroup
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool DefaultCollapsed { get; init; }
}

public class SelectOption
{
    public required string Value { get; init; }
    public required string Label { get; init; }
}

// --- Stream ---

public class StreamSchema : CardSchema
{
    public int MaxLines { get; init; } = 500;
    public bool AutoScroll { get; init; } = true;
    public bool ShowPauseButton { get; init; } = true;
    public bool ShowFilter { get; init; } = true;
}

// --- Chat ---

public class ChatSchema : CardSchema
{
    public bool ShowSenderSwitch { get; init; } = true;
    public bool ShowInput { get; init; } = true;
    public List<string>? Senders { get; init; }
}

// --- Tree ---

public class TreeSchema : CardSchema
{
    public required string NodeIdField { get; init; }
    public required string NodeLabelField { get; init; }
    public string? ParentIdField { get; init; }
    public string? ChildrenField { get; init; }
    public bool Expandable { get; init; } = true;
}

// --- Detail ---

public class DetailSchema : CardSchema
{
    public required List<DetailSection> Sections { get; init; }
}

public class DetailSection
{
    public required string Title { get; init; }
    public required List<DetailField> Fields { get; init; }
    public bool DefaultCollapsed { get; init; }
}

public class DetailField
{
    public required string Field { get; init; }
    public required string Label { get; init; }
    public ColumnFormat Format { get; init; } = ColumnFormat.Text;
    public bool Editable { get; init; }
}

// --- Action ---

public class ActionCardSchema : CardSchema
{
    public required string ActionId { get; init; }
    public string ActionLabel { get; init; } = "";
    public string? Description { get; init; }
    public List<ActionParamDef> Params { get; init; } = new();
    public string SubmitLabel { get; init; } = "执行";
}

public class ActionParamDef
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public string Type { get; init; } = "text";
    public List<SelectOption>? Options { get; init; }
    public bool Required { get; init; } = true;
}
