using System.IO;
using AgentCoreProcessor.Config;
using AgentLilara.PluginSDK.Services;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Tool.Host
{
    internal class ReviewControlImpl : IReviewControl
    {
        private volatile bool _wakeNotified;
        private volatile bool _reserveUsed;
        private volatile bool _completed;
        private readonly int _reserveBudget;

        private static string ProgressPath =>
            Path.Combine(PathConfig.StoragePath, "Dream", "DreamProgress.json");

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

        public void SaveProgress(string progressJson)
        {
            var dir = Path.GetDirectoryName(ProgressPath);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(ProgressPath, progressJson);
        }

        public string? LoadProgress()
        {
            if (!File.Exists(ProgressPath)) return null;
            return File.ReadAllText(ProgressPath);
        }
    }
}
