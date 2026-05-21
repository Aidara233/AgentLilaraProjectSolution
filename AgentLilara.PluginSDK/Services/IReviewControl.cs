namespace AgentLilara.PluginSDK.Services
{
    public interface IReviewControl
    {
        bool RequestReinforcement();
        void MarkComplete();
        void SaveProgress(string progressJson);
        string? LoadProgress();
    }
}
