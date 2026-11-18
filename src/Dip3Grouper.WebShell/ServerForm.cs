using System.Diagnostics;
using System.Drawing.Drawing2D;
using Dip3Grouper;
using Dip3Grouper.Web;
using Microsoft.Extensions.Hosting;

namespace Dip3GrouperApp.Shell;

/// <summary>
/// 数据服务面板：一屏看完「在跑没有 / 本机地址 / 局域网地址 / 后台地址 / 数据目录 / 退出」。
/// 关窗口只是收进托盘（服务不停），托盘菜单里的「退出」才真正停服务。
/// </summary>
internal sealed class ServerForm : Form
{
    private readonly int _port;
    private readonly bool _jiceng;
    private readonly bool _autoOpen;

    private readonly Dot _dot = new();
    private readonly Label _status = new();
    private readonly Label _statusSub = new();
    private readonly ProgressBar _bar = new();

    private readonly Label _localValue = new();
    private readonly Label _lanValue = new();
    private readonly Label _lanSub = new();
    private readonly Label _adminValue = new();
    private readonly Label _dirValue = new();
    private readonly Label _dirSub = new();

    private readonly NotifyIcon _tray = new();

    private IHost _host;
    private bool _exiting;
    private string _lanUrl = "";

    public ServerForm(int port, bool jiceng, bool autoOpen)
    {
        _port = port;
        _jiceng = jiceng;
        _autoOpen = autoOpen;

        BuildUi();
        BuildTray();
        RefreshAddresses();
    }

    private string LocalUrl => $"http://localhost:{_port}";
    private string AdminUrl => (_lanUrl.Length > 0 ? _lanUrl : LocalUrl) + "/admin";

    // ---------------------------------------------------------------- 布局

    private void BuildUi()
    {
        SuspendLayout();

        Text = "DIP 3.0 分组器 · 数据服务";
        Icon = AppIcon();
        BackColor = Color.White;
        ClientSize = new Size(660, 548);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        // ---- 标题区 ----
        Controls.Add(new PictureBox
        {
            Image = AppIcon().ToBitmap(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point(24, 18),
            Size = new Size(40, 40),
            BackColor = Color.Transparent,
        });

        Controls.Add(new Label
        {
            Text = "DIP 3.0 分组器",
            Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Ui.Text,
            AutoSize = true,
            Location = new Point(76, 18),
            BackColor = Color.Transparent,
        });
        Controls.Add(new Label
        {
            Text = "数据服务 · 科室局域网共享",
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Ui.Muted,
            AutoSize = true,
            Location = new Point(78, 46),
            BackColor = Color.Transparent,
        });

        Controls.Add(new Panel { BackColor = Ui.Line, Location = new Point(0, 78), Size = new Size(660, 1) });

        // ---- 状态卡 ----
        var cardState = new Card { Location = new Point(24, 94), Size = new Size(612, 84) };
        Controls.Add(cardState);

        _dot.Location = new Point(18, 21);
        cardState.Controls.Add(_dot);

        _status.Text = "正在启动服务…";
        _status.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold, GraphicsUnit.Point);
        _status.ForeColor = Ui.Text;
        _status.AutoSize = true;
        _status.Location = new Point(40, 13);
        _status.BackColor = Color.Transparent;
        cardState.Controls.Add(_status);

        _statusSub.Text = "载入分组规则与物价数据";
        _statusSub.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        _statusSub.ForeColor = Ui.Muted;
        _statusSub.AutoSize = true;
        _statusSub.Location = new Point(42, 41);
        _statusSub.BackColor = Color.Transparent;
        cardState.Controls.Add(_statusSub);

        _bar.Location = new Point(18, 66);
        _bar.Size = new Size(576, 4);
        _bar.Style = ProgressBarStyle.Continuous;
        _bar.Value = 4;
        cardState.Controls.Add(_bar);

        // ---- 地址卡：四行，每行「标签 / 值 / 副注 / 按钮」 ----
        var cardAddr = new Card { Location = new Point(24, 188), Size = new Size(612, 244) };
        Controls.Add(cardAddr);

        AddRow(cardAddr, 0, "本机访问", _localValue, LocalUrl, "本机浏览器直接打开", Ui.Accent,
            showOpen: true, open: () => Shell.OpenBrowser(LocalUrl), copy: () => LocalUrl);

        AddRow(cardAddr, 1, "局域网访问", _lanValue, "", "", Ui.Text,
            showOpen: true, open: () => Shell.OpenBrowser(_lanUrl), copy: () => _lanUrl, sub: _lanSub);

        AddRow(cardAddr, 2, "后台管理", _adminValue, "", "录入物价 / 立项指南 / 映射维护", Ui.Text,
            showOpen: true, open: () => Shell.OpenBrowser(AdminUrl), copy: () => AdminUrl);

        AddRow(cardAddr, 3, "数据目录", _dirValue, ShortPath(AppPaths.DataDir), "", Ui.Text,
            showOpen: true, open: () => Shell.OpenFolder(AppPaths.DataDir),
            copy: () => AppPaths.DataDir, sub: _dirSub, mono: true, tip: AppPaths.DataDir);

        // ---- 底部动作 ----
        var refresh = WideButton("刷新地址", 24, 448, 104);
        refresh.Click += (_, __) => { RefreshAddresses(); _statusSub.Text = "已按当前网卡重新获取地址"; };
        Controls.Add(refresh);

        var admin = WideButton("打开管理后台", 140, 448, 144);
        admin.Click += (_, __) => Shell.OpenBrowser(AdminUrl);
        Controls.Add(admin);

        var quit = WideButton("退出服务", 516, 448, 120);
        quit.ForeColor = Ui.Danger;
        quit.FlatAppearance.BorderColor = Color.FromArgb(0xF3, 0xC7, 0xC7);
        quit.Click += (_, __) => AskExit();
        Controls.Add(quit);

        Controls.Add(new Label
        {
            Text = "关闭窗口不会停止服务，程序留在右下角托盘；要停止服务请点「退出服务」。",
            Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Ui.Muted,
            AutoSize = true,
            Location = new Point(24, 488),
            BackColor = Color.Transparent,
        });
        Controls.Add(new Label
        {
            Text = "其他电脑无需安装程序，浏览器打开局域网地址即可；打不开请检查防火墙。",
            Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Ui.Faint,
            AutoSize = true,
            Location = new Point(24, 506),
            BackColor = Color.Transparent,
        });

        ResumeLayout(false);
    }

