using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Dip3Grouper;

namespace Dip3GrouperApp.Desktop;

/// <summary>
/// 「物价解读 AI」面板：咨询对话 + 本轮引用 + 本机模型配置。
///
/// <para>回答链路固定为「本地检索 → 带编号证据 → 模型」，模型只拿到用户当轮问题与本机条文，
/// 拿不到整库，也拿不到任何病历数据。回答里的 [G]/[J]/[P] 编号可在「查看引用」逐条核对原文。</para>
///
/// <para>配置（含 API Key）全在 Core 的 <see cref="PriceAiSettingsStore"/>，本文件只负责界面；
/// 会话历史只存在内存里，关掉面板即消失，不落盘。</para>
/// </summary>
public partial class PriceAiPanel : UserControl
{
    /// <summary>✕：收起整个工具栏抽屉（由 ToolPanel 转发给 MainWindow）</summary>
    public event EventHandler CloseRequested;

    /// <summary>‹：回工具栏首页</summary>
    public event EventHandler BackRequested;

    private static readonly string[] Regions =
    {
        "山东省德州市", "山东省", "北京市", "上海市", "广东省", "江苏省",
        "浙江省", "河南省", "河北省", "四川省",
    };

    private AppState _st;
    private bool _ready;
    private bool _filling;              // 填充 ComboBox / 表单时挡住 SelectionChanged 回写
    private bool _busy;
    private PriceAiSettings _cfg;
    private readonly List<PriceAiTurn> _history = new();   // 只保存已完成的成对轮次
    private List<PriceAiEvidence> _evidences = new();
    private PriceAiAsk _lastAsk;
    private string _lastAnswer = "";
    private CancellationTokenSource _cts;

    public PriceAiPanel() : this(null) { }

    public PriceAiPanel(AppState st)
    {
        InitializeComponent();
        _st = st;
        Loaded += (_, _) => Init();
    }

