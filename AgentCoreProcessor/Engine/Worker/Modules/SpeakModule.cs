using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Engine.Modules
{
    internal class SpeakModule : EngineModule
    {
        public override string Name => "说话";

        public Func<string, Task>? OnSpeak { get; set; }
        public Func<string, string?, List<MessageAttachment>, Task>? OnSendMedia { get; set; }

        public bool HadSpeakThisRound { get; private set; }

        public override void Attach(ILoopBus bus)
        {
            bus.Subscribe<ToolExecutedEvent>(e =>
            {
                if (e.Call.Tool == "speak" && e.Result.IsSuccess && OnSpeak != null)
                {
                    OnSpeak(e.Result.Data ?? "").GetAwaiter().GetResult();
                    HadSpeakThisRound = true;
                }
                else if (e.Call.Tool == "send_media" && e.Result.IsSuccess && OnSendMedia != null)
                {
                    try
                    {
                        var json = JObject.Parse(e.Result.Data ?? "{}");
                        var type = json["type"]?.ToString() ?? "";
                        var path = json["path"]?.ToString() ?? "";
                        var text = json["text"]?.ToString();

                        var attachmentType = type switch
                        {
                            "image" or "sticker" => AttachmentType.Image,
                            "voice" => AttachmentType.Audio,
                            "file" => AttachmentType.File,
                            _ => AttachmentType.Image
                        };

                        var attachments = new List<MessageAttachment>
                        {
                            new()
                            {
                                Type = attachmentType,
                                LocalPath = IsLocalPath(path) ? path : null,
                                SourceUrl = IsLocalPath(path) ? null : path,
                                Category = type == "sticker" ? "sticker" : null
                            }
                        };

                        OnSendMedia(type, text, attachments).GetAwaiter().GetResult();
                        HadSpeakThisRound = true;
                    }
                    catch (Exception ex)
                    {
                        FrameworkLogger.Log("SpeakModule", $"发送媒体失败: {ex.Message}");
                    }
                }
            });
        }

        public void ResetRound()
        {
            HadSpeakThisRound = false;
        }

        public override void Reset()
        {
            HadSpeakThisRound = false;
            OnSpeak = null;
            OnSendMedia = null;
        }

        private static bool IsLocalPath(string path)
        {
            return path.Length > 1 && (path[1] == ':' || path.StartsWith('/') || path.StartsWith("\\\\"));
        }
    }
}
