using PanelDeck;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

if (args.FirstOrDefault() == "--lifecycle-fixture") { await Task.Delay(1200); return; }

if (args.Length >= 2 && args[0] == "--display-probe") {
    await DisplayFrameTests.Probe(args[1], args.Length > 2 ? int.Parse(args[2]) : 30); return;
}

void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
NetworkMonitorTests.Run(Check);
await FrameRateTests.Run(Check);
DisplayFrameTests.Run(Check);
await AutomaticCollectorTests.Run(Check);
// Unique local pipes and synthetic data only. No production service, hardware or UI is used.
var frame = new HardwareFrame(123, "TEST CPU", "TEST GPU", new() { ["cpuTemp"] = new("测试", "C", 42, "synthetic") }, [], null);
var themeWrites = new List<string>();
using (var themeController = new Controller(new Settings(), (_, _) => Task.FromResult(frame), next => themeWrites.Add(next.PanelTheme))) {
    Check(await themeController.ChangePanelTheme("glass", initialize: true) == "glass", "First phone connection adopts its existing theme");
    Check(await themeController.ChangePanelTheme("classic", initialize: true) == "glass" && themeWrites.Count == 1, "Reconnect cannot overwrite saved PC theme or write settings repeatedly");
    Check(await themeController.ChangePanelTheme("studio") == "studio", "Explicit PC/phone theme changes replace canonical theme");
    Check(await themeController.ChangePanelTheme("studio") == "studio" && themeWrites.Count == 2, "Retrying an acknowledged selection is idempotent");
    var restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(themeController.Settings))!;
    Check(restored.PanelTheme == "studio", "Theme survives settings serialization and restart");
}
using (var failingTheme = new Controller(new Settings { PanelTheme = "glass" }, persist: _ => throw new IOException("test storage failure"))) {
    try { await failingTheme.ChangePanelTheme("studio"); throw new Exception("Expected persistence failure"); } catch (IOException) { }
    Check(failingTheme.Settings.PanelTheme == "glass", "Failed persistence does not publish an unsaved theme");
}
async Task<HardwareFrame?> RoundTrip(string payload, bool allowDisconnect = false) {
    string name = "PanelDeck.Test." + Guid.NewGuid().ToString("N");
    using var pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 0, 262144);
    var serving = Task.Run(async () => {
        await pipe.WaitForConnectionAsync();
        try { await pipe.WriteAsync(Encoding.UTF8.GetBytes(payload + "\n")); await pipe.FlushAsync(); }
        catch (IOException) when (allowDisconnect) { }
        pipe.Dispose();
    });
    var result = await CollectorService.Read(CancellationToken.None, name);
    await serving;
    return result;
}
// Runtime detection uses an isolated fake installation, never uninstalls or installs components.
var runtimeFixture = Path.Combine(Path.GetTempPath(), "PanelDeck-RuntimeTest-" + Guid.NewGuid().ToString("N"));
try {
    Directory.CreateDirectory(runtimeFixture);
    var appFixture = Path.Combine(runtimeFixture, "app"); Directory.CreateDirectory(appFixture);
    void Host(ushort machine) {
        using var file = File.Create(Path.Combine(runtimeFixture, "dotnet.exe")); using var writer = new BinaryWriter(file);
        writer.Write((ushort)0x5a4d); file.Position = 0x3c; writer.Write(64); file.Position = 64; writer.Write(0x4550); writer.Write(machine);
    }
    void Framework(string name, string version, string file) {
        var path = Path.Combine(runtimeFixture, "shared", name, version); Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, file), "fixture");
    }
    PanelDeck.RuntimeSetup.RuntimeStatus Inspect() => PanelDeck.RuntimeSetup.RuntimePrerequisites.Inspect(appFixture, runtimeFixture);
    Check(!Inspect().CanLaunch, "No runtime is reported missing without starting an installer");
    Host(0x8664);
    Framework("Microsoft.NETCore.App", "9.0.20", "coreclr.dll");
    Framework("Microsoft.WindowsDesktop.App", "11.0.0", "System.Windows.Forms.dll");
    Framework("Microsoft.AspNetCore.App", "10.0.0-preview.1", "Microsoft.AspNetCore.dll");
    Check(!Inspect().CanLaunch && Inspect().NeedsDesktop && Inspect().NeedsAspNet, "Wrong major versions and prereleases do not satisfy .NET 10");
    Framework("Microsoft.NETCore.App", "10.0.12", "coreclr.dll");
    Framework("Microsoft.WindowsDesktop.App", "10.0.3", "System.Windows.Forms.dll");
    Framework("Microsoft.WindowsDesktop.App", "10.0.12", "System.Windows.Forms.dll");
    Check(!Inspect().CanLaunch && !Inspect().NeedsDesktop && Inspect().NeedsAspNet && Inspect().Desktop == "10.0.12", "Only missing ASP.NET is requested and latest desktop patch is selected");
    Framework("Microsoft.AspNetCore.App", "10.0.12", "Microsoft.AspNetCore.dll");
    Check(Inspect().SharedReady && Inspect().CanLaunch, "Complete x64 runtime installation is ready");
    Host(0x014c);
    Check(!Inspect().CanLaunch, "An x86 dotnet host does not satisfy an x64 app");
    foreach (var name in new[] { "coreclr.dll", "System.Windows.Forms.dll", "Microsoft.AspNetCore.dll" }) File.WriteAllText(Path.Combine(appFixture, name), "fixture");
    Check(Inspect().SelfContained && Inspect().CanLaunch, "Self-contained bundle needs no global runtime");
} finally {
    var resolvedFixture = Path.GetFullPath(runtimeFixture);
    if (Path.GetDirectoryName(resolvedFixture) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) &&
        Path.GetFileName(resolvedFixture).StartsWith("PanelDeck-RuntimeTest-", StringComparison.Ordinal)) Directory.Delete(resolvedFixture, true);
}

