using System;
using Microsoft.AspNetCore.Components;

namespace AgentCoreProcessor.WebUI.Components.Shared
{
    public class DataColumn<T>
    {
        public string Header { get; init; } = "";
        public Func<T, object?>? Value { get; init; }
        public RenderFragment<T>? Template { get; init; }
        public bool Sortable { get; init; } = true;
    }
}
