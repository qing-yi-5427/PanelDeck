using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PanelDeck;

// Manual UAC or an explicitly registered task. The network-facing GUI stays unprivileged.
public sealed class SessionCollector : IDisposable
{
    private readonly object sync = new();
    private bool disposed;
    private bool fullStarting;
    private int generation;
    private volatile Process? process;
    private NamedPipeServerStream? lifetime;
    private CancellationTokenSource? lifetimeStop;
    private volatile bool elevated;
    private bool automatic;
    public string? Pipe { get; private set; }
    public bool IsRunning { get { try { return process != null && !process.HasExited; } catch { return false; } } }
    public bool IsAuthorized => elevated && IsRunning;
    public bool IsAutomatic => automatic && IsAuthorized;
    public void StartAutomatic() { if (!IsAuthorized) Start(true, scheduled: true); }
    public void StopAutomatic() { lock (sync) if (automatic) CloseWorker(); }
    public void StartBasic() { if (!IsRunning) Start(false); }
    public void StopBasic() { lock (sync) if (!elevated) CloseWorker(); }
    public void Authorize()
    {
        if (IsAuthorized) return;
        // ETW frame rates need elevation but no PawnIO. HardwareMonitor reports missing sensors.
        Start(true);
    }
    private void Start(bool full, bool scheduled = false)
    {
        int ticket;
        lock (sync) {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (full ? IsAuthorized || fullStarting : IsRunning || fullStarting) return;
            ticket = ++generation; if (full) fullStarting = true;
        }
        try {
        using var parent = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        string nonce = Guid.NewGuid().ToString("N");
        string pipeName = PipeFor(nonce);
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        // A standard user may supply a different administrator's credentials in UAC.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.Read, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        var nextLifetime = NamedPipeServerStreamAcl.Create(pipeName + ".Lifetime", PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        var nextStop = new CancellationTokenSource();
        // No commands are sent. Closing this parent-owned pipe gracefully ends the worker.
        var connected = AcceptLifetime(nextLifetime, nextStop.Token);
        var info = new ProcessStartInfo {
            FileName = Environment.ProcessPath!, UseShellExecute = full,
            Verb = full ? "runas" : "", WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = !full
        };
        foreach (var arg in new[] { full ? "--session-collector" : "--basic-collector", parent.Id.ToString(), parent.StartTime.ToUniversalTime().Ticks.ToString(), nonce, identity.User!.Value }) info.ArgumentList.Add(arg);
        Process? next = null;
        try {
            if (scheduled) {
                string expected = AutomaticCollector.Start(parent.Id, parent.StartTime.ToUniversalTime().Ticks, nonce);
                connected.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
                if (!nextLifetime.IsConnected) throw new IOException("自动采集进程未连接。");
                next = Process.GetProcessById(checked((int)AutomaticCollector.PipeClient(nextLifetime.SafePipeHandle)));
                if (!string.Equals(AutomaticCollector.ProcessPath(next), expected, StringComparison.OrdinalIgnoreCase) || next.SessionId != parent.SessionId)
                    throw new IOException("自动采集进程身份不匹配。");
            } else next = Process.Start(info) ?? throw new InvalidOperationException("无法启动本次采集授权。");
            lock (sync) {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (ticket != generation) { nextStop.Cancel(); nextLifetime.Dispose(); nextStop.Dispose(); next.Dispose(); return; }
                CloseWorker();
                lifetime = nextLifetime; lifetimeStop = nextStop;
                process = next; elevated = full; automatic = scheduled; Pipe = pipeName;
            }
        } catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) {
            nextStop.Cancel(); nextLifetime.Dispose(); nextStop.Dispose();
            throw new InvalidOperationException("已取消授权，继续使用基础采集。");
        } catch {
            nextStop.Cancel(); nextLifetime.Dispose(); nextStop.Dispose(); next?.Dispose(); throw;
        }
        } finally {
            if (full) lock (sync) { if (ticket == generation) fullStarting = false; }
        }
    }
    private static async Task AcceptLifetime(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try { await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
    }
    public static string PipeFor(string nonce)
    {
        if (!Guid.TryParseExact(nonce, "N", out _)) throw new ArgumentException("Invalid session identifier");
        return "PanelDeck.Hardware.Session." + nonce;
    }
    public static bool MatchesParent(Process parent, long startTicks, string expectedPath)
    {
        try {
            return !parent.HasExited && parent.StartTime.ToUniversalTime().Ticks == startTicks
                && string.Equals(parent.MainModule?.FileName, expectedPath, StringComparison.OrdinalIgnoreCase);
        } catch { return false; }
    }
    // Also used by isolated tests with synthetic collectors; no privilege or hardware needed there.
    public static async Task RunUntilParentExits(Process parent, Func<CancellationToken, Task> serve, Task? lifetimeClosed = null)
    {
        using var stop = new CancellationTokenSource();
        var serving = Task.Run(() => serve(stop.Token));
        var parentExit = parent.WaitForExitAsync(stop.Token);
        await Task.WhenAny(serving, parentExit, lifetimeClosed ?? Task.Delay(Timeout.Infinite, stop.Token));
        stop.Cancel();
        try { await serving.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { }
        // Timeout propagates to Main and terminates this helper; never persist after the parent.
    }
    public static async Task Run(string[] args, bool scheduled = false)
    {
        if (args.Length != 5 || !int.TryParse(args[1], out var pid) || !long.TryParse(args[2], out var ticks)) return;
        bool full = args[0] == "--session-collector";
        if (!full && args[0] != "--basic-collector") return;
        using var identity = WindowsIdentity.GetCurrent();
        if (full && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return;
        using var parent = Process.GetProcessById(pid);
        if (scheduled) {
            if (identity.User!.Value != args[4] || parent.HasExited || parent.StartTime.ToUniversalTime().Ticks != ticks
                || parent.SessionId != Process.GetCurrentProcess().SessionId) return;
        } else if (!MatchesParent(parent, ticks, Environment.ProcessPath!)) return;
        var sid = new SecurityIdentifier(args[4]);
        string name = PipeFor(args[3]);
        using var lifetime = new NamedPipeClientStream(".", name + ".Lifetime", PipeDirection.In, PipeOptions.Asynchronous);
        using var connectionTimeout = new CancellationTokenSource(5000);
        await lifetime.ConnectAsync(connectionTimeout.Token);
        if (AutomaticCollector.PipeServer(lifetime.SafePipeHandle) != pid) return;
        await RunUntilParentExits(parent, ct => ServeSession(name, sid, full, ct), lifetime.ReadAsync(new byte[1]).AsTask());
    }
    private static async Task ServeSession(string name, SecurityIdentifier sid, bool full, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var hardware = CollectorService.Serve(name, stop.Token, sid, allowPrivileged: full);
        if (!full) { await hardware; return; }
        var frames = FrameRatePipe.Serve(name, sid, stop.Token);
        await Task.WhenAny(hardware, frames);
        stop.Cancel();
        await Task.WhenAll(hardware, frames);
    }
    private void CloseWorker() {
        lifetimeStop?.Cancel(); lifetime?.Dispose(); lifetimeStop?.Dispose(); process?.Dispose();
        lifetime = null; lifetimeStop = null; process = null; Pipe = null; elevated = false; automatic = false;
    }
    public void Dispose() { lock (sync) { if (disposed) return; disposed = true; CloseWorker(); } }
}
