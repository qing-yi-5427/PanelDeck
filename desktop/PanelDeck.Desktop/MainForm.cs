using System.Diagnostics;

namespace PanelDeck;

public sealed class MainForm : BorderlessForm
{
    private readonly Controller controller;
    private readonly bool preview;
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 1000 };
    private readonly Label connection = UiTheme.Label("等待手机连接", 11, bold: true), device = UiTheme.Label("同一局域网 · 自动连接", 9, UiTheme.Muted),
        state = UiTheme.Label("电脑醒着，手机就亮着", 10, UiTheme.Muted), warning = UiTheme.Label("", 9, UiTheme.Muted),
        collectorState = UiTheme.Label("基础读取", 9, UiTheme.Muted);
    private readonly UiButton authorize, automatic, off;
    private readonly MetricCard cpu = new("CPU · 处理器", UiTheme.Blue), gpu = new("GPU · 显卡", UiTheme.Purple);
    private readonly Dictionary<string, Label> values = new();
    private readonly ToolTip hints = new();
    private string? authorizationNotice;
    private long authorizationNoticeUntil;
    private bool hidden;
    private Snapshot? displayed;
    private bool displayedOnline, displayedCharging;
    private int displayedBattery = -2;
    public MainForm(Controller controller, bool background = false, bool preview = false)
    {
        SuspendLayout(); this.controller = controller; this.preview = preview; hidden = background;
        VisibleChanged += (_, _) => UpdateVisibility();
        Resize += (_, _) => UpdateVisibility();
        Icon = AppIcon.Value; Text = "曜屏"; ClientSize = new(1080, 760); MinimumSize = new(940, 740);
        StartPosition = FormStartPosition.CenterScreen; BackColor = UiTheme.Background; ForeColor = UiTheme.Ink;
        Font = UiTheme.Font(10); AutoScaleMode = AutoScaleMode.None;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new(0) };
        root.ColumnStyles.Add(new(SizeType.Absolute, 208)); root.ColumnStyles.Add(new(SizeType.Percent, 100)); Controls.Add(root);
        var sidebar = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Sidebar, Margin = new(0), Padding = new(18) };
        var logo = new PictureBox { Image = AppIcon.Value.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Bounds = new(22, 30, 44, 44) }; sidebar.Controls.Add(logo);
        var brand = UiTheme.Label("曜屏", 20, bold: true); brand.Location = new(76, 26); sidebar.Controls.Add(brand);
        var sub = UiTheme.Label("你的桌面，随时可见", 9, UiTheme.Muted); sub.Location = new(23, 88); sidebar.Controls.Add(sub);
        var overview = UiTheme.Button("概览", primary: true); overview.SetBounds(18, 150, 172, 42); sidebar.Controls.Add(overview);
        var settings = UiTheme.Button("设置与配对", OpenSettings); settings.SetBounds(18, 203, 172, 42); sidebar.Controls.Add(settings);
        var previewButton = UiTheme.Button("打开手机面板 ↗", () => Process.Start(new ProcessStartInfo(controller.PreviewUrl()) { UseShellExecute = true }));
        previewButton.SetBounds(18, 256, 172, 42); sidebar.Controls.Add(previewButton);
        var sidebarFoot = UiTheme.Label("局域网连接\n关闭窗口后继续在托盘运行", 9, UiTheme.Muted); sidebarFoot.AutoSize = false; sidebarFoot.Size = new(175, 55);
        sidebarFoot.Location = new(22, ClientSize.Height - 80); sidebar.Controls.Add(sidebarFoot); sidebar.Layout += (_, _) => sidebarFoot.Location = new(sidebar.Padding.Left, sidebar.Height - sidebarFoot.Height - sidebar.Padding.Bottom);
        root.Controls.Add(sidebar, 0, 0);
        var main = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(28, 24, 28, 18), ColumnCount = 1, RowCount = 7, Margin = new(0) };
        foreach (var height in new[] { 64, 86, 240, 124, 68, 48 }) main.RowStyles.Add(new(SizeType.Absolute, height));
        main.RowStyles.Add(new(SizeType.Percent, 100)); root.Controls.Add(main, 1, 0);
        var heading = new Panel { Dock = DockStyle.Fill, Margin = new(0) };
        heading.Controls.Add(UiTheme.Label("桌面概览", 24, bold: true)); var intro = UiTheme.Label("电脑状态与手机屏幕，在这里一目了然。", 9, UiTheme.Muted); intro.Location = new(2, 43); heading.Controls.Add(intro); main.Controls.Add(heading);
        var connectionCard = new UiCard { Dock = DockStyle.Fill, Margin = new(0, 0, 0, 14) };
        connection.Location = new(22, 15); device.Location = new(22, 44); connectionCard.Controls.AddRange([connection, device]);
        var pairButton = UiTheme.Button("连接手机", OpenSettings); pairButton.Size = new(108, 36); pairButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        connectionCard.Controls.Add(pairButton); connectionCard.Layout += (_, _) => pairButton.Location = new(connectionCard.Width - pairButton.Width - connectionCard.Padding.Right, connectionCard.Padding.Top); main.Controls.Add(connectionCard);
        var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new(0) };
        cards.ColumnStyles.Add(new(SizeType.Percent, 50)); cards.ColumnStyles.Add(new(SizeType.Percent, 50)); cpu.Dock = gpu.Dock = DockStyle.Fill; cpu.Margin = new(0, 0, 7, 14); gpu.Margin = new(7, 0, 0, 14); cards.Controls.Add(cpu); cards.Controls.Add(gpu); main.Controls.Add(cards);
        var details = new UiCard { Dock = DockStyle.Fill, Margin = new(0, 0, 0, 14), Padding = new(18, 14, 18, 12) };
        var detailGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2, BackColor = Color.White };
        for (int i = 0; i < 4; i++) detailGrid.ColumnStyles.Add(new(SizeType.Percent, 25));
        detailGrid.RowStyles.Add(new(SizeType.Percent, 58)); detailGrid.RowStyles.Add(new(SizeType.Percent, 42));
        AddValue(detailGrid, "ramUsed", "内存", 0, 0); AddValue(detailGrid, "vramUsed", "显存", 1, 0); AddValue(detailGrid, "cpuFan", "FAN 1", 2, 0); AddValue(detailGrid, "gpuFan", "GPU 风扇", 3, 0);
        AddValue(detailGrid, "cpuClock", "CPU 频率", 0, 1); AddValue(detailGrid, "gpuClock", "GPU 频率", 1, 1); AddValue(detailGrid, "ramLoad", "内存使用率", 2, 1); AddValue(detailGrid, "vramLoad", "显存使用率", 3, 1);
        details.Controls.Add(detailGrid); main.Controls.Add(details);
        var screen = new Panel { Dock = DockStyle.Fill, Margin = new(0) };
        var screenTitle = UiTheme.Label("手机屏幕", 11, bold: true); screenTitle.Location = new(0, 3); state.Location = new(0, 32); screen.Controls.AddRange([screenTitle, state]);
        var controls = new FlowLayoutPanel { Size = new(222, 42), Anchor = AnchorStyles.Top | AnchorStyles.Right, WrapContents = false, Margin = new(0) };
        automatic = UiTheme.Button("自动亮灭", controller.Auto); off = UiTheme.Button("手动息屏", controller.Off); automatic.Width = off.Width = 101;
        controls.Controls.AddRange([automatic, off]); screen.Controls.Add(controls); screen.Layout += (_, _) => controls.Location = new(screen.Width - controls.Width, 3); main.Controls.Add(screen);
        var actions = new Panel { Dock = DockStyle.Fill, Margin = new(0) };
        authorize = UiTheme.Button("授权完整读取（UAC）", primary: true); authorize.Size = new(224, 38); actions.Controls.Add(authorize);
        authorize.Click += async (_, _) => {
            authorize.Enabled = false;
            try { await controller.AuthorizeHardware(); authorizationNotice = "本次运行已授权，正在读取完整硬件数据。"; }
            catch (Exception ex) { authorizationNotice = ex.Message; }
            if (IsDisposed || Disposing) return;
            authorizationNoticeUntil = Environment.TickCount64 + 10000; RefreshState();
        };
        collectorState.Location = new(238, 10); collectorState.AutoSize = false; collectorState.Size = new(240, 28); collectorState.AutoEllipsis = true; actions.Controls.Add(collectorState);
        var export = UiTheme.Button("导出读数", () => { using var dialog = new SaveFileDialog { Filter = "JSON 文件|*.json", FileName = "曜屏硬件读数.json" }; if (dialog.ShowDialog(this) == DialogResult.OK) controller.Export(dialog.FileName); });
        export.Size = new(110, 38); export.Anchor = AnchorStyles.Top | AnchorStyles.Right; actions.Controls.Add(export); actions.Layout += (_, _) => export.Location = new(actions.Width - export.Width, 0); main.Controls.Add(actions);
        var foot = new Panel { Dock = DockStyle.Fill, Margin = new(0) }; warning.AutoSize = false; warning.Dock = DockStyle.Fill; warning.Padding = new(0, 4, 0, 0); foot.Controls.Add(warning); main.Controls.Add(foot);
        if (!preview) {
            refresh.Tick += (_, _) => RefreshState();
        }
        RefreshState();
        InstallChrome(root);
        UiTheme.ApplyBackground(this);
        // Changing AutoScaleMode clears its baseline; assign the 96-DPI design size afterwards.
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96); ResumeLayout(true);
    }
    private void OpenSettings() { using var dialog = new SettingsForm(controller); dialog.ShowDialog(this); }
    private void AddValue(TableLayoutPanel parent, string key, string caption, int column, int row) {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = new(0) };
        var label = UiTheme.Label(caption, 8.5f, UiTheme.Muted); var value = UiTheme.Label("—", row == 0 ? 14 : 10, bold: row == 0); label.Location = new(0, 0); value.Location = new(0, row == 0 ? 21 : 18);
        panel.Controls.AddRange([label, value]); parent.Controls.Add(panel, column, row); values[key] = value;
    }
    private void UpdateVisibility() {
        if (preview) return;
        bool showing = Visible && WindowState != FormWindowState.Minimized;
        controller.DashboardVisible = showing;
        refresh.Enabled = showing;
        if (showing && authorize != null) RefreshState();
    }
    protected override void SetVisibleCore(bool value) { base.SetVisibleCore(value && !hidden && !preview); }
    public void ShowWindow() { hidden = false; Show(); WindowState = FormWindowState.Normal; Activate(); }
    private void RefreshState() {
        if (IsDisposed || Disposing) return;
        if ((!Visible || WindowState == FormWindowState.Minimized) && !preview && IsHandleCreated) return;
        authorize.Enabled = !controller.SessionAuthorized && !controller.AuthorizationPending;
        authorize.Text = controller.AuthorizationPending ? "正在启动完整采集…" : controller.AutomaticallyAuthorized ? "完整采集已自动开启" : controller.SessionAuthorized ? "本次运行已授权" : "授权完整读取（UAC）";
        ApplySnapshot(controller.Current, controller.PhoneOnline, controller.PhoneBattery, controller.PhoneCharging);
        collectorState.Text = controller.CollectorStatus;
        warning.Text = controller.Error ?? (Environment.TickCount64 < authorizationNoticeUntil ? authorizationNotice : null) ?? controller.AutomaticCollectorNotice ?? controller.Settings.LoadNotice ?? controller.Current.Warning ?? "关闭窗口后，曜屏会在托盘继续运行。";
        hints.SetToolTip(warning, warning.Text);
    }
    internal void ApplySnapshot(Snapshot s, bool online, int battery, bool charging) {
        if (ReferenceEquals(displayed, s) && displayedOnline == online && displayedBattery == battery && displayedCharging == charging) return;
        displayed = s; displayedOnline = online; displayedBattery = battery; displayedCharging = charging;
        Metric? M(string key) => s.Metrics.GetValueOrDefault(key);
        cpu.Update(s.Cpu, M("cpuLoad"), M("cpuTemp"), M("cpuPower")); gpu.Update(s.Gpu, M("gpuLoad"), M("gpuTemp"), M("gpuPower"));
        connection.Text = online ? "●  手机已连接" : "○  等待手机连接"; connection.ForeColor = online ? UiTheme.Green : UiTheme.Ink;
        device.Text = online ? $"局域网连接{(battery >= 0 ? $" · 电量 {battery}%" : "")}{(charging ? " · 正在充电" : "")}" : "打开手机设置，填写电脑地址与配对码";
        state.Text = s.Screen.Mode == "off" ? s.Screen.Reason : "电脑醒着，手机就亮着";
        automatic.Selected = s.Pc?.ManualOff != true; off.Selected = s.Pc?.ManualOff == true;
        foreach (var (key, label) in values) { var metric = M(key); label.Text = MetricCard.Format(metric); hints.SetToolTip(label, metric?.Source ?? "暂不可读，可尝试授权完整读取"); }
    }
    private static IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
    protected override void Dispose(bool disposing) { var font = Font; if (disposing) { refresh.Dispose(); hints.Dispose(); if (!preview) controller.DashboardVisible = false; foreach (var picture in Descendants(this).OfType<PictureBox>()) picture.Image?.Dispose(); } base.Dispose(disposing); if (disposing) font.Dispose(); }
}
