using System.Security.Cryptography;
using System.Text.Json;

namespace PanelDeck;

public sealed record Reading(string Id, string Hardware, string Kind, string Name, string Type, float? Value);
public sealed record Metric(string Label, string Unit, float? Value, string? Source);
public sealed record ScreenDecision(string Mode, string Reason);
public sealed record PcState(bool Suspended, bool ManualOff);
public sealed record Snapshot(long Timestamp, string Computer, ScreenDecision Screen,
    string Cpu, string Gpu, Dictionary<string, Metric> Metrics, string? Warning,
    PcState? Pc = null, long SampleAgeMs = 0, int ProtocolVersion = 2, string? PanelTheme = null);

public sealed record PanelThemeOption(string Id, string Name, string Description)
{
    public override string ToString() => Name;
}

public static class PanelThemes
{
    public static readonly PanelThemeOption[] All = [
        new("classic", "经典 · 仪表盘", "经典双列仪表，大字读数与清晰分区。"),
        new("material", "Material · 安卓", "柔和配色与圆角卡片。"),
        new("winui", "WinUI · 微软", "清晰分区与细腻描边。"),
        new("flutter", "Flutter · 清新卡片", "轻盈卡片与鲜明重点色。"),
        new("glass", "macOS · 玻璃质感", "半透明层次与柔和光感。"),
        new("editorial", "纸页 · 数据周刊", "暖纸色与章节排版，支持横竖屏重排。"),
        new("ambient", "静夜 · 桌面时钟", "深色背景、大时钟与紧凑读数，支持横竖屏重排。"),
        new("telemetry", "遥测 · 性能座舱", "全宽 CPU / GPU 数据轨道，支持横竖屏重排。"),
        new("studio", "拼贴 · 硬件工作室", "GPU 主卡与非对称信息块，支持横竖屏重排。")
    ];
    public static bool IsKnown(string? id) => All.Any(theme => theme.Id == id);
    public static string PreviewPath(string? theme = null, bool landscape = false) =>
        "/preview?orientation=" + (landscape ? "landscape" : "portrait") + (IsKnown(theme) ? "&theme=" + theme : "");
}

public sealed class Settings
{
    private static readonly object SaveLock = new();
    public string ListenAddress { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 8080;
    public bool StartMinimized { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool AutoFullCollection { get; set; }
    public bool UseCollectorService { get; set; } = true;
    public bool EnableGameFps { get; set; } = true;
    public int SampleSeconds { get; set; } = 2;
    public int SleepSampleSeconds { get; set; } = 15;
    public string CpuFanId { get; set; } = "";
    // Empty until the first phone connects, preserving its existing selection on upgrade.
    public string PanelTheme { get; set; } = "";
    public string Token { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    [System.Text.Json.Serialization.JsonIgnore] public string? LoadNotice { get; set; }
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PanelDeck");
    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public static Settings Load()
    {
        try { var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); s.Validate(); return s; }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        catch (Exception ex) {
            DiagnosticLog.Write("settings-load", ex);
            try { File.Copy(FilePath, FilePath + ".recovery", true); } catch { }
            return new() { LoadNotice = "原设置无法读取，已恢复默认值；请检查连接设置并重新配对。" };
        }
    }
    public void Validate()
    {
        if (AutoFullCollection) StartWithWindows = true;
        CpuFanId ??= "";
        if (!PanelThemes.IsKnown(PanelTheme)) PanelTheme = "";
        Port = Math.Clamp(Port, 1024, 65535);
        SampleSeconds = Math.Clamp(SampleSeconds, 1, 10);
        SleepSampleSeconds = Math.Clamp(SleepSampleSeconds, 5, 60);
        if (!System.Net.IPAddress.TryParse(ListenAddress, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) ListenAddress = "0.0.0.0";
        if (Token == null || Token.Length != 64 || !Token.All(Uri.IsHexDigit)) Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }
    public Settings Copy() => (Settings)MemberwiseClone();
    public void Save()
    {
        lock (SaveLock) {
        Validate(); Directory.CreateDirectory(DirectoryPath);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
        }
    }
}

public static class ScreenPolicy
{
    public static ScreenDecision Decide(bool suspended, bool manualOff)
    {
        if (suspended) return new("off", "电脑正在休眠");
        if (manualOff) return new("off", "已手动息屏，点击自动恢复");
        return new("on", "电脑已开机，保持亮屏");
    }
}
