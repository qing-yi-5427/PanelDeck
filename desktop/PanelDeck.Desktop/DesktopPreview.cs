namespace PanelDeck;

/// <summary>Native hidden-handle rendering, with the same DPI initialization as the product.</summary>
public static class DesktopPreview
{
    private static int checks;
    private static string output = "";
    private static void Require(bool pass, string reason) { if (!pass) { File.WriteAllText(Path.Combine(output,"runtime-failure.txt"),reason); throw new InvalidOperationException(reason); } checks++; }
    public static void Render(string directory) => RenderRuntime(directory);
    // Explicit visible diagnostic: DrawToBitmap does not include DWM/non-client paint.
    public static void RenderChrome(string directory) {
        output = Path.GetFullPath(directory); Directory.CreateDirectory(output); checks = 0;
        using var controller = new Controller(new Settings());
        using var main = new MainForm(controller, preview: true, visiblePreview: true);
        main.Text = "曜屏 · 窗口边框验证（示例数据）";
        main.ApplySnapshot(Sample(), true, 100, true);
        main.TopMost = true; main.Show(); ShowChromeWindow(main.Handle, 5); main.Activate();
        void CaptureEdge(Form form, string name) {
            Require(form.Visible && IsChromeVisible(form.Handle), "Compositor capture requires a natively visible diagnostic window");
            PositionChromeWindow(form.Handle, new nint(-1), 0, 0, 0, 0, 0x43);
            Application.DoEvents(); form.Update();
            Thread.Sleep(180); // Allow compositor resize/restore transitions to finish.
            Application.DoEvents(); FlushComposition();
            var origin = form.PointToScreen(Point.Empty);
            var covering = ChromeWindowAt(new NativeScreenPoint { X = origin.X + form.Width / 2, Y = origin.Y + form.Height / 2 });
            File.AppendAllText(Path.Combine(output, "windows.txt"), $"{name} hwnd={form.Handle} bounds={form.Bounds} origin={origin} covering={ChromeAncestor(covering, 2)}\n");
            Require(ChromeAncestor(covering, 2) == form.Handle, "Diagnostic window is occluded; cannot validate compositor pixels");
            using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
            using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(form.PointToScreen(Point.Empty), Point.Empty, bitmap.Size);
            bitmap.Save(Path.Combine(output, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            int margin = Math.Max(2, form.Padding.Left / 2);
            foreach (var point in new[] { new Point(bitmap.Width / 2, margin), new Point(bitmap.Width / 2, bitmap.Height - margin - 1),
                new Point(margin, bitmap.Height / 2), new Point(bitmap.Width - margin - 1, bitmap.Height / 2) }) {
                var color = bitmap.GetPixel(point.X, point.Y);
                Require(Math.Abs(color.R - form.BackColor.R) <= 2 && Math.Abs(color.G - form.BackColor.G) <= 2 && Math.Abs(color.B - form.BackColor.B) <= 2,
                    $"System frame overpainted client edge in {name} at {point}: {color}");
            }
        }
        CaptureEdge(main, "chrome-normal");
        foreach (int active in new[] { 0, 1, 0, 1 }) {
            SendChromeMessage(main.Handle, 0x86, active, 0);
            SendChromeMessage(main.Handle, 0x85, 1, 0);
            CaptureEdge(main, "chrome-activation-" + active);
        }
        main.Size = main.MinimumSize; CaptureEdge(main, "chrome-resized");
        main.WindowState = FormWindowState.Maximized; Application.DoEvents();
        Require(main.Bounds == Screen.FromHandle(main.Handle).WorkingArea, "Maximized custom window must fit the working area");
        CaptureEdge(main, "chrome-maximized");
        main.WindowState = FormWindowState.Normal; CaptureEdge(main, "chrome-restored");
        using (var settings = new SettingsForm(controller)) {
            settings.TopMost = true; settings.Show(); ShowChromeWindow(settings.Handle, 5); settings.Activate();
            CaptureEdge(settings, "chrome-settings");
            settings.Close();
        }
        File.WriteAllText(Path.Combine(output, "chrome-checks.txt"), $"PASS {checks} visible DWM / edge / working-area checks at {main.DeviceDpi} DPI. Synthetic data only.\n");
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendChromeMessage(nint window, int message, nint wParam, nint lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "ShowWindow")]
    private static extern bool ShowChromeWindow(nint window, int command);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    private static extern bool IsChromeVisible(nint window);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeScreenPoint { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "WindowFromPoint")]
    private static extern nint ChromeWindowAt(NativeScreenPoint point);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetAncestor")]
    private static extern nint ChromeAncestor(nint window, int flag);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    private static extern bool PositionChromeWindow(nint window, nint after, int x, int y, int width, int height, uint flags);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll", EntryPoint = "DwmFlush")]
    private static extern int FlushComposition();
    public static void RenderRuntime(string directory) {
        checks = 0; directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory); output = directory;
        File.Delete(Path.Combine(output,"runtime-failure.txt"));
        using var controller = new Controller(new Settings());
        using var main = new MainForm(controller, preview: true);
        main.ApplySnapshot(Sample(), true, 100, true);
        Capture(main, Path.Combine(directory, "desktop-runtime.png"));
        Require(Math.Abs(main.ClientSize.Width - 1092 * main.DeviceDpi / 96f) <= 2, "Main content did not scale with its font/DPI");
        Require(Math.Abs(main.ClientSize.Height - 812 * main.DeviceDpi / 96f) <= 2, "Main vertical DPI baseline lost");
        main.Size = main.MinimumSize;
        Capture(main, Path.Combine(directory, "desktop-minimum.png"));
        for (int page = 0; page < 5; page++) {
            using var settings = new SettingsForm(controller); settings.SelectPage(page);
            Capture(settings, Path.Combine(directory, $"settings-runtime-{page}.png"));
            if (page == 3) {
                var automatic = All(settings).OfType<CheckBox>().Single(c => c.Text == "登录后自动完整采集");
                var startup = All(settings).OfType<CheckBox>().Single(c => c.Text == "登录 Windows 后启动");
                automatic.Checked = true;
                Require(startup.Checked && !startup.Enabled, "Automatic collection must keep ordinary logon startup enabled");
                var scroll = All(settings).OfType<Panel>().First(p => p.AutoScroll && p.Contains(automatic));
                scroll.ScrollControlIntoView(automatic);
                Capture(settings, Path.Combine(directory, "settings-automatic-collection.png"));
                automatic.Checked = false;
                Require(startup.Enabled, "Disabling automatic collection restores independent startup selection");
            }
        }
        // Corner and edge hit tests do not move, resize or show any window.
        foreach (var (point, expected) in new (Point,int)[] { (new(0,0),13),(new(99,0),14),(new(0,99),16),(new(99,99),17),(new(0,50),10),(new(99,50),11),(new(50,0),12),(new(50,99),15),(new(50,50),1) })
            Require(BorderlessForm.ResizeHitTest(point,new(100,100),6)==expected,"Window resize edge mismatch");
        File.WriteAllText(Path.Combine(directory,"runtime-checks.txt"),$"PASS {checks} native hidden-window geometry checks; actual DPI {main.DeviceDpi}. No window was shown.\n");
    }
    private static IEnumerable<Control> All(Control c) => new[] { c }.Concat(c.Controls.Cast<Control>().SelectMany(All));
    private static void LayoutAll(Control c) { c.PerformLayout(); foreach(Control child in c.Controls) LayoutAll(child); }
    private static void Capture(Form form, string filename) {
        _ = form.Handle;
        foreach (var control in All(form)) _ = control.Handle;
        form.PerformAutoScale(); LayoutAll(form);
        Require(!form.Visible,"Preview must remain hidden");
        Require(form.FormBorderStyle == FormBorderStyle.None,"System title bar must be absent");
        foreach(var card in All(form).OfType<MetricCard>()) {
            foreach(var label in card.Controls.OfType<UiLabel>()) {
                Require(label.Bottom <= card.ClientSize.Height + 1,"Metric falls outside its card");
                Require(label.GetPreferredSize(new Size(label.Width,0)).Height <= label.Height + 2,$"Metric text clipped vertically: {label.Text}, preferred {label.GetPreferredSize(new Size(label.Width,0))}, actual {label.Size}, font {label.Font.SizeInPoints}, DPI {label.DeviceDpi}");
            }
        }
        foreach(var button in All(form).OfType<UiButton>()) {
            using var font = new Font(button.Font.FontFamily,button.Font.SizeInPoints * button.DeviceDpi / 72f,button.Font.Style,GraphicsUnit.Pixel);
            using var bitmap = new Bitmap(1,1); using var graphics = Graphics.FromImage(bitmap);
            Require(graphics.MeasureString(button.Text,font).Width <= button.ClientSize.Width + 2,"Button text clipped: " + button.Text);
        }
        var entries = All(form).Where(c=>c is Label or Button or ComboBox or NumericUpDown || c==form).Select(c=>new {Type=c.GetType().Name,c.Text,c.DeviceDpi,c.Width,c.Height,c.Left,c.Top,Font=c.Font.SizeInPoints,Family=c.Font.FontFamily.Name});
        File.WriteAllText(Path.ChangeExtension(filename,"json"),System.Text.Json.JsonSerializer.Serialize(entries,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        using var image = new Bitmap(form.ClientSize.Width,form.ClientSize.Height);
        image.SetResolution(form.DeviceDpi,form.DeviceDpi);
        form.DrawToBitmap(image,new Rectangle(Point.Empty,form.ClientSize));
        image.Save(filename,System.Drawing.Imaging.ImageFormat.Png);
    }
    private static Snapshot Sample() {
        var metrics = new Dictionary<string, Metric>();
        void Add(string key, string unit, float value) => metrics[key] = new(key, unit, value, "离屏预览示例数据");
        Add("cpuLoad", "%", 26.4f); Add("cpuTemp", "°C", 54); Add("cpuPower", "W", 64); Add("cpuClock", "MHz", 5125); Add("cpuFan", "RPM", 1260);
        Add("gpuLoad", "%", 72); Add("gpuTemp", "°C", 63); Add("gpuPower", "W", 285); Add("gpuClock", "MHz", 2670); Add("gpuFan", "RPM", 1480);
        Add("ramUsed", "GB", 18.6f); Add("vramUsed", "GB", 11.2f); Add("ramLoad", "%", 29); Add("vramLoad", "%", 35);
        return new(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "示例电脑", new("on", "电脑已开机，保持亮屏"), "AMD Ryzen 7 9800X3D", "NVIDIA GeForce RTX 5090 D", metrics, null, new(false, false));
    }
}