for (int i = 0; i < 20; i++) {
    var result = await RoundTrip(JsonSerializer.Serialize(frame));
    if (result?.Metrics["cpuTemp"].Value != 42) throw new Exception("Pipe data lost at iteration " + i);
}
Check(true, "20 pipe connect/write/dispose cycles preserve complete data");
Check(await RoundTrip("{bad") == null, "Malformed collector data rejected");
Check(await RoundTrip("{}") == null, "Incomplete collector data rejected");
var largeFrame = frame with { Warning = new string('测', 20000) };
Check((await RoundTrip(JsonSerializer.Serialize(largeFrame)))?.Warning == largeFrame.Warning, "Pooled pipe buffer grows without losing UTF-8 data");
Check(await RoundTrip(JsonSerializer.Serialize(frame with { Warning = new string('x', 270000) }), true) == null, "Oversized collector frame still respects the 256 KiB limit");
using var timeout = new CancellationTokenSource(200);
Check(await CollectorService.Read(timeout.Token, "PanelDeck.Missing." + Guid.NewGuid()) == null, "Missing service respects cancellation");
var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
using var controller = new Controller(new Settings { ListenAddress = "127.0.0.1", Port = 0 }, async (_, ct) => {
    started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return frame;
});
await controller.Start(); await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
controller.Off(); await Task.Delay(400);
Check(controller.Current.Screen.Mode == "off", "Manual off works while hardware read is blocked");
controller.Auto(); await Task.Delay(400);
Check(controller.Current.Screen.Mode == "on", "Awake PC returns on without waiting for hardware");
controller.PowerChanged(true); await Task.Delay(400);
Check(controller.Current.Screen.Mode == "off" && controller.Current.Pc!.Suspended, "Sleep state is propagated without hardware dependency");
controller.PowerChanged(false); await Task.Delay(400);
Check(controller.Current.Screen.Mode == "on", "Resume restores on without keyboard or mouse input");

