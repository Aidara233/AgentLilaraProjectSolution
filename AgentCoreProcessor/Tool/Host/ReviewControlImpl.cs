using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Tool.Host
{
    internal class ReviewControlImpl : IReviewControl
    {
        private volatile bool _wakeNotified;
        private volatile bool _reserveUsed;
        private volatile bool _completed;
        private readonly int _reserveBudget;

        public bool IsCompleted => _completed;
        public bool ReserveGranted => _reserveUsed;
        public bool WakeNotified => _wakeNotified;
        public int ReserveBudget => _reserveBudget;

        public ReviewControlImpl(int reserveBudget)
        {
            _reserveBudget = reserveBudget;
        }

        public void NotifyWake() => _wakeNotified = true;

        public bool RequestReinforcement()
        {
            if (_wakeNotified || _reserveUsed) return false;
            _reserveUsed = true;
            return true;
        }

        public void MarkComplete() => _completed = true;
    }
}