    /// <summary>抽屉里打开时显示「‹ 工具栏」返回键</summary>
    public bool CanGoBack
    {
        get => BtnBack.Visibility == Visibility.Visible;
        set => BtnBack.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================= 初始化 =================

    private void Init()
    {
        if (_ready) return;
        _st ??= App.State;
        _cfg = PriceAiSettingsStore.Current;

        FillProviderCombo();
        FillRegionCombo();
        LoadSettingsToUi();
        RefreshHeader();
        RefreshEvidence();
        Welcome();
        _ready = true;
    }

    private void FillProviderCombo()
    {
        _filling = true;
        CbProvider.ItemsSource = PriceAiSettingsStore.Providers;
        CbProvider.SelectedItem = PriceAiSettingsStore.FindProvider(_cfg.Provider);
        _filling = false;
        SyncProviderHint();
    }

    private void FillRegionCombo()
    {
        _filling = true;
        CbRegion.Items.Clear();
        foreach (var r in Regions) CbRegion.Items.Add(r);
        CbRegion.Items.Add("自定义…");
        CbRegion.SelectedItem = Regions.Contains(_cfg.Region) ? _cfg.Region : "自定义…";
        _filling = false;
    }

    private void SyncProviderHint()
    {
        var p = CbProvider.SelectedItem as PriceAiProvider;
        TxtProviderHint.Text = p == null
            ? ""
            : p.Hint + " 预设只填常用值，最终以服务商文档为准；选完仍可手动改地址和模型名。";
    }

    private void LoadSettingsToUi()
    {
        EdtBaseUrl.Text = _cfg.BaseUrl;
        PwbKey.Password = _cfg.ApiKey;
        EdtModel.Text = _cfg.Model;
        EdtRegion.Text = _cfg.Region;
        EdtTopK.Text = _cfg.TopK.ToString();
        EdtJieduK.Text = _cfg.JieduK.ToString();
        EdtPriceK.Text = _cfg.PriceK.ToString();
        EdtTemp.Text = _cfg.Temperature.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        EdtTimeout.Text = _cfg.TimeoutSec.ToString();
        EdtMaxTokens.Text = _cfg.MaxTokens.ToString();
        TxtSettingsStatus.Text = "";
        SyncProviderHint();
    }

    private void RefreshHeader()
    {
        var ready = PriceAiSettingsStore.IsReady(_cfg);
        TxtSub.Text = ready
            ? $"{_cfg.Region} · 本机立项指南 {_st.Lixiang.State.Sections.Count} 类 · 解读辅导 {_st.Lixiang.State.Jiedu?.List?.Count ?? 0} 条"
            : $"{_cfg.Region} · 尚未配置模型 API";
        TxtModel.Text = ready
            ? $"{_cfg.Model} · 密钥 {PriceAiSettingsStore.MaskKey(_cfg.ApiKey)}"
            : "未配置模型 API（点 ⚙ 填写接口地址 / 密钥 / 模型名）";
        TxtHint.Text = ready
            ? "回答只依据本机检索出的政策条文，结论后可回引 [G]/[J]/[P] 编号；证据不足时模型会明确说明未检索到。Enter 发送，Shift+Enter 换行。"
            : "先在 ⚙ 设置里填好接口地址、密钥与模型名；本机资料库检索不依赖模型，配置前也能点「查看引用」核对条文。Enter 发送，Shift+Enter 换行。";
    }

    // ================= 消息区 =================

    private static void Bind(FrameworkElement obj, DependencyProperty prop, string key)
        => obj.SetResourceReference(prop, key);

    /// <summary>欢迎卡片：只讲清链路与边界，不伪造任何回答。</summary>
    private void Welcome()
    {
        MsgList.Children.Clear();
        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(13, 11, 13, 11),
            Margin = new Thickness(0, 0, 0, 12),
            BorderThickness = new Thickness(1),
        };
        Bind(card, Border.BackgroundProperty, "B.SoftBg");
        Bind(card, Border.BorderBrushProperty, "B.Border");

        var sp = new StackPanel();
        var title = new TextBlock
        {
            Text = "按立项指南解读物价规则", FontSize = 13, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        };
        Bind(title, TextBlock.ForegroundProperty, "B.Text");
        sp.Children.Add(title);

        var body = new TextBlock
        {
            FontSize = 11.5, LineHeight = 19, TextWrapping = TextWrapping.Wrap,
            Text = "① 先从本机立项指南 / 解读辅导 / 地区落地价里检索条文；\n"
                 + "② 只把这些条文连同你的问题发给「你自己配置的模型」；\n"
                 + "③ 回答里的 [G1] [J1] [P1] 是证据编号，点下方「查看引用」可核对原文；\n"
                 + "④ 本地区未收录落地价时，只解读计价规则，不报具体金额。",
        };
        Bind(body, TextBlock.ForegroundProperty, "B.Muted");
        sp.Children.Add(body);
        card.Child = sp;
        MsgList.Children.Add(card);

        if (!PriceAiSettingsStore.IsReady(_cfg))
            AddStatus("当前未配置模型 API。点右上角 ⚙ 填写接口地址、密钥与模型名后即可开始咨询。");
        else
            AddStatus("已就绪。直接输入项目名称或计价疑问，例如「换药项目的基本物耗能不能另收？」");
    }

    private void AddBubble(string role, string content, string caption)
    {
        var user = role == "user";
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(11, 8, 11, 9),
            Margin = new Thickness(user ? 76 : 0, 0, user ? 0 : 76, 10),
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = 650,
            BorderThickness = new Thickness(user ? 0 : 1),
        };
        Bind(border, Border.BackgroundProperty, user ? "B.Accent" : "B.Card");
        if (!user) Bind(border, Border.BorderBrushProperty, "B.Border");

