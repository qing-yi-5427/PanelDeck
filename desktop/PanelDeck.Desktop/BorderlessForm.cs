using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PanelDeck;

/// <summary>
/// Native borderless shell. Call InstallChrome once after constructing the content;
/// the derived form owns DPI initialization and all close/tray behavior.
/// </summary>
public class BorderlessForm : Form
{
    private const int WmNcCalcSize = 0x0083, WmNcHitTest = 0x0084, WmGetMinMaxInfo = 0x0024;
    private const int WmNcPaint = 0x0085, WmNcActivate = 0x0086;
    private const int WmNcLeftButtonDown = 0x00A1, HtCaption = 2;
    private const int WsThickFrame = 0x00040000, WsSysMenu = 0x00080000, WsMinimizeBox = 0x00020000, WsMaximizeBox = 0x00010000;
    private const int DesignBarHeight = 40, DesignBorder = 6;
    private TableLayoutPanel? frame;
    private Panel? titleBar;
    private CaptionTitle? caption;
    private CaptionButton? minimize, maximize, close;
    private bool canMaximize = true, canMinimize = true;

    public BorderlessForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    /// <summary>
    /// Adds a 40px title bar and a 6px resize edge at the 96-DPI design baseline.
    /// Existing content dimensions are preserved. Call before final autoscaling.
    /// </summary>
    protected void InstallChrome(Control content, bool allowMaximize = true, bool allowMinimize = true)
    {
        if (frame != null) throw new InvalidOperationException("Window chrome is already installed.");
        if (content.Parent != this) throw new ArgumentException("Content must be a direct child of this form.", nameof(content));
        canMaximize = allowMaximize; canMinimize = allowMinimize;
        MaximizeBox = canMaximize; MinimizeBox = canMinimize;
        var previousClient = ClientSize; var previousMinimum = MinimumSize;
        SuspendLayout();
        Controls.Remove(content);
        Padding = new(DesignBorder);
        frame = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = BackColor, Margin = new(0), Padding = new(0), TabIndex = 0 };
        frame.ColumnStyles.Add(new(SizeType.Percent, 100));
        frame.RowStyles.Add(new(SizeType.Absolute, DesignBarHeight));
        frame.RowStyles.Add(new(SizeType.Percent, 100));
        titleBar = new Panel { Dock = DockStyle.Fill, Margin = new(0), Padding = new(0), BackColor = BackColor, TabIndex = 1, AccessibleName = "窗口标题栏" };
        caption = new CaptionTitle { Text = Text, Font = Font, BackColor = BackColor, ForeColor = Color.FromArgb(89, 100, 118), TabStop = false, AccessibleRole = AccessibleRole.StaticText };
        minimize = new(CaptionKind.Minimize) { AccessibleName = "最小化", TabIndex = 0, Visible = canMinimize };
        maximize = new(CaptionKind.Maximize) { AccessibleName = "最大化", TabIndex = 1, Visible = canMaximize };
        close = new(CaptionKind.Close) { AccessibleName = "关闭窗口", TabIndex = 2 };
        minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        maximize.Click += (_, _) => ToggleMaximize();
        close.Click += (_, _) => Close(); // Keeps the derived form's FormClosing / tray contract.
        titleBar.Controls.AddRange([caption, minimize, maximize, close]);
        titleBar.MouseDown += DragTitleBar; caption.MouseDown += DragTitleBar;
        titleBar.MouseDoubleClick += DoubleClickTitleBar; caption.MouseDoubleClick += DoubleClickTitleBar;
        titleBar.Layout += (_, _) => LayoutCaption();
        content.Dock = DockStyle.Fill; content.Margin = new(0); content.TabIndex = 0;
        frame.Controls.Add(titleBar, 0, 0); frame.Controls.Add(content, 0, 1); Controls.Add(frame);
        ClientSize = new(previousClient.Width + DesignBorder * 2, previousClient.Height + DesignBarHeight + DesignBorder * 2);
        if (!previousMinimum.IsEmpty) MinimumSize = new(previousMinimum.Width + DesignBorder * 2, previousMinimum.Height + DesignBarHeight + DesignBorder * 2);
        ResumeLayout(true);
    }

    private void LayoutCaption()
    {
        if (titleBar == null || caption == null || minimize == null || maximize == null || close == null) return;
        int buttonWidth = Math.Max(32, (int)Math.Round(titleBar.Height * 1.15));
        int right = titleBar.ClientSize.Width;
        void Place(CaptionButton button, bool visible) {
            button.Visible = visible;
            if (!visible) return;
            right -= buttonWidth; button.SetBounds(right, 0, buttonWidth, titleBar.Height);
        }
        Place(close, true); Place(maximize, canMaximize); Place(minimize, canMinimize);
        int inset = Math.Max(8, titleBar.Height / 3);
        caption.SetBounds(inset, 0, Math.Max(0, right - inset * 2), titleBar.Height);
    }

    private void DragTitleBar(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || e.Clicks > 1) return;
        ReleaseCapture();
        SendMessage(Handle, WmNcLeftButtonDown, new nint(HtCaption), nint.Zero);
    }

    private void DoubleClickTitleBar(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) ToggleMaximize();
    }

    private void ToggleMaximize()
    {
        if (!canMaximize) return;
        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); if (caption != null) caption.Text = Text; }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); if (caption != null) caption.Font = Font; }
    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        if (frame != null) frame.BackColor = BackColor;
        if (titleBar != null) titleBar.BackColor = BackColor;
        if (caption != null) caption.BackColor = BackColor;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (maximize != null) {
            maximize.Restore = WindowState == FormWindowState.Maximized;
            maximize.AccessibleName = maximize.Restore ? "还原窗口" : "最大化";
            maximize.Invalidate();
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Supported on Windows 11; older DWM versions simply return an error.
        int preference = 2;
        try {
            DwmSetWindowAttribute(Handle, 33, ref preference, sizeof(int));
            // DWMWA_BORDER_COLOR / DWMWA_COLOR_NONE: keep DWM corners and shadow,
            // but do not add a system-colored outline around our client-drawn shell.
            int borderColor = unchecked((int)0xfffffffe);
            DwmSetWindowAttribute(Handle, 34, ref borderColor, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    protected override CreateParams CreateParams
    {
        get {
            var cp = base.CreateParams;
            cp.Style |= WsThickFrame | WsSysMenu;
            if (canMinimize) cp.Style |= WsMinimizeBox; else cp.Style &= ~WsMinimizeBox;
            if (canMaximize) cp.Style |= WsMaximizeBox; else cp.Style &= ~WsMaximizeBox;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        // Retain the resizable window style and system keyboard behavior while
        // taking responsibility for the entire non-client appearance.
        if (m.Msg == WmNcCalcSize) { m.Result = nint.Zero; return; }
        if (m.Msg == WmNcPaint) { m.Result = nint.Zero; return; }
        if (m.Msg == WmNcActivate) {
            // WS_THICKFRAME is retained only for resize/snap behavior. Let Windows
            // update activation without painting its classic frame over the client edge.
            m.LParam = new nint(-1);
            base.WndProc(ref m);
            Invalidate();
            return;
        }
        if (m.Msg == WmGetMinMaxInfo) {
            base.WndProc(ref m);
            ApplyWorkingArea(m.LParam);
            m.Result = nint.Zero; return;
        }
        if (m.Msg == WmNcHitTest && WindowState == FormWindowState.Normal) {
            long packed = m.LParam.ToInt64();
            var client = PointToClient(new Point(unchecked((short)(packed & 0xffff)), unchecked((short)((packed >> 16) & 0xffff))));
            int edge = Math.Max(DesignBorder, Padding.Left);
            int hit = ResizeHitTest(client, ClientSize, edge);
            if (hit != 1) { m.Result = new nint(hit); return; }
        }
        base.WndProc(ref m);
    }

    private void ApplyWorkingArea(nint pointer)
    {
        if (pointer == nint.Zero) return;
        nint monitor = MonitorFromWindow(Handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var limits = Marshal.PtrToStructure<MinMaxInfo>(pointer);
        limits.MaxPosition.X = info.Work.Left - info.Monitor.Left;
        limits.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
        limits.MaxSize.X = info.Work.Right - info.Work.Left;
        limits.MaxSize.Y = info.Work.Bottom - info.Work.Top;
        if (MinimumSize.Width > 0) limits.MinTrackSize.X = MinimumSize.Width;
        if (MinimumSize.Height > 0) limits.MinTrackSize.Y = MinimumSize.Height;
        Marshal.StructureToPtr(limits, pointer, false);
    }

    internal static int ResizeHitTest(Point p, Size client, int edge)
    {
        if (p.X < 0 || p.Y < 0 || p.X >= client.Width || p.Y >= client.Height) return 1;
        bool left = p.X < edge, right = p.X >= client.Width - edge, top = p.Y < edge, bottom = p.Y >= client.Height - edge;
        if (top) return left ? 13 : right ? 14 : 12;
        if (bottom) return left ? 16 : right ? 17 : 15;
        return left ? 10 : right ? 11 : 1;
    }

    private sealed class CaptionTitle : Control
    {
        public CaptionTitle() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        protected override void OnPaint(PaintEventArgs e) {
            // Geometry is already DPI-scaled by the form; derive the caption font
            // from the bar height so detached previews follow the same proportions.
            using var font = new Font(Font.FontFamily, Math.Max(10, Height * 0.30f), FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(ForeColor);
            using var format = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            e.Graphics.DrawString(Text, font, brush, ClientRectangle, format);
        }
    }

    private enum CaptionKind { Minimize, Maximize, Close }
    private sealed class CaptionButton : Button
    {
        private readonly CaptionKind kind;
        private bool hover;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool Restore { get; set; }
        public CaptionButton(CaptionKind kind) {
            this.kind = kind;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; TabStop = true; AccessibleRole = AccessibleRole.PushButton;
            Margin = new(0); Cursor = Cursors.Hand;
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e) {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = hover ? kind == CaptionKind.Close ? Color.FromArgb(225, 72, 76) : Color.FromArgb(226, 231, 239) : Parent?.BackColor ?? SystemColors.Control;
            e.Graphics.Clear(fill);
            float scale = Height / (float)DesignBarHeight, size = 10 * scale;
            float x = (Width - size) / 2, y = (Height - size) / 2;
            using var pen = new Pen(hover && kind == CaptionKind.Close ? Color.White : Color.FromArgb(80, 92, 111), Math.Max(1, scale));
            if (kind == CaptionKind.Minimize) e.Graphics.DrawLine(pen, x, y + size / 2, x + size, y + size / 2);
            else if (kind == CaptionKind.Close) { e.Graphics.DrawLine(pen, x, y, x + size, y + size); e.Graphics.DrawLine(pen, x + size, y, x, y + size); }
            else if (Restore) {
                float offset = 3 * scale;
                e.Graphics.DrawLines(pen, [new PointF(x + offset, y + offset), new(x + offset, y), new(x + size, y), new(x + size, y + size - offset), new(x + size - offset, y + size - offset)]);
                e.Graphics.DrawRectangle(pen, x, y + offset, size - offset, size - offset);
            } else e.Graphics.DrawRectangle(pen, x, y, size, size);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -(int)(5 * scale), -(int)(5 * scale)));
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, int message, nint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
