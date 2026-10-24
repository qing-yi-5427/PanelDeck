using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace PanelDeck;

public sealed class Controller : IDisposable
{
    private volatile Settings settings;
    public Settings Settings => settings;
    public readonly PairingWindow Pairing = new();
    private readonly CancellationTokenSource stop = new();
    private LanServer? server;
    private readonly SessionCollector sessionCollector = new();
    private readonly NetworkMonitor networkMonitor = new();
    private readonly SemaphoreSlim settingsGate = new(1, 1);
    private readonly Action<Settings> persist;
    private int authorizing;
    private int configuringAutomatic;
    private readonly SemaphoreSlim collectorGate = new(1, 1);
    private long nextAutomaticAttempt;
    private readonly Func<bool, Task> configureAutomatic;
    private readonly Func<bool> automaticReady;
    private readonly Action<bool> registerStartup;
    public string? AutomaticCollectorNotice { get; private set; }
    private int disposed;
    public bool AuthorizationPending => Volatile.Read(ref authorizing) != 0;
    public bool SessionAuthorized => sessionCollector.IsAuthorized;
    public bool AutomaticallyAuthorized => sessionCollector.IsAutomatic;
    public async Task AuthorizeHardware() {
        await collectorGate.WaitAsync();
        try {
        if (Interlocked.CompareExchange(ref authorizing, 1, 0) != 0) return;
        try { await Task.Run(sessionCollector.Authorize); AutomaticCollectorNotice = null; RequestSample(); }
        finally { Volatile.Write(ref authorizing, 0); }
        } finally { collectorGate.Release(); }
    }
    private Task? monitorTask, samplingTask;
    private readonly SemaphoreSlim sampleRequested = new(0, 1);
    private readonly Func<Settings, CancellationToken, Task<HardwareFrame>>? sampleOverride;
    private volatile HardwareFrame frame = new(0, "CPU", "GPU", new(), [], null);
    private volatile bool dashboardVisible;
    public bool DashboardVisible {
        get => dashboardVisible;
        set { bool previous = dashboardVisible; dashboardVisible = value; if (value && !previous) RequestSample(); }
    }
    private void RequestSample() { if (Volatile.Read(ref disposed) != 0) return; try { if (sampleRequested.CurrentCount == 0) sampleRequested.Release(); } catch (SemaphoreFullException) { } }
    private volatile bool manualOff, suspended;
    private long phoneSeen = -60000;
    private volatile int phoneBattery = -1;
    private volatile bool phoneCharging;
    private volatile Snapshot snapshot;
    private IReadOnlyList<Reading> readings = Array.Empty<Reading>();
    public Snapshot Current {
        get { var current = snapshot; var theme = settings.PanelTheme; return current.PanelTheme == theme ? current : current with { PanelTheme = theme }; }
    }
    public IReadOnlyList<Reading> Readings => readings;
    public int PhoneBattery => phoneBattery;
    public bool PhoneCharging => phoneCharging;
    public bool PhoneOnline => Environment.TickCount64 - Interlocked.Read(ref phoneSeen) < 25000;
    public string? Error { get; private set; }
    public string CollectorStatus { get; private set; } = "正在检测采集服务";
    public Controller(Settings value, Func<Settings, CancellationToken, Task<HardwareFrame>>? sampleOverride = null, Action<Settings>? persist = null,
        Func<bool, Task>? configureAutomatic = null, Func<bool>? automaticReady = null, Action<bool>? registerStartup = null) {
        settings = value;
        this.persist = persist ?? (next => next.Save());
        this.sampleOverride = sampleOverride;
        this.configureAutomatic = configureAutomatic ?? AutomaticCollector.Configure;
        this.automaticReady = automaticReady ?? AutomaticCollector.IsReady;
        this.registerStartup = registerStartup ?? StartupRegistration.Set;
        snapshot = new(0, Environment.MachineName, new("on", "正在启动"), "CPU", "GPU", new(), null,
            new(false, false), PanelTheme: value.PanelTheme);
    }
    public void Auto() { manualOff = false; }
    public void Off() { manualOff = true; }
    public void PowerChanged(bool sleeping) {
        networkMonitor.Reset();
        suspended = sleeping;
        if (sleeping) snapshot = snapshot with { Screen = new("off", "电脑正在休眠"), Pc = new(true, manualOff) };
    }
    public string[] Addresses() {
        if (Settings.ListenAddress != "0.0.0.0") return [$"http://{Settings.ListenAddress}:{Settings.Port}"];
        return NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(n => n.Address)
            .Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && LanServer.IsLocalNetwork(ip))
            .Select(ip => $"http://{ip}:{Settings.Port}").Distinct().ToArray();
    }
    public string PreviewUrl(string? theme = null, bool landscape = false) =>
        $"http://127.0.0.1:{Settings.Port}" + PanelThemes.PreviewPath(theme, landscape);
    public async Task<string> ChangePanelTheme(string theme, bool initialize = false) {
        if (!PanelThemes.IsKnown(theme)) throw new ArgumentException("未知面板主题。", nameof(theme));
        await settingsGate.WaitAsync();
        try {
            if ((initialize && settings.PanelTheme.Length > 0) || settings.PanelTheme == theme) return settings.PanelTheme;
            var next = settings.Copy(); next.PanelTheme = theme;
            persist(next); settings = next;
            return theme;
        } finally { settingsGate.Release(); }
    }
    private async Task StartServer() {
        var next = new LanServer(Settings.ListenAddress, Settings.Port, () => Current, () => Settings.Token, Pairing,
            (battery, charging) => { phoneBattery = battery; phoneCharging = charging; Interlocked.Exchange(ref phoneSeen, Environment.TickCount64); }, Auto, ChangePanelTheme);
        try { await next.Start(); server = next; Error = null; }
        catch { await next.DisposeAsync(); throw; }
    }
    public async Task ApplySettings(Settings next, string? selectedTheme = null) {
        await settingsGate.WaitAsync();
        Volatile.Write(ref configuringAutomatic, 1);
        await collectorGate.WaitAsync();
        try {
        // A settings window may have been open while the phone changed its theme.
        next.PanelTheme = selectedTheme ?? settings.PanelTheme;
        next.Validate();
        var old = settings;
        bool networkChanged = old.Port != next.Port || old.ListenAddress != next.ListenAddress || server == null;
        if (networkChanged && server != null) { await server.DisposeAsync(); server = null; }
        settings = next;
        try {
            if (networkChanged) await StartServer();
            registerStartup(next.StartWithWindows);
            persist(next);
            // All fallible local operations precede UAC. A cancelled setup restores them.
            // No task is changed until the protected copy is complete and verified.
            if (old.AutoFullCollection != next.AutoFullCollection || (next.AutoFullCollection && !automaticReady()))
                await configureAutomatic(next.AutoFullCollection);
            if (!next.AutoFullCollection) sessionCollector.StopAutomatic();
            AutomaticCollectorNotice = null;
            Interlocked.Exchange(ref nextAutomaticAttempt, 0);
            RequestSample();
        } catch {
            if (networkChanged && server != null) { await server.DisposeAsync(); server = null; }
            settings = old;
            try { registerStartup(old.StartWithWindows); persist(old); } catch (Exception ex) { DiagnosticLog.Write("settings-rollback", ex); }
            if (networkChanged) try { await StartServer(); } catch (Exception ex) { Error = "连接服务未启动：" + ex.Message; }
            throw;
        }
        } finally { Volatile.Write(ref configuringAutomatic, 0); collectorGate.Release(); settingsGate.Release(); }
    }
    public async Task Start() {
        try { await StartServer(); }
        catch (Exception ex) { Error = "连接服务未启动，请检查设置中的地址和端口：" + ex.Message; }
        monitorTask = Task.Run(async () => {
            string lastMode = ""; bool wasOnline = false;
            HardwareFrame? lastFrame = null;
            bool? lastSuspended = null, lastManual = null;
            string? lastTheme = null;
            try {
                while (!stop.IsCancellationRequested) {
                    var reading = frame;
                    bool sleeping = suspended, manual = manualOff, online = PhoneOnline;
                    var theme = settings.PanelTheme;
                    if (!ReferenceEquals(lastFrame, reading) || lastSuspended != sleeping || lastManual != manual || lastTheme != theme) {
                        snapshot = new(reading.Timestamp, Environment.MachineName, ScreenPolicy.Decide(sleeping, manual), reading.Cpu, reading.Gpu, reading.Metrics, reading.Warning,
                            new PcState(sleeping, manual), PanelTheme: theme);
                        lastFrame = reading; lastSuspended = sleeping; lastManual = manual;
                        lastTheme = theme;
                    }
                    if ((lastMode.Length > 0 && lastMode != snapshot.Screen.Mode) || wasOnline != online) RequestSample();
                    wasOnline = online; lastMode = snapshot.Screen.Mode;
                    await Task.Delay(250, stop.Token);
                }
            } catch (OperationCanceledException) { }
            catch (Exception ex) { DiagnosticLog.Write("activity", ex); Error = "电脑活动检测暂不可用，请重新打开曜屏。"; }
        });
        samplingTask = Task.Run(async () => {
            long nextServiceAttempt = 0;
            try {
                while (!stop.IsCancellationRequested) {
                    var config = settings; var now = Environment.TickCount64;
                    try {
                        HardwareFrame? next;
                        if (sampleOverride != null) next = await sampleOverride(config, stop.Token);
                        else {
                            next = null;
                            if (config.AutoFullCollection && !sessionCollector.IsAuthorized && Volatile.Read(ref configuringAutomatic) == 0
                                && now >= Interlocked.Read(ref nextAutomaticAttempt) && Interlocked.CompareExchange(ref authorizing, 1, 0) == 0) {
                                Interlocked.Exchange(ref nextAutomaticAttempt, now + 60000);
                                try {
                                    await collectorGate.WaitAsync(stop.Token);
                                    try { if (settings.AutoFullCollection) { sessionCollector.StartAutomatic(); AutomaticCollectorNotice = null; } }
                                    finally { collectorGate.Release(); }
                                }
                                catch (Exception ex) {
                                    DiagnosticLog.Write("automatic-collector", ex);
                                    AutomaticCollectorNotice = "自动完整采集未启动；请在设置中保留开关并重新保存修复，或使用本次授权。";
                                } finally { Volatile.Write(ref authorizing, 0); }
                            }
                            bool session = sessionCollector.IsRunning && sessionCollector.Pipe != null;
                            if (sessionCollector.IsAuthorized) {
                                next = await CollectorService.Read(stop.Token, sessionCollector.Pipe!);
                                stop.Token.ThrowIfCancellationRequested();
                            } else if (config.UseCollectorService && !AuthorizationPending && now >= nextServiceAttempt) {
                                next = await CollectorService.Read(stop.Token);
                                stop.Token.ThrowIfCancellationRequested();
                                nextServiceAttempt = next == null ? now + 10000 : 0;
                                if (next != null && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - next.Timestamp < 30000) { sessionCollector.StopBasic(); session = false; }
                            }
                            if (next == null && session && sessionCollector.Pipe is string pipe) {
                                next = await CollectorService.Read(stop.Token, pipe);
                                stop.Token.ThrowIfCancellationRequested();
                            }
                            if (next != null && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - next.Timestamp < 30000) {
                                CollectorStatus = session ? sessionCollector.IsAuthorized ? "完整采集 · 本次运行已授权" : "基础采集 · 可点击授权完整读取" : "完整采集服务已连接";
                            } else {
                                // Keep native GPU libraries out of the UI/server process. UAC
                                // replaces this basic worker rather than duplicating its drivers.
                                if (!sessionCollector.IsRunning && !AuthorizationPending) sessionCollector.StartBasic();
                                next = sessionCollector.Pipe == null ? null : await CollectorService.Read(stop.Token, sessionCollector.Pipe);
                                if (next == null || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - next.Timestamp >= 30000) throw new IOException("Collector unavailable");
                                CollectorStatus = sessionCollector.IsAuthorized ? "完整采集 · 本次运行已授权" : "基础采集 · 可点击授权完整读取";
                            }
                        }
                        if (sessionCollector.IsAutomatic) CollectorStatus = "完整采集 · 自动授权";
                        else if (!sessionCollector.IsAuthorized && AutomaticCollectorNotice != null) CollectorStatus += " · 自动授权待修复";
                        if (config.CpuFanId.Length > 0) {
                            var fan = next.Readings.FirstOrDefault(r => r.Type == "Fan" && r.Id == config.CpuFanId);
                            var metrics = new Dictionary<string, Metric>(next.Metrics) { ["cpuFan"] = new("CPU 风扇", "RPM", fan?.Value, fan == null ? null : fan.Hardware + " · " + fan.Name) };
                            next = next with { Metrics = metrics, Warning = fan == null ? "所选 CPU 风扇接口当前不可用。" : next.Warning?.Contains("CPU_FAN 暂按") == true ? null : next.Warning };
                        }
                        var network = networkMonitor.Sample();
                        bool captureFrames = config.EnableGameFps && PhoneOnline && !suspended && !manualOff;
                        var fps = FrameRateMonitor.Missing(!config.EnableGameFps ? "帧率监控已关闭" : !captureFrames ? "手机离线 / 面板息屏" : "请在电脑授权完整读取（UAC）");
                        if (sampleOverride == null && sessionCollector.IsAuthorized && sessionCollector.Pipe is string framesPipe)
                            fps = await FrameRatePipe.Read(framesPipe, captureFrames, stop.Token);
                        var networkMetrics = new Dictionary<string, Metric>(next.Metrics) {
                            ["gameFps"] = new(fps.Label, fps.Unit, fps.Value, fps.Source),
                            ["gameFrameTime"] = new("帧时间", "ms", fps.FrameTimeMs, "约 2 秒平均显示帧间隔"),
                            ["gameFpsLow"] = new("1% Low", "FPS", fps.Low1Percent, fps.Value == null ? fps.Source : fps.Low1Percent == null ? "统计中 · 需连续 30 秒显示帧数据" : "最近 30 秒最慢 1% 显示帧平均耗时换算"),
                            ["netDownload"] = new("下载", "B/s", network.Download, network.Source),
                            ["netUpload"] = new("上传", "B/s", network.Upload, network.Source)
                        };
                        next = next with { Metrics = networkMetrics };
                        frame = next; readings = next.Readings;
                    } catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw; }
                    catch (Exception ex) {
                        DiagnosticLog.Write("sampling", ex);
                        frame = frame with { Warning = "采集暂不可用，正在重试。电脑状态联动仍在运行。" };
                    }
                    int interval = Current.Screen.Mode == "off" || (!PhoneOnline && !DashboardVisible) ? config.SleepSampleSeconds : config.SampleSeconds;
                    await sampleRequested.WaitAsync(TimeSpan.FromSeconds(interval), stop.Token);
                }
            } catch (OperationCanceledException) { }
        });
    }
    public void Export(string file) => File.WriteAllText(file, JsonSerializer.Serialize(new { snapshot, readings }, new JsonSerializerOptions { WriteIndented = true }));
    public void Dispose() {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Pairing.Close(); stop.Cancel();
        try { Task.WaitAll(new[] { monitorTask ?? Task.CompletedTask, samplingTask ?? Task.CompletedTask }, 3000); } catch { }
        if (server != null) try { Task.Run(async () => await server.DisposeAsync()).Wait(4000); } catch { }
        sessionCollector.Dispose();
        stop.Dispose();
    }
}
