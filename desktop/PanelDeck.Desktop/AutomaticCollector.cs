using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32.SafeHandles;

namespace PanelDeck;

// Only this explicit setup entry point elevates. Normal starts use an already approved,
// read/execute-only task in the interactive user's session, never a SYSTEM desktop.
public static class AutomaticCollector
{
    public static string UserSid { get { using var identity = WindowsIdentity.GetCurrent(); return identity.User!.Value; } }
    public static string TaskName(string sid) => "PanelDeck.AutoCollector." + ValidateSid(sid);
    public static string InstallRoot(string sid) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PanelDeck Auto Collector", ValidateSid(sid));
    private static readonly Lazy<string> bundleId = new(() => BundleId(AppContext.BaseDirectory));
    public static string CurrentBundleId => bundleId.Value;
    public static string ValidateSid(string sid) {
        if (!System.Text.RegularExpressions.Regex.IsMatch(sid, @"\AS-1-5-21-\d+-\d+-\d+-\d+\z") || new SecurityIdentifier(sid).Value != sid)
            throw new ArgumentException("需要本机交互用户账户。");
        return sid;
    }
    public static string[] BundleFiles(string source) {
        foreach (string name in new[] { "PanelDeck.exe", "PanelDeck.dll", "PanelDeck.deps.json", "PanelDeck.runtimeconfig.json", "PanelDeck.PresentMon.dll", "LibreHardwareMonitorLib.dll" })
            if (!File.Exists(Path.Combine(source, name))) throw new IOException("请从完整 PC 发行包配置自动采集。");
        return Directory.GetFiles(source).Where(p => Path.GetExtension(p).Equals(".dll", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(p) is "PanelDeck.exe" or "PanelDeck.deps.json" or "PanelDeck.runtimeconfig.json").Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static string BundleId(string source) {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in BundleFiles(source)) {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(path).ToLowerInvariant() + "\0"));
            using var input = File.OpenRead(path); hash.AppendData(SHA256.HashData(input));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    public static string TaskXml(string sid, string executable, string version) {
        ValidateSid(sid);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name, object value) => new(ns + name, value);
        return new XDocument(new XElement(ns + "Task", new XAttribute("version", "1.2"),
            E("RegistrationInfo", new[] { E("Description", "曜屏自动完整采集：由普通权限主程序按需启动，退出主程序时结束。"), E("Version", version) }),
            E("Principals", new XElement(ns + "Principal", new XAttribute("id", "User"), E("UserId", sid), E("LogonType", "InteractiveToken"), E("RunLevel", "HighestAvailable"))),
            E("Settings", new[] { E("MultipleInstancesPolicy", "Parallel"), E("DisallowStartIfOnBatteries", "false"), E("StopIfGoingOnBatteries", "false"),
                E("AllowHardTerminate", "false"), E("StartWhenAvailable", "false"), E("AllowStartOnDemand", "true"), E("Enabled", "true"),
                E("Hidden", "true"), E("ExecutionTimeLimit", "PT0S"), E("Priority", "7") }),
            new XElement(ns + "Actions", new XAttribute("Context", "User"), E("Exec", new[] { E("Command", executable),
                E("Arguments", $"--scheduled-collector {sid} $(Arg0) $(Arg1) $(Arg2)"), E("WorkingDirectory", Path.GetDirectoryName(executable)!) })))).ToString();
    }
    public static bool ValidTask(string xml, string sid, string version, out string executable) {
        executable = "";
        try {
            var root = XDocument.Parse(xml).Root!; XNamespace ns = root.Name.Namespace;
            var action = root.Element(ns + "Actions")!;
            if (action.Elements().Count() != 1 || action.Elements().Single().Name != ns + "Exec") return false;
            var exec = action.Element(ns + "Exec")!;
            executable = exec.Element(ns + "Command")!.Value;
            string directory = Path.GetDirectoryName(Path.GetFullPath(executable))!;
            string leaf = Path.GetFileName(directory);
            var principal = root.Element(ns + "Principals")!.Elements().Single();
            return string.Equals(Path.GetDirectoryName(directory), InstallRoot(sid), StringComparison.OrdinalIgnoreCase)
                && Guid.TryParseExact(leaf, "N", out _) && Path.GetFileName(executable) == "PanelDeck.exe"
                && exec.Element(ns + "WorkingDirectory")?.Value == directory
                && exec.Element(ns + "Arguments")?.Value == $"--scheduled-collector {sid} $(Arg0) $(Arg1) $(Arg2)"
                && principal.Element(ns + "UserId")?.Value == sid && principal.Element(ns + "LogonType")?.Value == "InteractiveToken"
                && principal.Element(ns + "RunLevel")?.Value == "HighestAvailable"
                && root.Element(ns + "RegistrationInfo")?.Element(ns + "Version")?.Value == version
                && root.Element(ns + "Settings")?.Element(ns + "Enabled")?.Value == "true"
                && root.Element(ns + "Settings")?.Element(ns + "AllowStartOnDemand")?.Value == "true";
        } catch { executable = ""; return false; }
    }
    private static T WithTask<T>(Func<dynamic, T> action) {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        object? folder = null, task = null;
        try { service.Connect(); folder = service.GetFolder("\\"); task = ((dynamic)folder).GetTask(TaskName(UserSid)); return action(task); }
        finally { Release(task); Release(folder); Release(service); }
    }
    private static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
    public static bool IsReady() {
        try { return WithTask<bool>(t => ValidTask((string)t.Xml, UserSid, CurrentBundleId, out var exe)
            && BundleId(Path.GetDirectoryName(exe)!) == CurrentBundleId); }
        catch { return false; }
    }
    public static string Start(int pid, long ticks, string nonce) {
        _ = SessionCollector.PipeFor(nonce);
        if (pid <= 0 || ticks <= 0) throw new ArgumentException("无效的主程序身份。");
        return WithTask<string>(t => {
            if (!ValidTask((string)t.Xml, UserSid, CurrentBundleId, out var exe) || BundleId(Path.GetDirectoryName(exe)!) != CurrentBundleId)
                throw new InvalidOperationException("自动采集需修复：请在设置中保留开关并重新保存。");
            object? running = null;
            try { running = t.Run(new string[] { pid.ToString(), ticks.ToString(), nonce }); }
            finally { Release(running); }
            return exe;
        });
    }
    public static async Task Configure(bool enable) {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "--configure-auto-collector", enable ? "enable" : "disable", UserSid }) info.ArgumentList.Add(arg);
        try {
            using var process = Process.Start(info) ?? throw new IOException("无法启动自动采集配置。");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new IOException("自动采集配置失败。请使用当前 Windows 管理员账户和完整发行包重试。");
        } catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) {
            throw new InvalidOperationException("已取消管理员授权，设置保持原状。");
        }
    }
    public static void ConfigureElevated(string[] args) {
        if (args.Length != 3 || args[1] is not ("enable" or "disable")) throw new ArgumentException("无效配置参数。");
        using var identity = WindowsIdentity.GetCurrent();
        string sid = ValidateSid(args[2]);
        // InteractiveToken must belong to the actual signed-in administrator, not another
        // administrator entered into a standard user's credentials dialog.
        if (identity.User!.Value != sid || !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("自动完整采集需要当前登录的 Windows 管理员账户。");
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        object? folder = null, registered = null;
        try {
            service.Connect(); folder = service.GetFolder("\\");
            if (args[1] == "disable") {
                try { ((dynamic)folder).DeleteTask(TaskName(sid), 0); }
                catch (COMException ex) when (ex.HResult == unchecked((int)0x80070002)) { }
                return;
            }
            string source = AppContext.BaseDirectory;
            var files = BundleFiles(source); string version = BundleId(source);
            // Fresh immutable directory: repair/upgrade never overwrites a running DLL.
            string destination = Path.Combine(InstallRoot(sid), Guid.NewGuid().ToString("N"));
            EnsureProtectedDirectory(Path.GetDirectoryName(InstallRoot(sid))!);
            EnsureProtectedDirectory(InstallRoot(sid)); EnsureProtectedDirectory(destination);
            foreach (string file in files) {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("发行文件不能是链接。");
                string copied = Path.Combine(destination, Path.GetFileName(file));
                File.Copy(file, copied, false);
                // Do not rely on copy semantics for ownership or inherited source ACLs.
                var acl = new FileSecurity(); acl.SetAccessRuleProtection(true, false);
                foreach (string owner in new[] { "S-1-5-18", "S-1-5-32-544" })
                    acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(owner), FileSystemRights.FullControl, AccessControlType.Allow));
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
                acl.SetOwner(new SecurityIdentifier("S-1-5-32-544")); new FileInfo(copied).SetAccessControl(acl);
            }
            if (BundleId(destination) != version) throw new IOException("复制期间发行文件发生变化，请重试。");
            // The user may run/read the task, but cannot change its privileged action.
            string sddl = $"O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;{sid})";
            registered = ((dynamic)folder).RegisterTask(TaskName(sid), TaskXml(sid, Path.Combine(destination, "PanelDeck.exe"), version), 6 | 16, sid, null, 3, sddl);
        } finally { Release(registered); Release(folder); Release(service); }
    }
    private static void EnsureProtectedDirectory(string path) {
        for (var parent = new DirectoryInfo(path); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("自动采集安装路径不能包含链接。");
        var directory = Directory.CreateDirectory(path);
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        foreach (string sid in new[] { "S-1-5-18", "S-1-5-32-544" })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"), FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        acl.SetOwner(new SecurityIdentifier("S-1-5-32-544")); directory.SetAccessControl(acl);
    }
    public static string ProcessPath(Process process) {
        using var handle = OpenProcess(0x1000, false, process.Id);
        var text = new StringBuilder(32768); int size = text.Capacity;
        if (handle.IsInvalid || !QueryFullProcessImageName(handle, 0, text, ref size)) throw new IOException("无法核对采集进程。");
        return text.ToString();
    }
    public static uint PipeClient(SafePipeHandle handle) {
        if (!GetNamedPipeClientProcessId(handle, out uint pid)) throw new IOException("无法核对采集管道。");
        return pid;
    }
    public static uint PipeServer(SafePipeHandle handle) {
        if (!GetNamedPipeServerProcessId(handle, out uint pid)) throw new IOException("无法核对主程序管道。");
        return pid;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
