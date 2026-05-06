namespace AgentCoreProcessor.WebUI.Components.Shared
{
    public enum AlertLevel { Info, Warning, Error }

    public class AlertItem
    {
        public AlertLevel Level { get; init; }
        public string Source { get; init; } = "";
        public string Message { get; init; } = "";
        public string? LinkHref { get; init; }
    }
}
