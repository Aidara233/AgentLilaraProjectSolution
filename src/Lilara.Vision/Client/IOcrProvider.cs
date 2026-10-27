using System.Threading.Tasks;

namespace AgentCoreProcessor.Client
{
    internal class OcrResult
    {
        public bool HasText { get; set; }
        public string? Text { get; set; }
    }

    internal interface IOcrProvider
    {
        Task<OcrResult> RecognizeAsync(string imagePath);
    }
}