        var sp = new StackPanel();
        if (!string.IsNullOrEmpty(caption))
        {
            var cap = new TextBlock
            {
                Text = caption, FontSize = 10.5, Margin = new Thickness(0, 0, 0, 5),
                TextWrapping = TextWrapping.Wrap, MaxWidth = 600,
            };
            Bind(cap, TextBlock.ForegroundProperty, "B.Faint");
            sp.Children.Add(cap);
        }
        var tb = new TextBlock
        {
            Text = content, FontSize = 12.5, LineHeight = 19, TextWrapping = TextWrapping.Wrap,
            // StackPanel 在测量阶段给子元素无限宽度，Border 的 MaxWidth 截不到 TextBlock 内部，
            // 不显式限宽就会出现「长问题挤成一行、末尾被裁」。650 - 左右 padding ≈ 600。
            MaxWidth = 600,
        };
        Bind(tb, TextBlock.ForegroundProperty, user ? "B.White" : "B.Text");
        sp.Children.Add(tb);
        border.Child = sp;
        MsgList.Children.Add(border);
        MsgScroll.ScrollToEnd();
    }

    private void AddStatus(string text, bool error = false)
    {
        var tb = new TextBlock
        {
            Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 2, 10), LineHeight = 17,
        };
        Bind(tb, TextBlock.ForegroundProperty, error ? "B.Danger" : "B.Faint");
        MsgList.Children.Add(tb);
        MsgScroll.ScrollToEnd();
    }

    // ================= 发送 / 取消 =================

    private void OnQuestionKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            _ = SendAsync();
        }
    }

    private void OnSend(object sender, RoutedEventArgs e) => _ = SendAsync();

    private void OnQuick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string q)
        {
            EdtQuestion.Text = q;
            EdtQuestion.Focus();
            EdtQuestion.CaretIndex = EdtQuestion.Text.Length;
        }
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        try { _cts?.Cancel(); } catch { /* 已结束 */ }
    }

    private void SetBusy(bool busy, string hint = null)
    {
        _busy = busy;
        BtnSend.IsEnabled = !busy;
        BtnStop.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        EdtQuestion.IsEnabled = !busy;
        if (hint != null) TxtHint.Text = hint;
        if (!busy) TxtHint.Text = PriceAiSettingsStore.IsReady(_cfg)
            ? "回答只依据本机检索出的政策条文，结论后可回引 [G]/[J]/[P] 编号。Enter 发送，Shift+Enter 换行。"
            : "先在 ⚙ 设置里填好接口地址、密钥与模型名。Enter 发送，Shift+Enter 换行。";
    }

    private async Task SendAsync()
    {
        if (!_ready) Init();
        if (_busy) return;
        var q = EdtQuestion.Text.Trim();
        if (q.Length == 0) return;

        if (!PriceAiSettingsStore.IsReady(_cfg))
        {
            ShowSettings(true);
            TxtSettingsStatus.Text = "请先填写接口地址与模型名称，保存后即可咨询。";
            return;
        }

        EdtQuestion.Text = "";
        AddBubble("user", q, null);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true, "正在检索本机立项指南与地区落地价…");

        try
        {
            var ask = await Task.Run(() => PriceAiKnowledge.Build(_st, q, _cfg, _history), ct);
            _lastAsk = ask;
            _evidences = ask.Evidences;
            RefreshEvidence();
            AddStatus(ask.Status);

            SetBusy(true, $"已取到 {ask.Evidences.Count} 条证据，正在等待 {_cfg.Model} 回答…");
            var messages = PriceAiKnowledge.Messages(ask, _history, _cfg.HistoryTurns);
            var reply = await PriceAiClient.ChatAsync(_cfg, messages, ct);

            if (!reply.Ok)
            {
                AddStatus("模型调用失败：" + reply.Message, error: true);
                return;
            }

            _lastAnswer = reply.Content;
            var caption = $"依据 {ask.Evidences.Count} 条本机条文 · {_cfg.Model} · {reply.ElapsedMs} ms";
            AddBubble("assistant", reply.Content, caption);
            AddStatus($"输入 {reply.PromptTokens} / 输出 {reply.CompletionTokens} tokens。结论请以「查看引用」里的原文为准。");

            _history.Add(new PriceAiTurn { Role = "user", Content = q });
            _history.Add(new PriceAiTurn { Role = "assistant", Content = reply.Content });
            var keep = Math.Max(0, _cfg.HistoryTurns) * 2;
            if (_history.Count > keep) _history.RemoveRange(0, _history.Count - keep);
        }
        catch (OperationCanceledException)
        {
            AddStatus("已取消本次解读。");
        }
        catch (Exception ex)
        {
            AddStatus("解读过程出错：" + ex.Message, error: true);
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ================= 引用 =================

    private void RefreshEvidence()
    {
        TxtEvidence.Text = _evidences.Count == 0
            ? (_lastAsk == null ? "本轮引用：尚未检索 · 点「查看引用」展开证据区"
                                : _lastAsk.Status)
            : $"{_lastAsk?.Status ?? "本轮引用"} · 共 {_evidences.Count} 条，可逐条核对原文";
        EvidenceList.ItemsSource = _evidences.Select(e => e.OneLine).ToList();
        if (_evidences.Count > 0) EvidenceList.SelectedIndex = 0;
        else EdtEvidenceDetail.Text = "";
    }

    private void ShowEvidence(bool show)
    {
        EvidencePane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        BtnEvidence.Content = show ? "收起引用" : "查看引用";
    }

    private void OnEvidenceToggle(object sender, RoutedEventArgs e)
        => ShowEvidence(EvidencePane.Visibility != Visibility.Visible);

    private void OnEvidenceSel(object sender, SelectionChangedEventArgs e)
    {
        var i = EvidenceList.SelectedIndex;
        EdtEvidenceDetail.Text = i >= 0 && i < _evidences.Count
            ? $"[{_evidences[i].Tag}] {_evidences[i].Source} · {_evidences[i].Title}\n\n{_evidences[i].Text}"
            : "";
    }

    private void OnCopyLast(object sender, RoutedEventArgs e)
    {
        if (_lastAnswer.Length == 0) { AddStatus("还没有可复制的回答。"); return; }
        try { Clipboard.SetText(_lastAnswer); AddStatus("最近一条回答已复制到剪贴板。"); }
        catch (Exception ex) { AddStatus("复制失败：" + ex.Message, error: true); }
    }

    // ================= 设置 =================

    private void ShowSettings(bool show)
    {
        SettingsView.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) { LoadSettingsToUi(); FillProviderCombo(); }
    }

    private void OnSettingsToggle(object sender, RoutedEventArgs e) => ShowSettings(true);

    private void OnSettingsClose(object sender, RoutedEventArgs e) => ShowSettings(false);

    private void OnProviderPick(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || !_ready) return;
        if (CbProvider.SelectedItem is not PriceAiProvider p) return;
        SyncProviderHint();

        // 只替换「还是预设值 / 还空着」的字段，不覆盖用户手改过的内容
        var prev = PriceAiSettingsStore.FindProvider(_cfg.Provider);
        if (string.IsNullOrWhiteSpace(EdtBaseUrl.Text) || EdtBaseUrl.Text.Trim() == prev.BaseUrl)
            EdtBaseUrl.Text = p.BaseUrl;
        if (string.IsNullOrWhiteSpace(EdtModel.Text) || EdtModel.Text.Trim() == prev.Model)
            EdtModel.Text = p.Model;
    }

    private void OnRegionPick(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || !_ready) return;
        var v = CbRegion.SelectedItem as string ?? "";
        if (v == "自定义…") { ShowSettings(true); EdtRegion.Focus(); return; }
        var next = _cfg.Clone();
        next.Region = v;
        PriceAiSettingsStore.Save(next);
        _cfg = PriceAiSettingsStore.Current;
        RefreshHeader();
        AddStatus($"解读地区已切换为 {v}。{(PriceAiKnowledge.LocalPriceAvailable(v) ? "本地区落地价已纳入检索。" : "本地区落地价未收录，本轮只解读计价规则、不报金额。")}");
    }

    private static int IntOf(TextBox tb, int fallback)
        => int.TryParse(tb.Text.Trim(), out var v) ? v : fallback;

    private PriceAiSettings ReadSettingsFromUi()
    {
        var next = _cfg.Clone();
        next.Provider = (CbProvider.SelectedItem as PriceAiProvider)?.Id ?? "custom";
        next.BaseUrl = EdtBaseUrl.Text.Trim();
        next.ApiKey = PwbKey.Password.Trim();
        next.Model = EdtModel.Text.Trim();
        next.Region = EdtRegion.Text.Trim();
        next.TopK = IntOf(EdtTopK, next.TopK);
        next.JieduK = IntOf(EdtJieduK, next.JieduK);
        next.PriceK = IntOf(EdtPriceK, next.PriceK);
        next.MaxTokens = IntOf(EdtMaxTokens, next.MaxTokens);
        next.TimeoutSec = IntOf(EdtTimeout, next.TimeoutSec);
        if (double.TryParse(EdtTemp.Text.Trim(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var t)) next.Temperature = t;
        return next;
    }

    private void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        var next = ReadSettingsFromUi();
        var check = PriceAiSettingsStore.Validate(next);
        if (!check.Ok) { TxtSettingsStatus.Text = check.Message; return; }

        PriceAiSettingsStore.Save(next);
        _cfg = PriceAiSettingsStore.Current;
        FillRegionCombo();
        RefreshHeader();
        LoadSettingsToUi();
        TxtSettingsStatus.Text = $"已保存到本机 · {_cfg.Region} · {_cfg.Model} · 密钥 {PriceAiSettingsStore.MaskKey(_cfg.ApiKey)}";
        AddStatus($"配置已更新：{_cfg.Region} / {_cfg.Model}。{(PriceAiKnowledge.LocalPriceAvailable(_cfg.Region) ? "本地区落地价参与检索。" : "本地区落地价未收录，只解读计价规则。")}");
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        if (!_ready) Init();
        if (!PriceAiSettingsStore.IsReady(_cfg))
        {
            ShowSettings(true);
            TxtSettingsStatus.Text = "请先填写接口地址与模型名称。";
            return;
        }
        TxtHint.Text = "正在测试连接…";
        var r = await PriceAiClient.TestAsync(_cfg);
        TxtHint.Text = r.Message;
        if (!r.Ok) AddStatus("连接测试失败：" + r.Message, error: true);
        else AddStatus("连接测试通过：" + r.Message);
    }

    private async void OnTestSettings(object sender, RoutedEventArgs e)
    {
        var next = ReadSettingsFromUi();
        var check = PriceAiSettingsStore.Validate(next);
        if (!check.Ok) { TxtSettingsStatus.Text = check.Message; return; }
        TxtSettingsStatus.Text = "正在用当前填写的值测试连接…";
        var r = await PriceAiClient.TestAsync(next);
        TxtSettingsStatus.Text = r.Message;
    }

    private void OnNewChat(object sender, RoutedEventArgs e)
    {
        _history.Clear();
        _evidences = new List<PriceAiEvidence>();
        _lastAsk = null;
        _lastAnswer = "";
        RefreshEvidence();
        ShowEvidence(false);
        Welcome();
    }

    // ================= 事件 / 键盘 / 截图 =================

    private void OnBack(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);
    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    public void FocusSearch()
    {
        if (!_ready) Init();
        if (SettingsView.Visibility == Visibility.Visible) EdtBaseUrl.Focus();
        else EdtQuestion.Focus();
    }

    /// <summary>Esc 先关设置面板，再交回 ToolPanel 收起抽屉；Ctrl+F 定位输入框</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SettingsView.Visibility == Visibility.Visible)
        {
            ShowSettings(false);
            return true;
        }
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            FocusSearch();
            return true;
        }
        return false;
    }

    /// <summary>
    /// 离屏截图入口：<c>priceai</c> 欢迎态、<c>priceai:set</c> 设置面板、
    /// <c>priceai:ask:关键词</c> 跑一次**真实的本地检索**（不调用模型），把证据与气泡一起渲染出来。
    /// </summary>
    internal void Diag(string tab, string query)
    {
        Init();
        if (tab == "set") { ShowSettings(true); return; }
        if (string.IsNullOrWhiteSpace(query)) return;

        var ask = PriceAiKnowledge.Build(_st, query, _cfg);
        _lastAsk = ask;
        _evidences = ask.Evidences;
        RefreshEvidence();
        ShowEvidence(true);
        AddBubble("user", query, null);
        AddBubble("assistant",
            $"【诊断模式】本轮从本机资料库检索到 {ask.Evidences.Count} 条证据（指南 {ask.GuideCount} / 解读 {ask.JieduCount} / 落地价 {ask.PriceCount}），"
            + "未向模型发起网络请求。可在下方「查看引用」逐条核对条文原文。",
            ask.Status);
        EdtQuestion.Text = "";
    }
}