    /// <summary>
    /// 地址卡的一行：标签(16) + 值(104) + 副注(104) + [打开](474) + [复制](536)。
    /// 值一律 AutoEllipsis —— 数据目录可能有两三级中文路径，宁可省号也不能溢出卡片。
    /// </summary>
    private static void AddRow(Card card, int index, string caption, Label value, string text,
        string subText, Color color, bool showOpen, Action open, Func<string> copy,
        Label sub = null, bool mono = false, string tip = null)
    {
        var top = 10 + index * 56;

        card.Controls.Add(Caption(caption, 16, top + 4));

        value.Text = text;
        value.Font = new Font(mono ? "Consolas" : "Microsoft YaHei UI", mono ? 10F : 11.5F,
            mono ? FontStyle.Regular : FontStyle.Bold, GraphicsUnit.Point);
        value.ForeColor = color;
        value.AutoSize = false;
        value.AutoEllipsis = true;
        value.Location = new Point(104, top + 1);
        value.Size = new Size(360, 22);
        value.TextAlign = ContentAlignment.MiddleLeft;
        value.BackColor = Color.Transparent;
        if (!string.IsNullOrEmpty(tip))
        {
            var tipHolder = new ToolTip();
            tipHolder.SetToolTip(value, tip);
            value.Tag = tipHolder;      // 保住引用，别让 ToolTip 被 GC 掉
        }
        card.Controls.Add(value);

        var subLabel = sub ?? new Label();
        subLabel.Text = subText;
        subLabel.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
        subLabel.ForeColor = Ui.Faint;
        subLabel.AutoSize = false;
        subLabel.AutoEllipsis = true;
        subLabel.Location = new Point(104, top + 25);
        subLabel.Size = new Size(360, 18);
        subLabel.TextAlign = ContentAlignment.MiddleLeft;
        subLabel.BackColor = Color.Transparent;
        card.Controls.Add(subLabel);

        if (showOpen)
        {
            var openBtn = SmallButton("打 开", 474, top + 8, true, 56);
            openBtn.Click += (_, __) => open();
            card.Controls.Add(openBtn);
        }

        if (copy != null)
        {
            var copyBtn = SmallButton("复 制", 536, top + 8, false, 56);
            copyBtn.Click += (_, __) =>
            {
                try
                {
                    var t = copy();
                    if (!string.IsNullOrWhiteSpace(t))
                    {
                        Clipboard.SetText(t);
                        value.Text = t;   // 复制后回填，避免值还在省略号状态看不全
                    }
                }
                catch { }
            };
            card.Controls.Add(copyBtn);
        }
    }

