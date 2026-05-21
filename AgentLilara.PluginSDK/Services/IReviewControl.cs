namespace AgentLilara.PluginSDK.Services
{
    public interface IReviewControl
    {
        bool IsCompleted { get; }
        bool WakeNotified { get; }
        bool ReserveGranted { get; }
        bool RequestReinforcement();
        void MarkComplete();
    }
}
