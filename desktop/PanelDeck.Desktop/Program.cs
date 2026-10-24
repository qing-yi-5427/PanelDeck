using Microsoft.Extensions.Hosting;

namespace PanelDeck;

internal static class Program
{
    private static void Main(string[] args)
    {
        // STA initialization loads user32/legacy hooks before Main is entered.
        // Set the per-process policy first, then initialize the UI apartment.
        ProcessPolicy.Initialize();
        if (args.FirstOrDefault() is "--session-collector" or "--basic-collector" or "--scheduled-collector" or "--configure-auto-collector" || args.Contains("--collector") || args.FirstOrDefault() == "--command") {
            Run(args); return;
        }
        var ui = new Thread(() => Run(args));
        ui.SetApartmentState(ApartmentState.STA); ui.Start(); ui.Join();
    }
    private static void Run(string[] args)
    {
        try { MainCore(args); }
        catch (Exception ex) { DiagnosticLog.Write("startup", ex); Environment.ExitCode = 1; }
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void MainCore(string[] args)
    {
        if (args.FirstOrDefault() == "--configure-auto-collector") { AutomaticCollector.ConfigureElevated(args); return; }
        if (args.FirstOrDefault() == "--scheduled-collector") {
            if (args.Length == 5) SessionCollector.Run(["--session-collector", args[2], args[3], args[4], args[1]], scheduled: true).GetAwaiter().GetResult();
            return;
        }
        if (args.FirstOrDefault() is "--session-collector" or "--basic-collector") { SessionCollector.Run(args).GetAwaiter().GetResult(); return; }
        if (args.Contains("--collector")) {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
            builder.Services.AddWindowsService(options => options.ServiceName = "PanelDeckCollector");
            builder.Services.AddHostedService<CollectorService>();
            builder.Build().Run(); return;
        }
        if (args.Length == 2 && args[0] == "--render-ui") {
            ApplicationConfiguration.Initialize();
            DesktopPreview.Render(args[1]);
            return;
        }
        if (args.Length == 2 && args[0] == "--render-runtime") {
            ApplicationConfiguration.Initialize();
            DesktopPreview.RenderRuntime(args[1]);
            return;
        }
        if (args.Length > 0 && args[0] == "--command") { SingleInstance.Send(args.ElementAtOrDefault(1) ?? "show"); return; }
        using var mutex = new Mutex(false, "Local\\PanelDeck.Desktop.SingleInstance");
        try { if (!mutex.WaitOne(TimeSpan.FromSeconds(1))) { if (!args.Contains("--background")) SingleInstance.Send("show"); return; } }
        catch (AbandonedMutexException) { }
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => DiagnosticLog.Write("ui", e.Exception);
        try
        {
            var settings = Settings.Load();
            try { settings.Save(); } catch (Exception ex) { DiagnosticLog.Write("settings-save", ex); settings.LoadNotice = "设置无法保存，请检查当前用户的数据目录权限。"; }
            using var app = new DesktopApplicationContext(settings, args.Contains("--background") || (settings.StartMinimized && !args.Contains("--show")));
            Application.Run(app);
        }
        finally { mutex.ReleaseMutex(); }
    }
}
