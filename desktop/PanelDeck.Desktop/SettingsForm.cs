using System.Net.NetworkInformation;

namespace PanelDeck;

public sealed class SettingsForm : BorderlessForm
{
    private readonly Controller controller;
    private readonly ComboBox address = new(), fan = new(), panelTheme = new();
    private bool themeEdited, refreshingTheme, saving;
    private readonly NumericUpDown port = Number(1024, 65535), sample = Number(1, 10), sleepingSample = Number(5, 60);
    private readonly CheckBox collector = new() { Text = "复用已安装的后台采集服务", AutoSize = true }, startup = new() { Text = "登录 Windows 后启动", AutoSize = true }, minimized = new() { Text = "启动时只显示托盘图标", AutoSize = true },
        gameFps = new() { Text = "手机显示全屏游戏 FPS", AutoSize = true },
        autoFull = new() { Text = "登录后自动完整采集", AutoSize = true },
        revoke = new() { Text = "撤销所有手机配对，保存后需重新配对", AutoSize = true };
    private readonly Label message = UiTheme.Label("", 9, UiTheme.Muted), pairing = UiTheme.Label("尚未开启配对", 19, UiTheme.Blue, true);
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly Panel pages = new() { Dock = DockStyle.Fill };
    private readonly List<Control> pageControls = new();
    private readonly List<UiButton> navButtons = new();
    private string pairingCode = "";
    public SettingsForm(Controller controller)
    {
        SuspendLayout(); this.controller = controller; var s = controller.Settings;
        Icon = AppIcon.Value; Text = "曜屏 · 设置"; ClientSize = new(950, 770); MinimumSize = new(900, 750); StartPosition = FormStartPosition.CenterParent;
        Font = UiTheme.Font(10); BackColor = UiTheme.Background; ForeColor = UiTheme.Ink; AutoScaleMode = AutoScaleMode.None;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new(0) };
        root.ColumnStyles.Add(new(SizeType.Absolute, 208)); root.ColumnStyles.Add(new(SizeType.Percent, 100)); Controls.Add(root);
        var sidebar = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Sidebar, Margin = new(0) }; root.Controls.Add(sidebar);
        var title = UiTheme.Label("设置", 24, bold: true); title.Location = new(24, 28); sidebar.Controls.Add(title);
        var caption = UiTheme.Label("让曜屏适合你的桌面", 9, UiTheme.Muted); caption.Location = new(24, 78); sidebar.Controls.Add(caption);
        foreach (var name in new[] { "连接与配对", "面板外观", "手机屏幕", "采集与运行", "运行环境" }) {
            int index = navButtons.Count; var nav = UiTheme.Button(name, () => SelectPage(index)); nav.SetBounds(18, 130 + index * 53, 172, 42); navButtons.Add(nav); sidebar.Controls.Add(nav);
        }
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new(28, 25, 28, 18), Margin = new(0) };
        body.RowStyles.Add(new(SizeType.Percent, 100)); body.RowStyles.Add(new(SizeType.Absolute, 52)); body.RowStyles.Add(new(SizeType.Absolute, 44)); root.Controls.Add(body, 1, 0); body.Controls.Add(pages);
        var network = Page("连接与配对", "在同一局域网，把这台电脑连接到手机。");
        Section(network, "手机连接");
        var urls = new TextBox { ReadOnly = true, Multiline = true, Height = 70, ScrollBars = ScrollBars.Vertical, Text = string.Join(Environment.NewLine, controller.Addresses()), BorderStyle = BorderStyle.FixedSingle };
        Row(network, "电脑地址", urls);
        var pair = UiTheme.Button("生成配对码", () => { pairingCode = controller.Pairing.Open(); RefreshPairing(); timer.Start(); }, primary: true); pair.Width = 160; Row(network, "临时配对", pair);
        Row(network, "配对码", pairing);
        Hint(network, "在手机中填写电脑地址和配对码。配对码 5 分钟内有效，仅可使用一次；关闭此窗口后失效。");
        Section(network, "连接选项");
        address.DropDownStyle = ComboBoxStyle.DropDownList; address.Items.Add("0.0.0.0");
        foreach (var ip in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up).SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
            .Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Select(ip => ip.ToString()).Distinct()) address.Items.Add(ip);
        if (!address.Items.Contains(s.ListenAddress)) address.Items.Add(s.ListenAddress); address.SelectedItem = s.ListenAddress; Row(network, "网卡地址", address);
        port.Value = s.Port; Row(network, "端口", port); Hint(network, "默认网卡 0.0.0.0 表示接收所有网卡的局域网连接。更改后先保存，再重新打开设置配对。");
        var setup = UiTheme.Button("允许局域网连接…"); setup.Width = 190;
        setup.Click += async (_, _) => {
            if (MessageBox.Show(this, "此操作将允许手机通过专用局域网连接，仅此次配置需要管理员授权。继续？", "允许局域网连接", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            setup.Enabled = false;
            try { await WindowsSetup.Run((int)port.Value); message.Text = "局域网访问已配置，请保存设置。"; }
            catch (Exception ex) { message.Text = ex.Message; }
            finally { setup.Enabled = true; }
        };
        Row(network, "无法连接时", setup); Row(network, "配对管理", revoke);
        Hint(network, "仅用于可信的家庭或办公室网络，请勿开放到公网。端口改变后需重新允许局域网连接。");
        var appearance = Page("面板外观", "电脑预览与手机面板，共用同一套主题。");
        Section(appearance, "显示主题");
        panelTheme.DropDownStyle = ComboBoxStyle.DropDownList;
        panelTheme.Items.AddRange(PanelThemes.All);
        RefreshThemeSelection(); Row(appearance, "CSS 样式", panelTheme);
        var themeDescription = Hint(appearance, ((PanelThemeOption)panelTheme.SelectedItem!).Description, 11, UiTheme.Ink);
        panelTheme.SelectedIndexChanged += (_, _) => {
            if (!refreshingTheme) themeEdited = true;
            themeDescription.Text = ((PanelThemeOption)panelTheme.SelectedItem!).Description;
        };
        Hint(appearance, "保存后同步到已配对手机；手机暂时离线时，重新连接后应用。在手机端保存主题，也会同步回电脑。");
        Section(appearance, "预览当前选择");
        void Preview(bool landscape) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            controller.PreviewUrl(((PanelThemeOption)panelTheme.SelectedItem!).Id, landscape)) { UseShellExecute = true });
        var portraitPreview = UiTheme.Button("竖屏预览 ↗", () => Preview(false)); portraitPreview.Width = 178;
        var landscapePreview = UiTheme.Button("横屏预览 ↗", () => Preview(true)); landscapePreview.Width = 178;
        Row(appearance, "手机比例", portraitPreview); Row(appearance, "横屏布局", landscapePreview);
        Hint(appearance, "在浏览器内按手机比例显示，使用与 APK 完全相同的 CSS 和实时数据。预览不会修改手机主题，取消设置也不会保存。前五套主题保留竖版布局，后四套可横屏重排。");
        Hint(appearance, "首次连接会沿用手机已保存的主题。两端都升级后支持同步；旧版 APK 仍可显示数据，但无法接收主题设置。");
        var display = Page("手机屏幕", "简单联动，让手机跟随电脑的开关状态。");
        Section(display, "自动亮灭屏");
        Hint(display, "电脑开机且未睡眠 → 手机亮屏\n电脑睡眠、关机或断联 → 手机息屏", 15, UiTheme.Ink);
        Hint(display, "锁屏、静置和游戏均不影响亮屏。电脑恢复运行并重新连接后，手机自动亮屏。连接中断时保留 45 秒缓冲，避免网络抖动导致反复亮灭。");
        Section(display, "在手机上设置");
        Hint(display, "亮度由手机系统自动调节。显示主题可在电脑的“面板外观”或手机设置中选择；息屏方式在手机上设置。");
        Hint(display, "默认息屏会显示黑色遮罩，然后等待系统超时。若要立即熄灭屏幕，可在手机上授权“立即锁屏”。");
        Section(display, "临时息屏"); Hint(display, "主界面可以手动让手机息屏；点击“自动亮灭”即可恢复电脑与手机的联动。");
        var hardware = Page("采集与运行", "调节采样速度，以及曜屏在电脑上的运行方式。");
        Section(hardware, "硬件数据");
        Hint(hardware, "完整采集提供游戏帧率及硬件支持的温度、功耗和风扇。可在下方开启自动采集，或在主界面仅授权本次运行。");
        sample.Value = s.SampleSeconds; sleepingSample.Value = s.SleepSampleSeconds; Row(hardware, "亮屏采样 · 秒", sample); Row(hardware, "息屏采样 · 秒", sleepingSample);
        gameFps.Checked = s.EnableGameFps; Row(hardware, "游戏帧率", gameFps);
        Hint(hardware, "授权后自动跟随前台全屏或无边框全屏程序，无需游戏名单或额外安装。仅手机在线且亮屏时采集。FPS 统计系统显示事件，含可追踪的生成帧；暂不支持时显示 —。帧率本身不需要 PawnIO 驱动。");
        fan.DropDownStyle = ComboBoxStyle.DropDownList; fan.Items.Add(new FanOption("", "自动识别（部分主板为暂定）"));
        foreach (var f in controller.Readings.Where(x => x.Type == "Fan" && !x.Kind.StartsWith("Gpu"))) {
            string hint = f.Id == "/lpc/it8689e/0/fan/0" && controller.Current.Warning?.Contains("CPU_FAN 暂按") == true ? " · 推定 CPU_FAN" : "";
            fan.Items.Add(new FanOption(f.Id, f.Name + " · " + (f.Value.HasValue ? $"{f.Value:0} RPM" : "不可用") + hint));
        }
        if (s.CpuFanId.Length > 0 && !fan.Items.Cast<FanOption>().Any(f => f.Id == s.CpuFanId)) fan.Items.Add(new FanOption(s.CpuFanId, "已选接口（当前不可用）"));
        fan.SelectedIndex = Math.Max(0, fan.Items.Cast<FanOption>().ToList().FindIndex(f => f.Id == s.CpuFanId)); Row(hardware, "FAN 1 接口", fan);
        Hint(hardware, "FAN 1 的显示名称不代表实际主板接口。请按 BIOS 或接线确认，不会根据转速高低猜测。");
        Section(hardware, "启动与后台");
        autoFull.Checked = s.AutoFullCollection; Row(hardware, "完整采集", autoFull);
        Hint(hardware, "首次开启并保存时确认一次 UAC，以后登录自动完整采集。关闭也需确认 UAC。需当前登录账户为 Windows 管理员；不会安装驱动。");
        if (s.AutoFullCollection) Hint(hardware, AutomaticCollector.IsReady() ? "自动采集配置就绪。更新程序后如提示待修复，保留开关并重新保存即可。" : "自动采集配置需要修复：保留此开关并保存，完成一次管理员授权。", 10, UiTheme.Ink);
        startup.Checked = s.StartWithWindows || s.AutoFullCollection; startup.Enabled = !s.AutoFullCollection;
        autoFull.CheckedChanged += (_, _) => { if (autoFull.Checked) startup.Checked = true; startup.Enabled = !autoFull.Checked; };
        minimized.Checked = s.StartMinimized; Row(hardware, "启动方式", startup); Row(hardware, "", minimized);
        Hint(hardware, "关闭主窗口后留在托盘继续运行。需要完整退出时，使用托盘菜单中的“退出曜屏”。");
        Section(hardware, "高级兼容"); collector.Checked = s.UseCollectorService; Row(hardware, "采集服务", collector); Hint(hardware, "仅供已安装旧版采集服务的电脑使用。通常直接使用主界面的本次运行授权即可。数据是否可读仍取决于硬件及驱动支持。");
        var environment = Page("运行环境", "检查曜屏需要的组件，缺少时按需安装。");
        Section(environment, ".NET 10 · x64");
        var runtimeStatus = UiTheme.Label(RuntimeSetupUi.StatusText(), 11, UiTheme.Ink); Row(environment, "本机状态", runtimeStatus);
        var runtimeRefresh = UiTheme.Button("重新检测", () => runtimeStatus.Text = RuntimeSetupUi.StatusText()); runtimeRefresh.Width = 160; Row(environment, "检查组件", runtimeRefresh);
        var runtimeSetup = UiTheme.Button("检测 / 安装…"); runtimeSetup.Width = 180;
        runtimeSetup.Click += async (_, _) => {
            runtimeSetup.Enabled = false;
            try { await RuntimeSetupUi.Open(); }
            catch (Exception ex) { if (!IsDisposed && !Disposing) message.Text = ex.Message; }
            finally { if (!IsDisposed && !Disposing) { runtimeSetup.Enabled = true; runtimeStatus.Text = RuntimeSetupUi.StatusText(); } }
        };
        Row(environment, "安装缺少项", runtimeSetup);
        Hint(environment, "只安装缺少的微软运行时，已有组件会直接复用。点击助手中的安装按钮后，Windows 可能请求管理员授权；不会自动重启电脑。");
        Section(environment, "主程序无法启动时");
        Hint(environment, "请打开发行文件夹中的 PanelDeck.Launcher.exe。它不依赖 .NET 10，会先检查运行环境；组件齐全时直接启动曜屏并退出，不在后台常驻。");
        Hint(environment, "自包含版已附带运行环境，不需要额外安装。精简版需要桌面运行时与 ASP.NET Core 运行时；运行时缺失时无法进入本设置页，因此保留独立启动检查入口。");
        message.AutoSize = false; message.Dock = DockStyle.Fill; message.Padding = new(0, 8, 0, 0); body.Controls.Add(message);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new(0) };
        var save = UiTheme.Button("保存设置", primary: true); var cancel = UiTheme.Button("取消"); cancel.DialogResult = DialogResult.Cancel;
        save.Click += async (_, _) => {
            var next = s.Copy(); next.ListenAddress = address.Text; next.Port = (int)port.Value; next.SampleSeconds = (int)sample.Value; next.SleepSampleSeconds = (int)sleepingSample.Value; next.CpuFanId = (fan.SelectedItem as FanOption)?.Id ?? "";
            if (revoke.Checked) next.Token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            next.UseCollectorService = collector.Checked; next.EnableGameFps = gameFps.Checked; next.AutoFullCollection = autoFull.Checked; next.StartWithWindows = startup.Checked; next.StartMinimized = minimized.Checked;
            saving = true; Enabled = false;
            try { await controller.ApplySettings(next, themeEdited ? ((PanelThemeOption)panelTheme.SelectedItem!).Id : null); saving = false; DialogResult = DialogResult.OK; Close(); }
            catch (Exception ex) { message.Text = "保存失败：" + ex.Message; }
            finally { saving = false; if (!IsDisposed) Enabled = true; }
        };
        actions.Controls.AddRange([save, cancel]); body.Controls.Add(actions); CancelButton = cancel; AcceptButton = save;
        FormClosing += (_, e) => { if (saving) e.Cancel = true; };
        timer.Tick += (_, _) => { RefreshPairing(); RefreshThemeSelection(); }; timer.Start(); SelectPage(0);
        InstallChrome(root, allowMaximize: false, allowMinimize: false);
        UiTheme.ApplyBackground(this);
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96); ResumeLayout(true);
    }
    internal void SelectPage(int index) { for (int i = 0; i < pageControls.Count; i++) { pageControls[i].Visible = i == index; navButtons[i].Selected = i == index; navButtons[i].Invalidate(); } }
    private void RefreshThemeSelection() {
        if (themeEdited || panelTheme.Items.Count == 0) return;
        int index = Math.Max(0, Array.FindIndex(PanelThemes.All, theme => theme.Id == controller.Settings.PanelTheme));
        refreshingTheme = true;
        try { panelTheme.SelectedIndex = index; } finally { refreshingTheme = false; }
    }
    private void RefreshPairing() {
        if (!controller.Pairing.IsActive) { pairing.Text = pairingCode.Length == 0 ? "尚未开启配对" : "配对码已使用或已结束"; return; }
        var left = controller.Pairing.RemainingSeconds; pairing.Text = left > 0 ? $"{pairingCode}   {left / 60}:{left % 60:00}" : "配对码已过期";
    }
    private static NumericUpDown Number(int min, int max) => new() { Minimum = min, Maximum = max, Width = 104, BorderStyle = BorderStyle.FixedSingle };
    private TableLayoutPanel Page(string title, string description) {
        var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = UiTheme.Background, Margin = new(0) }; pages.Controls.Add(page); pageControls.Add(page);
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new(0, 0, 12, 20), BackColor = UiTheme.Background };
        grid.ColumnStyles.Add(new(SizeType.Absolute, 144)); grid.ColumnStyles.Add(new(SizeType.Percent, 100)); page.Controls.Add(grid);
        Hint(grid, title, 23, UiTheme.Ink, true); Hint(grid, description); return grid;
    }
    private static void Section(TableLayoutPanel grid, string title) {
        var label = UiTheme.Label(title, 11, UiTheme.Ink, true); label.Margin = new(0, 18, 0, 13); int row = grid.RowCount++; grid.RowStyles.Add(new(SizeType.AutoSize)); grid.Controls.Add(label, 0, row); grid.SetColumnSpan(label, 2);
    }
    private static void Row(TableLayoutPanel grid, string label, Control input) {
        int row = grid.RowCount++; grid.RowStyles.Add(new(SizeType.AutoSize));
        var caption = UiTheme.Label(label, 9.5f, UiTheme.Muted); caption.Margin = new(0, 8, 12, 14); grid.Controls.Add(caption, 0, row);
        // Preserve the designed input width while an initially hidden settings page lays out.
        // TableLayoutPanel can otherwise shrink fixed-width controls to the first tiny cell.
        input.MinimumSize = input is ComboBox or TextBox ? new Size(220, 0) : new Size(input.Width, 0);
        input.Anchor = AnchorStyles.Left | (input is ComboBox or TextBox ? AnchorStyles.Right : AnchorStyles.None); input.Margin = new(0, 4, 0, 13); grid.Controls.Add(input, 1, row);
        if (input is ComboBox or TextBox or NumericUpDown) { input.ForeColor = UiTheme.Ink; input.BackColor = Color.White; }
        if (input is ComboBox combo) { combo.DrawMode = DrawMode.OwnerDrawFixed; combo.ItemHeight = 26; combo.DrawItem += (_, e) => { e.DrawBackground(); var text = e.Index >= 0 ? combo.Items[e.Index]?.ToString() ?? "" : combo.Text; TextRenderer.DrawText(e.Graphics, text, combo.Font, e.Bounds, e.ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis); e.DrawFocusRectangle(); }; }
        if (input is CheckBox check) { check.MaximumSize = new(430, 0); check.ForeColor = UiTheme.Ink; }
    }
    private static UiLabel Hint(TableLayoutPanel grid, string text, float size = 9, Color? color = null, bool bold = false) {
        int row = grid.RowCount++; grid.RowStyles.Add(new(SizeType.AutoSize)); var hint = UiTheme.Label(text, size, color ?? UiTheme.Muted, bold);
        hint.MaximumSize = new(620, 0); hint.Margin = new(0, 2, 0, 12); grid.Controls.Add(hint, 0, row); grid.SetColumnSpan(hint, 2);
        grid.SizeChanged += (_, _) => hint.MaximumSize = new(Math.Max(180, grid.ClientSize.Width - grid.Padding.Horizontal - 4), 0);
        return hint;
    }
    protected override void Dispose(bool disposing) { var font = Font; if (disposing) { controller.Pairing.Close(); timer.Dispose(); } base.Dispose(disposing); if (disposing) font.Dispose(); }
    private sealed record FanOption(string Id, string Label) { public override string ToString() => Label; }
}
