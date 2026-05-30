using System.Text.Json;

namespace Plugin.Email;

public class EmailConfig
{
    public SmtpConfig Smtp { get; set; } = new();
    public ImapConfig Imap { get; set; } = new();

    public static EmailConfig Load(string configDir)
    {
        Directory.CreateDirectory(configDir);
        var path = Path.Combine(configDir, "Email.json");

        if (!File.Exists(path))
        {
            var cfg = new EmailConfig();
            var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            return cfg;
        }

        try
        {
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<EmailConfig>(text,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch
        {
            return new EmailConfig();
        }
    }
}

public class SmtpConfig
{
    public string Host { get; set; } = "smtp.qq.com";
    public int Port { get; set; } = 465;
    public bool UseSsl { get; set; } = true;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public class ImapConfig
{
    public string Host { get; set; } = "imap.qq.com";
    public int Port { get; set; } = 993;
    public bool UseSsl { get; set; } = true;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}
