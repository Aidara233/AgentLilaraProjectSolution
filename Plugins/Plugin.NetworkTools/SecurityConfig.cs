// Plugins/Plugin.NetworkTools/SecurityConfig.cs
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Plugin.NetworkTools;

public enum SecurityMode { Blacklist, Whitelist, None }

public class SecurityConfig
{
    public SecurityMode Mode { get; set; } = SecurityMode.Blacklist;
    public List<string> Domains { get; set; } = new();
    public bool BlockPrivateIps { get; set; } = true;

    public int DefaultTimeoutSeconds { get; set; } = 30;
    public int MaxResponseBodyBytes { get; set; } = 20480;
    public string UserAgent { get; set; } = "AgentLilara-NetworkTools/1.0";
    public int MaxRedirects { get; set; } = 5;

    public int MaxConcurrentDownloads { get; set; } = 3;
    public int ChunkSizeBytes { get; set; } = 8192;

    // ── Loading ──

    public static SecurityConfig Load(string configDir)
    {
        Directory.CreateDirectory(configDir);
        var path = Path.Combine(configDir, "NetworkTools.json");

        if (!File.Exists(path))
        {
            var defaults = new SecurityConfig();
            var json = JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SecurityConfig>(json) ?? new SecurityConfig();
        }
        catch
        {
            return new SecurityConfig();
        }
    }

    // ── Domain validation ──

    /// <summary>检查给定URL是否允许访问。返回null表示通过，否则返回拒绝原因。</summary>
    public string? ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "URL格式无效";

        if (uri.Scheme != "http" && uri.Scheme != "https")
            return "仅支持 http/https 协议";

        var host = uri.Host;

        // 域名检查
        switch (Mode)
        {
            case SecurityMode.Blacklist:
                if (Domains.Any(d => MatchDomain(host, d)))
                    return $"域名 {host} 在黑名单中";
                break;
            case SecurityMode.Whitelist:
                if (!Domains.Any(d => MatchDomain(host, d)))
                    return $"域名 {host} 不在白名单中";
                break;
        }

        return null; // 通过
    }

    /// <summary>检查IP是否为私有/内网地址。返回null表示通过（公网），否则返回拒绝原因。</summary>
    public string? ValidateIp(string host)
    {
        if (!BlockPrivateIps) return null;

        if (!IPAddress.TryParse(host, out var ip))
        {
            // 尝试DNS解析
            try
            {
                var addresses = Dns.GetHostAddresses(host);
                if (addresses.Length == 0)
                    return $"无法解析域名: {host}";
                ip = addresses[0];
            }
            catch
            {
                return $"DNS解析失败: {host}";
            }
        }

        if (IsPrivateIp(ip))
            return $"禁止访问内网地址: {ip}";

        return null;
    }

    /// <summary>检查重定向目标是否与原始host不同（需重新校验）。</summary>
    public bool HostChanged(string originalHost, string newUrl)
    {
        if (!Uri.TryCreate(newUrl, UriKind.Absolute, out var uri))
            return true;
        return !string.Equals(originalHost, uri.Host, StringComparison.OrdinalIgnoreCase);
    }

    // ── Private helpers ──

    private static bool MatchDomain(string host, string pattern)
    {
        if (pattern.StartsWith("*."))
        {
            var suffix = pattern[2..];
            return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && host.Length > suffix.Length;
        }
        return string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPrivateIp(IPAddress addr)
    {
        if (IPAddress.IsLoopback(addr)) return true;

        var bytes = addr.GetAddressBytes();

        if (addr.AddressFamily == AddressFamily.InterNetwork)
        {
            // 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // 169.254.0.0/16 (link-local)
            if (bytes[0] == 169 && bytes[1] == 254) return true;
        }
        else if (addr.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IPv6Loopback.Equals(addr)) return true;
            // fc00::/7
            if (bytes.Length >= 1 && bytes[0] >= 0xfc && bytes[0] <= 0xfd) return true;
        }

        return false;
    }
}