// Restoring the dashboard must wake a sampler sleeping for the background interval.
int wakeSamples = 0;
using (var wakeController = new Controller(new Settings { ListenAddress = "127.0.0.1", Port = 0, SleepSampleSeconds = 15 }, (_, _) => {
    Interlocked.Increment(ref wakeSamples); return Task.FromResult(frame with { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
})) {
    await wakeController.Start();
    for (int i = 0; i < 30 && Volatile.Read(ref wakeSamples) == 0; i++) await Task.Delay(100);
    Check(Volatile.Read(ref wakeSamples) == 1, "Hidden dashboard starts with one sample then sleeps");
    await Task.Delay(300);
    Check(wakeController.Current.Metrics.TryGetValue("netDownload", out var download) && download.Unit == "B/s" &&
        wakeController.Current.Metrics.TryGetValue("netUpload", out var upload) && upload.Unit == "B/s",
        "Controller includes network rates independently of hardware collector privileges");
    var unchanged = wakeController.Current; await Task.Delay(600);
    Check(ReferenceEquals(unchanged, wakeController.Current), "Unchanged PC state reuses snapshot without repeated allocation");
    wakeController.DashboardVisible = true;
    for (int i = 0; i < 30 && Volatile.Read(ref wakeSamples) == 1; i++) await Task.Delay(100);
    Check(Volatile.Read(ref wakeSamples) >= 2, "Dashboard restore requests a fresh sample without waiting 15 seconds");
}

// Session authorization lifecycle without requesting elevation or probing sensors.
using (var self = System.Diagnostics.Process.GetCurrentProcess()) {
    Check(SessionCollector.MatchesParent(self, self.StartTime.ToUniversalTime().Ticks, Environment.ProcessPath!), "Session validates parent executable and creation time");
    Check(!SessionCollector.MatchesParent(self, self.StartTime.ToUniversalTime().Ticks + 1, Environment.ProcessPath!), "Reused PID with wrong creation time rejected");
    Check(!SessionCollector.MatchesParent(self, self.StartTime.ToUniversalTime().Ticks, "C:\\wrong.exe"), "Unrelated parent executable rejected");
}
try { SessionCollector.PipeFor("../arbitrary"); throw new Exception("Accepted bad session id"); } catch (ArgumentException) { Check(true, "Session pipe name cannot supply arbitrary paths"); }
var childInfo = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
childInfo.ArgumentList.Add("--lifecycle-fixture");
using (var parent = System.Diagnostics.Process.Start(childInfo)!) {
    bool cancelled = false;
    await SessionCollector.RunUntilParentExits(parent, async ct => {
        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { cancelled = true; }
    }).WaitAsync(TimeSpan.FromSeconds(4));
    Check(cancelled, "Parent exit cancels the session collector without a persistent service");
}
using (var servingStop = new CancellationTokenSource()) {
    string sessionPipe = SessionCollector.PipeFor(Guid.NewGuid().ToString("N"));
    var serving = Task.Run(() => CollectorService.Serve(sessionPipe, servingStop.Token,
        System.Security.Principal.WindowsIdentity.GetCurrent().User, () => frame with { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }));
    for (int i = 0; i < 3; i++) {
        var result = await CollectorService.Read(CancellationToken.None, sessionPipe);
        if (result?.Metrics["cpuTemp"].Value != 42) throw new Exception("Session pipe unavailable");
    }
    Check(true, "Session collector serves repeated samples over its real ACL-protected read-only pipe");
    servingStop.Cancel(); await serving.WaitAsync(TimeSpan.FromSeconds(3));
}
using (var parent = System.Diagnostics.Process.GetCurrentProcess()) {
    bool cancelled = false;
    var lifetimeClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var worker = SessionCollector.RunUntilParentExits(parent, async ct => {
        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { cancelled = true; }
    }, lifetimeClosed.Task);
    lifetimeClosed.SetResult(); await worker.WaitAsync(TimeSpan.FromSeconds(3));
    Check(cancelled && !parent.HasExited, "Replacing a worker cancels its collector without terminating the parent");
}
controller.Dispose(); controller.Dispose();
Check(true, "Controller cleanup is safe when called twice");
Exception? contextFailure = null;
var uiThread = new Thread(() => {
    try {
        // No message loop: the queued startup never runs. No tray icon, singleton
        // command pipe, visible window, configuration write or hardware access.
        using var app = new DesktopApplicationContext(new Settings { ListenAddress = "127.0.0.1", Port = 0 }, background: true, integrateDesktop: false);
        app.Dispose(); app.Dispose();
    } catch (Exception ex) { contextFailure = ex; }
});
uiThread.SetApartmentState(ApartmentState.STA); uiThread.Start(); uiThread.Join();
Check(contextFailure == null, "Lazy application context cleanup tolerates framework and using disposal: " + contextFailure?.Message);
Check(AppIcon.Value.Width > 0, "Generated app icon is embedded and loadable");