    /// <summary>长路径只显示尾部两级：面板宽度有限，用户认的是「最后那个目录」，不是前面几层。</summary>
    private static string ShortPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length <= 40) return path;
        var parts = path.TrimEnd('\\').Split('\\');
        if (parts.Length <= 2) return path;
        return "…\\" + parts[^2] + "\\" + parts[^1];
    }

    private static Label Caption(string text, int x, int y) => new()
    {
        Text = text,
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
        ForeColor = Ui.Muted,
        AutoSize = false,
        Size = new Size(84, 20),
        TextAlign = ContentAlignment.MiddleLeft,
        Location = new Point(x, y),
        BackColor = Color.Transparent,
    };

    /// <summary>地址随网卡变化重算：插上网线、切 Wi-Fi、开 VPN 之后点「刷新地址」即可。</summary>
    private void RefreshAddresses()
    {
        _localValue.Text = LocalUrl;
        _dirValue.Text = ShortPath(AppPaths.DataDir);
        _dirSub.Text = "数据与备份所在目录";

        var list = Shell.LanEndpoints(_port);
        var best = list.FirstOrDefault();
        if (best == null)
        {
            _lanUrl = "";
            _lanValue.Text = "未接入局域网";
            _lanSub.Text = "本机仍可访问，其他电脑暂时连不上";
            _adminValue.Text = LocalUrl + "/admin";
            return;
        }

        _lanUrl = best.Url;
        _lanValue.Text = best.Url;
        _adminValue.Text = best.Url + "/admin";
        _lanSub.Text = $"{best.Kind} · {best.Adapter}" +
                       (list.Count > 1 ? $" · 另有 {list.Count - 1} 个网卡地址" : "");
    }

    private static Button WideButton(string text, int x, int y, int w)
    {
        var b = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(w, 34),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Ui.Text,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Ui.Line;
        b.FlatAppearance.MouseOverBackColor = Ui.CardHover;
        return b;
    }

    private static Button SmallButton(string text, int x, int y, bool primary, int w)
    {
        var b = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(w, 26),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            BackColor = primary ? Ui.Accent : Color.White,
            ForeColor = primary ? Color.White : Ui.Text,
        };
        b.FlatAppearance.BorderSize = primary ? 0 : 1;
        b.FlatAppearance.BorderColor = Ui.Line;
        if (primary) b.FlatAppearance.MouseOverBackColor = Ui.AccentHover;
        else b.FlatAppearance.MouseOverBackColor = Ui.CardHover;
        return b;
    }

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开面板", null, (_, __) => ShowPanel());
        menu.Items.Add("在浏览器中打开", null, (_, __) => Shell.OpenBrowser(LocalUrl));
        menu.Items.Add("复制局域网地址", null, (_, __) =>
        {
            try { Clipboard.SetText(_lanUrl.Length > 0 ? _lanUrl : LocalUrl); } catch { }
        });
        menu.Items.Add("打开管理后台", null, (_, __) => Shell.OpenBrowser(AdminUrl));
        menu.Items.Add("打开数据目录", null, (_, __) => Shell.OpenFolder(AppPaths.DataDir));
        menu.Items.Add("刷新局域网地址", null, (_, __) => RefreshAddresses());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出（停止服务）", null, (_, __) => AskExit());

        _tray.Icon = AppIcon();
        _tray.Text = "DIP 3.0 分组器 · 数据服务";
        _tray.ContextMenuStrip = menu;
        _tray.Visible = true;
        _tray.DoubleClick += (_, __) => ShowPanel();
    }

    // ---------------------------------------------------------------- 启动

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await StartServerAsync();
    }

    private async Task StartServerAsync()
    {
        SetStatus("正在启动服务…", "载入分组规则与物价数据", Ui.Warn);
        _bar.Visible = true;

        try
        {
            var (host, state) = await Task.Run(() =>
            {
                var st = new AppState();
                st.Init(_jiceng, (pct, msg) => Post(() =>
                {
                    _bar.Value = Math.Clamp(pct, 4, 100);
                    _statusSub.Text = msg;
                }));
                var h = WebServer.Build(_port, st);
                h.Start();
                return (h, st);
            });

            _host = host;
            _bar.Visible = false;
            SetStatus("服务运行中", $"端口 {_port} · 局域网内其他电脑可直接访问", Ui.Ok);
            RefreshAddresses();

            if (_autoOpen) Shell.OpenBrowser(LocalUrl);
            _tray.ShowBalloonTip(2500, "数据服务已启动",
                _lanUrl.Length > 0 ? $"其他电脑访问：{_lanUrl}" : $"本机：{LocalUrl}", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _bar.Visible = false;
            SetStatus("启动失败", "端口可能被占用，或 data / config 目录不完整", Ui.Danger);
            MessageBox.Show(this,
                "数据服务启动失败：\n\n" + ex.Message +
                $"\n\n请确认 {_port} 端口没有被其他程序占用，且本目录下的 data、config、web 三个文件夹完整。",
                "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _tray.Visible = false;
            _exiting = true;
            Application.Exit();
        }
    }

    private void Post(Action a)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(a); } catch { }
    }

    private void SetStatus(string title, string sub, Color color)
    {
        _status.Text = title;
        _statusSub.Text = sub;
        _dot.Color = color;
        _dot.Invalidate();
    }

    private void ShowPanel()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void AskExit()
    {
        var r = MessageBox.Show(this, "退出后将停止数据服务，局域网内其他电脑将无法访问。\n\n确定退出吗？",
            "退出 DIP 3.0 数据服务", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (r == DialogResult.Yes) ExitApp();
    }

    private void ExitApp()
    {
        _exiting = true;
        try { _host?.StopAsync(TimeSpan.FromSeconds(3)).Wait(3000); } catch { }
        _tray.Visible = false;
        _tray.Dispose();
        Close();
        Application.Exit();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 关窗 ≠ 停服务：科室里别人的页面还在用，误关窗口不该把服务带走。
        //
        // 这里**不能**只看 CloseReason.UserClosing：只有"点标题栏 ×"（SC_CLOSE）才会被标成
        // UserClosing；Alt+F4、任务栏右键、外部 WM_CLOSE 会被归到 TaskManagerClosing/None，
        // 只要放行其中一种，Application.Run 就返回、整个进程退出 ——
        // 表现就是用户反馈的"启动后窗口一关就没了"。
        // 所以反过来判：只有本程序主动退出（_exiting）和系统关机才放行。
        // 任务管理器"结束任务"不放行也没关系，它可以直接 TerminateProcess。
        var forced = e.CloseReason == CloseReason.WindowsShutDown;

        if (!_exiting && !forced)
        {
            e.Cancel = true;
            Hide();
            if (_host != null)
                _tray.ShowBalloonTip(2500, "仍在后台运行",
                    "数据服务没有停止。双击右下角图标可重新打开面板，右键可退出。", ToolTipIcon.Info);
            return;
        }
        base.OnFormClosing(e);
    }

    private static Icon AppIcon()
    {
        try
        {
            using var s = typeof(ServerForm).Assembly.GetManifestResourceStream("app.ico");
            if (s != null) return new Icon(s);
        }
        catch { }
        return SystemIcons.Application;
    }

    // ---------------------------------------------------------------- 小控件

    /// <summary>状态圆点（自绘，避免用 12×12 的图片资源）。</summary>
    private sealed class Dot : Control
    {
        private Color _color = Ui.Muted;

        public Dot()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            // 裸 Control 默认不支持透明背景色，直接赋 Color.Transparent 会抛
            // ArgumentException("控件不支持透明背景色")；先打开该样式再设。
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            Size = new Size(14, 14);
            BackColor = Color.Transparent;
        }

        public Color Color
        {
            get => _color;
            set { _color = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(_color);
            e.Graphics.FillEllipse(b, 1, 1, Width - 3, Height - 3);
        }
    }

    /// <summary>浅底 + 1px 边框的直角卡片（扁平风格，不做圆角阴影）。</summary>
    private sealed class Card : Panel
    {
        public Card()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Card;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var p = new Pen(Ui.Line);
            e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }
    }
}

/// <summary>配色：与桌面端一致的白底 + 蓝主色 + 红警示，不用渐变和阴影。</summary>
internal static class Ui
{
    public static readonly Color Text = Color.FromArgb(0x0F, 0x17, 0x2A);
    public static readonly Color Muted = Color.FromArgb(0x64, 0x74, 0x8B);
    public static readonly Color Faint = Color.FromArgb(0x98, 0xA2, 0xB3);
    public static readonly Color Line = Color.FromArgb(0xE3, 0xE8, 0xEF);
    public static readonly Color Card = Color.FromArgb(0xF7, 0xF9, 0xFC);
    public static readonly Color CardHover = Color.FromArgb(0xF1, 0xF5, 0xF9);
    public static readonly Color Accent = Color.FromArgb(0x25, 0x63, 0xEB);
    public static readonly Color AccentHover = Color.FromArgb(0x1D, 0x4E, 0xD8);
    public static readonly Color Ok = Color.FromArgb(0x16, 0xA3, 0x4A);
    public static readonly Color Warn = Color.FromArgb(0xF5, 0x9E, 0x0B);
    public static readonly Color Danger = Color.FromArgb(0xDC, 0x26, 0x26);
}
