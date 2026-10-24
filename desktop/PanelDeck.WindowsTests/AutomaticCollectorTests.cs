using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using PanelDeck;

internal static class AutomaticCollectorTests
{
    public static async Task Run(Action<bool, string> check) {
        var legacy = JsonSerializer.Deserialize<Settings>("{\"StartWithWindows\":true}")!;
        check(!legacy.AutoFullCollection, "Old settings never silently grant persistent elevation");
        var enabled = new Settings { AutoFullCollection = true }; enabled.Validate();
        check(enabled.StartWithWindows && JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(enabled))!.AutoFullCollection,
            "Automatic collection implies normal-user logon startup and survives serialization");

        string sid = AutomaticCollector.UserSid, version = new string('A', 64);
        string exe = Path.Combine(AutomaticCollector.InstallRoot(sid), Guid.NewGuid().ToString("N"), "PanelDeck.exe");
        string xml = AutomaticCollector.TaskXml(sid, exe, version);
        check(AutomaticCollector.ValidTask(xml, sid, version, out var path) && path == exe, "Approved task must target protected per-user collector copy");
        check(!AutomaticCollector.ValidTask(xml, sid, new string('B', 64), out _), "Old collector version cannot silently run after an upgrade");
        foreach (var pair in new[] { ("HighestAvailable", "LeastPrivilege"), ("InteractiveToken", "ServiceAccount"),
            ("--scheduled-collector", "--collector"), ("<Enabled>true", "<Enabled>false"), ("<AllowStartOnDemand>true", "<AllowStartOnDemand>false") })
            check(!AutomaticCollector.ValidTask(xml.Replace(pair.Item1, pair.Item2), sid, version, out _), "Reject altered task: " + pair.Item1);
        check(!AutomaticCollector.ValidTask(AutomaticCollector.TaskXml(sid, Path.Combine(Path.GetTempPath(), "PanelDeck.exe"), version), sid, version, out _),
            "Reject user-writable executable location");
        var doc = XDocument.Parse(xml); XNamespace ns = doc.Root!.Name.Namespace;
        var actions = doc.Root.Element(ns + "Actions")!; actions.Add(new XElement(actions.Elements().Single()));
        check(!AutomaticCollector.ValidTask(doc.ToString(), sid, version, out _), "Reject tasks with extra executable actions");
        check(!AutomaticCollector.ValidTask("<invalid", sid, version, out _), "Malformed task is unavailable, never trusted");
        foreach (string bad in new[] { "S-1-5-18", "../task", "S-1-5-21-1-2-3-4 & cmd" }) {
            bool rejected = false; try { AutomaticCollector.TaskName(bad); } catch (ArgumentException) { rejected = true; }
            check(rejected, "Reject non-user or injected task identity: " + bad);
        }
        // Windows validates the real schema without registering, running, or elevating a task.
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        object? folder = null;
        try {
            scheduler.Connect(); folder = scheduler.GetFolder("\\");
            ((dynamic)folder).RegisterTask("PanelDeck.ValidationOnly", xml, 1, sid, null, 3, null);
            check(true, "Windows Task Scheduler validates highest interactive task XML without installation");
        } finally { if (folder != null) Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(scheduler); }

        var frame = new HardwareFrame(1, "TEST", "TEST", new(), [], null);
        Settings MakeSettings(bool automatic = false) {
            var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start();
            int port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
            return new Settings { ListenAddress = "127.0.0.1", Port = port, AutoFullCollection = automatic, StartWithWindows = automatic };
        }
        var before = MakeSettings(); Settings saved = before.Copy(); bool startup = false; var requests = new List<bool>(); bool ready = false;
        using (var controller = new Controller(before, (_, _) => Task.FromResult(frame), s => saved = s.Copy(),
            v => { requests.Add(v); ready = v; return Task.CompletedTask; }, () => ready, v => startup = v)) {
            var next = before.Copy(); next.AutoFullCollection = true; await controller.ApplySettings(next);
            check(saved.AutoFullCollection && startup && requests.SequenceEqual(new[] { true }), "Enable persists settings and requests exactly one explicit setup");
            await controller.ApplySettings(controller.Settings.Copy());
            check(requests.Count == 1, "Unrelated settings saves do not repeat UAC when registration is current");
            ready = false; await controller.ApplySettings(controller.Settings.Copy());
            check(requests.Count == 2 && requests[1], "Saving enabled setting repairs missing/outdated task explicitly");
            next = controller.Settings.Copy(); next.AutoFullCollection = false; await controller.ApplySettings(next);
            check(!saved.AutoFullCollection && startup && requests.Last() == false, "Disable removes approval while preserving ordinary startup preference");
        }
        foreach (bool initial in new[] { false, true }) {
            before = MakeSettings(initial); saved = before.Copy(); startup = before.StartWithWindows;
            using var controller = new Controller(before, (_, _) => Task.FromResult(frame), s => saved = s.Copy(),
                _ => throw new OperationCanceledException("synthetic UAC cancellation"), () => true, v => startup = v);
            var next = before.Copy(); next.AutoFullCollection = !initial; next.StartWithWindows = !initial;
            try { await controller.ApplySettings(next); throw new Exception("Expected cancelled setup"); } catch (OperationCanceledException) { }
            check(controller.Settings.AutoFullCollection == initial && saved.AutoFullCollection == initial && startup == before.StartWithWindows,
                "Cancelled " + (initial ? "disable" : "enable") + " restores runtime settings, persisted settings and startup");
        }
        int setups = 0;
        before = MakeSettings();
        using (var controller = new Controller(before, (_, _) => Task.FromResult(frame), _ => throw new IOException("synthetic disk failure"),
            _ => { setups++; return Task.CompletedTask; }, () => false, _ => { })) {
            var next = before.Copy(); next.AutoFullCollection = true;
            try { await controller.ApplySettings(next); throw new Exception("Expected failed save"); } catch (IOException) { }
            check(setups == 0 && !controller.Settings.AutoFullCollection, "Failed local save never installs a privileged task");
        }
        // Content changes, not just assembly version labels, require re-approval.
        string fixture = Path.Combine(Path.GetTempPath(), "PanelDeck-AutoTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try {
            foreach (string name in new[] { "PanelDeck.exe", "PanelDeck.dll", "PanelDeck.deps.json", "PanelDeck.runtimeconfig.json", "PanelDeck.PresentMon.dll", "LibreHardwareMonitorLib.dll" })
                File.WriteAllText(Path.Combine(fixture, name), "synthetic");
            string hash = AutomaticCollector.BundleId(fixture);
            File.WriteAllText(Path.Combine(fixture, "settings.json"), "private settings excluded");
            check(hash == AutomaticCollector.BundleId(fixture), "Protected collector bundle excludes user settings and credentials");
            File.AppendAllText(Path.Combine(fixture, "PanelDeck.PresentMon.dll"), "changed");
            check(hash != AutomaticCollector.BundleId(fixture), "Native dependency changes invalidate automatic collector version");
        } finally {
            if (Path.GetDirectoryName(Path.GetFullPath(fixture)) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                && Path.GetFileName(fixture).StartsWith("PanelDeck-AutoTests-", StringComparison.Ordinal)) Directory.Delete(fixture, true);
        }
    }
}
