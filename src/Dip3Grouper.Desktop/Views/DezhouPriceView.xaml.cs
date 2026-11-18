using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Dip3Grouper;

namespace Dip3GrouperApp.Desktop.Views;

/// <summary>德州物价表格的一行。</summary>
public class DzRowVm : INotifyPropertyChanged
{
    public string Seq { get; set; }
    public string Code { get; set; }
    public string Name { get; set; }
    public string Category { get; set; }
    public string Unit { get; set; }
    public string Price3 { get; set; }
    public string Price2 { get; set; }
    public string Price1 { get; set; }
    /// <summary>扣减基本物耗价格（仅 25 号文「一般治疗」类 11 条有值，其余为空）。</summary>
    public string Deduct { get; set; }
    /// <summary>价格悬停说明：加收项给出「主项 × 比例 → 金额（原表公式）」；普通项即价格本身。</summary>
    public string Price3Tip { get; set; }
    public string Price2Tip { get; set; }
    public string Price1Tip { get; set; }
    public string Insurance { get; set; }
    public string SelfPay { get; set; }
    public string Doc { get; set; }
    /// <summary>短文号（只留〔年份〕序号，省下「德医保发」四字给名称列）；完整文号见 <see cref="DocTip"/></summary>
    public string DocShort { get; set; }
    public string DocTip { get; set; }
    public string Kind { get; set; }

    private bool _fav;

    /// <summary>是否已加入「常用收藏」（点星标即时切换，需要通知以便星标即时变色）</summary>
    public bool IsFav
    {
        get => _fav;
        set
        {
            if (_fav == value) return;
            _fav = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFav)));
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>收藏态变化后的回调（页面据此刷新「仅看收藏(N)」并在需要时重载列表）</summary>
    public Action<DzRowVm, bool> FavToggled { get; set; }

    private List<LxBlock> _blocks;

    /// <summary>详情块懒构建（只在行选中、详情展开时才算跨库关联）</summary>
    public Func<List<LxBlock>> BlocksFactory { get; set; }
    public List<LxBlock> Blocks => _blocks ??= BlocksFactory?.Invoke() ?? new List<LxBlock>();

    public string KindChip => Kind == "add" ? "加收项" : Kind == "ext" ? "扩展项" : "";
    public bool HasKindChip => Kind is "add" or "ext";
    public string PriceLine => $"{Nz(Price3)} / {Nz(Price2)} / {Nz(Price1)}";
    private static string Nz(string v) => string.IsNullOrWhiteSpace(v) ? "—" : v;
}

/// <summary>类别「使用说明」面板的一行（Group 非空时在正文上方带类别小标题）。</summary>
public class UsageVm
{
    public string No { get; set; }
    public string Group { get; set; }
    public string Text { get; set; }
    public bool HasGroup => !string.IsNullOrEmpty(Group);
}

/// <summary>可点击的「现行项目」链接（旧版物价现行标准 / 作废对照新编码列共用）。</summary>
public class DzLinkVm
{
    /// <summary>现行项目编码（点击后按此检索跳转）</summary>
    public string Code { get; set; }
    /// <summary>展示文本（编码 名称）</summary>
    public string Text { get; set; }
}

/// <summary>「作废对照」视图的一行：作废的原物价项目 → 对应新立项指南项目。</summary>
public class DzCancelRowVm
{
    public string Doc { get; set; }
    public string Exec { get; set; }
    public string Category { get; set; }
    public string OldCode { get; set; }
    public string OldName { get; set; }
    public string NewCode { get; set; }
    public string NewName { get; set; }
    public string Note { get; set; }

    /// <summary>批次展示（文号 · 执行日期），一列承载免得表格过宽</summary>
    public string Batch => string.IsNullOrEmpty(Exec) ? Doc : $"{Doc} · {Exec}";
    /// <summary>单元格悬停：文号与执行日期分开写，方便核对作废生效日</summary>
    public string BatchTip => string.IsNullOrEmpty(Exec) ? Doc : $"{Doc}　执行日期 {Exec}";
    /// <summary>新项目侧展示：无替代时给出明确说法而不是空白</summary>
    public string NewCodeText => string.IsNullOrEmpty(NewCode) ? "—" : NewCode;
    public string NewNameText => string.IsNullOrEmpty(NewName) ? "（直接废止，无替代项目）" : NewName;
    /// <summary>新编码是否可点击跳转（存在对应现行项目才给链接）</summary>
    public bool HasNewLink => !string.IsNullOrEmpty(NewCode);
    /// <summary>单元素链接列表（模板列 ItemsSource 复用旧版视图的链接模板）</summary>
    public List<DzLinkVm> NewLinks => string.IsNullOrEmpty(NewCode)
        ? new List<DzLinkVm>()
        : new List<DzLinkVm> { new() { Code = NewCode, Text = NewCode } };
    /// <summary>绑定占位（作废对照无「直接废止」占位文本，恒空 = 模板里该行始终折叠）</summary>
    public string NoStdText => "";
    /// <summary>划线保留（不作废）：通知废止表里该行被划横线，项目仍在执行</summary>
    public bool IsKeep { get; set; }
    /// <summary>「作废」/「保留」</summary>
    public string State => IsKeep ? "保留" : "作废";
    /// <summary>状态列悬停说明：划线保留的行照旧列在废止表上，但原文划线即不作废</summary>
    public string StateTip => IsKeep
        ? "通知附件「废止项目表」中该行划横线 = 不作废，项目继续执行（映射关系仅作对照）"
        : "通知确认作废，请按对应新项目停用";
}

/// <summary>「旧版物价」视图的一行：45号文旧价格项目，联查作废对照标出状态与现行标准。</summary>
public class DzLegacyRowVm
{
    public string Code { get; set; }
    public string Name { get; set; }
    public string Category { get; set; }
    public string Unit { get; set; }
    public string Price3 { get; set; }
    public string Price2 { get; set; }
    public string Price1 { get; set; }
    public string Insurance { get; set; }
    public string SelfPay { get; set; }
    /// <summary>「已作废」/「现行」</summary>
    public string Status { get; set; }
    /// <summary>状态着色用：true = 已作废（红）</summary>
    public bool IsDead { get; set; }
    /// <summary>现行标准全文（悬停提示用；未作废为空）</summary>
    public string CurrentStd { get; set; }
    /// <summary>现行标准明细：每个对应的新项目一条，可点击跳转到现行项目视图</summary>
    public List<DzLinkVm> Links { get; set; } = new();
    /// <summary>已作废但无替代项目（给明确说法而不是空白）</summary>
    public bool DeadNoStd { get; set; }
    /// <summary>直接废止时的占位说明（可点击链接为空时显示）</summary>
    public string NoStdText => DeadNoStd ? "（直接废止，无替代项目）" : "";
    /// <summary>本次新公布项目标记（「新」字角标，空 = 无）</summary>
    public string NewMark { get; set; }
}

/// <summary>
/// 「新旧比对」视图的一行：现行项目 ↔ 一个旧版物价项目（经作废对照反查旧码）。
/// 差额 / 涨幅按三级价计算；着色走 IsUp/IsDown 触发器（涨红跌绿，中国行情惯例）。
/// </summary>
public class DzCompareRowVm : INotifyPropertyChanged
{
    public string NewCode { get; set; }
    public string NewName { get; set; }
    public string Category { get; set; }
    /// <summary>main / add / ext</summary>
    public string Kind { get; set; }
    /// <summary>三档价格都留着：级别下拉切档时只换展示，不重建数据</summary>
    public string NewPrice3 { get; set; }
    public string NewPrice2 { get; set; }
    public string NewPrice1 { get; set; }
    public string OldCode { get; set; }
    public string OldName { get; set; }
    public string OldPrice3 { get; set; }
    public string OldPrice2 { get; set; }
    public string OldPrice1 { get; set; }
    /// <summary>三级价差额（新 − 旧）；任一侧解析不出数字时为 null（显示「—」）</summary>
    public double? Delta3 { get; set; }
    /// <summary>按三级价算的涨幅（%）；旧价缺失或为 0 时不计算</summary>
    public double? Ratio3 { get; set; }
    public double? Delta2 { get; set; }
    public double? Ratio2 { get; set; }
    public double? Delta1 { get; set; }
    public double? Ratio1 { get; set; }
    /// <summary>legacy = 旧版物价命中（可比价）；cancel = 仅作废对照（无旧价）；none = 纯新增</summary>
    public string Source { get; set; } = "none";

    /// <summary>
    /// 当前档位："3" 三级 / "2" 二级 / "1" 一级。由视图在换档时回填到本页每一行。
    /// <para>为什么放在 VM 上而不是重建 ItemsSource：换档只影响 4 个单元格的取数，
    /// 重建 50 行 VM 等于把分页、选中、滚动位置全部推倒重来，切一次级别像换了次视图。</para>
    /// </summary>
    public string Level { get; set; } = "3";

    /// <summary>立项指南里能查到该项目（决定「立项指南」链接可点还是置灰）</summary>
    public bool HasGuide { get; set; }
    /// <summary>立项指南条目摘要（类别 / 计价单位），悬停提示用</summary>
    public string GuideTip { get; set; } = "";
    /// <summary>链接文字：「立项指南」/「无立项指南」，后者置灰但保留位置，列不会忽宽忽窄</summary>
    public string GuideText => HasGuide ? "立项指南" : "无立项指南";

    public string KindChip => Kind == "add" ? "加收" : Kind == "ext" ? "扩展" : "";
    public bool HasKindChip => Kind is "add" or "ext";
    public string SourceText => Source switch { "legacy" => "旧版物价", "cancel" => "仅作废对照", _ => "纯新增" };

    // ---- 按当前档位取数（表格绑定的都是这几个）----

    private string NewPriceCur => Level switch { "2" => NewPrice2, "1" => NewPrice1, _ => NewPrice3 };
    private string OldPriceCur => Level switch { "2" => OldPrice2, "1" => OldPrice1, _ => OldPrice3 };
    private double? DeltaCur => Level switch { "2" => Delta2, "1" => Delta1, _ => Delta3 };
    private double? RatioCur => Level switch { "2" => Ratio2, "1" => Ratio1, _ => Ratio3 };

    /// <summary>价格列表头随档位变化：「新二级价」「旧一级价」…</summary>
    public string NewPriceHeader => Level switch { "2" => "新二级价", "1" => "新一级价", _ => "新三级价" };
    public string OldPriceHeader => Level switch { "2" => "旧二级价", "1" => "旧一级价", _ => "旧三级价" };

    public string DeltaText => DeltaCur is null ? "—" : (DeltaCur > 0 ? "+" : "") + DeltaCur.Value.ToString("0.##");
    public string RatioText => RatioCur is null ? "—" : (RatioCur > 0 ? "+" : "") + RatioCur.Value.ToString("0.#") + "%";
    public string NewPrice3Text => Blank(NewPriceCur) ? "—" : NewPriceCur;
    public string OldPrice3Text => Blank(OldPriceCur) ? "—" : OldPriceCur;
    public string OldNameText => Blank(OldName) ? "—" : OldName;

    /// <summary>两侧编码（链接列显示用）：纯新增 / 无可比旧项目时给「—」，同时置灰不可点。</summary>
    public string NewCodeText => Blank(NewCode) ? "—" : NewCode;
    public string OldCodeText => Blank(OldCode) ? "—" : OldCode;
    public bool HasNewCode => !Blank(NewCode);
    public bool HasOldCode => !Blank(OldCode);

    /// <summary>
    /// 空值判定：除空白外，源表里的占位符 <c>\</c>、<c>-</c> 也算空。
    /// 作废对照表用「\」表示"本项目无旧编码"，直接绑到列上会显示成反斜杠，
    /// 与旁边的「—」混在一起像是两套符号（2026-10-30 统一）。
    /// </summary>
    private static bool Blank(string s)
        => string.IsNullOrWhiteSpace(s) || s.Trim() is "\\" or "-" or "—";

    /// <summary>着色键：up=涨价红 / down=降价绿 / 空=持平或不可比</summary>
    public string Trend => DeltaCur is null ? "" : DeltaCur > 0 ? "up" : DeltaCur < 0 ? "down" : "flat";
    public bool IsUp => Trend == "up";
    public bool IsDown => Trend == "down";
    public bool HasDelta => DeltaCur != null;

    /// <summary>换档后刷新全部派生属性（表格绑定的是 *Text / IsUp 等，必须逐个通知）。</summary>
    public void SetLevel(string level)
    {
        if (level == Level) return;
        Level = level;
        foreach (var p in new[]
                 {
                     nameof(Level), nameof(NewPrice3Text), nameof(OldPrice3Text), nameof(DeltaText),
                     nameof(RatioText), nameof(Trend), nameof(IsUp), nameof(IsDown), nameof(HasDelta),
                     nameof(NewPriceHeader), nameof(OldPriceHeader),
                 })
            PropChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    public event PropertyChangedEventHandler PropChanged;

    event PropertyChangedEventHandler INotifyPropertyChanged.PropertyChanged
    {
        add => PropChanged += value;
        remove => PropChanged -= value;
    }
}

/// <summary>德州物价查询 —— 国家立项指南在德州落地的规范整合项目（15 位编码 + 三级价格）。</summary>
public partial class DezhouPriceView : UserControl
{
    private int _pageSize = 50;   // 每页条数（下拉可改；只影响当前会话，不落盘）
    private const double FavColWidth = 40;
    /// <summary>「扣减物耗」条件列宽（与网页端 dzCols 的 w:100 同值）。</summary>
    private const double DeductColWidth = 100;
    /// <summary>「立项指南」列宽：运行时插在比对表名称列后，不在 CmpCols 里，算总宽必须单独加。</summary>
    private const double GuideColWidth = 92;

    internal static readonly (string Prop, string Header, double Width, bool Align, bool Mono)[] Cols =
    {
        ("Code", "项目编码", 128, false, true),
        ("Name", "项目名称", 0, false, false),
        ("Category", "类别", 112, false, false),
        ("Unit", "单位", 62, false, false),
        // 价格列**居中**（第五个字段 = true，见 AlignmentOf）。宽度按**本视图真实内容**取大：
        // 表头「● 三级价格」7 字位 ≈ 106 是下限（选医院级别后本档表头会加「● 」前缀，
        // 自检第 64 项按加前缀后的宽度守门）；
        // 文字价只有『自主定价』4 字 ≈ 70 / 『暂不定价』，数字 5 位 ≈ 40。
        // ⚠ 曾按「暂自主定价」5 字定 108 —— 那 18 格只存在于**旧版物价**（6 行），现行项目根本没有；
        // 三列虚胖 + 右对齐让数字离列头右端 40+ px，物价人员看着像「价格列错位」。长句文字价走悬停 / 详情卡。
        ("Price3", "三级价格", 106, true, true),
        ("Price2", "二级价格", 106, true, true),
        ("Price1", "一级价格", 106, true, true),
        // 「扣减物耗」改为**条件列**（2026-11-01）：25 号文「一般治疗」类 11 条有值（注射 / 清创缝合 /
        // 换药 / 导尿 / 膀胱冲洗），其余 3,869 条全空白 —— 曾整列移除，但物价人员筛到一般治疗时
        // 看不到扣减额，用户要求恢复。折中：筛到「一般治疗」或当前页出现带值行时才显示
        // （见 SyncDeductCol / BuildColumns），其余场景保持原布局，不再一列 100px 只服务 11 行。
        // 详情卡的「扣减基本物耗价格」一行（带解释）与导出 / 导入模板列不受影响。
        // 表头 4 个汉字：12.5px 半粗中文 ≈ 17.4px/字 + 左右内边距 20 = 90；
        // 曾设 56 / 62 导致「医保支付」显示成「医保支」、「自付比例」显示成「自付比」（右侧被硬裁）
        ("Insurance", "医保支付", 92, true, false),
        ("SelfPay", "自付比例", 92, true, true),
        ("DocShort", "落地文号", 104, false, false),
    };

    /// <summary>作废对照视图的列（star 宽 0 = 吃掉剩余宽度的名称列）；Batch 为两行模板列，NewCodeText 为链接模板列。</summary>
    internal static readonly (string Prop, string Header, double Width, bool Align, bool Mono)[] CancelCols =
    {
        ("Batch", "批次 / 执行", 168, false, false),
        ("Category", "类别", 122, false, false),
        ("OldCode", "作废编码", 100, false, true),
        ("OldName", "作废项目名称", 0, false, false),
        ("State", "状态", 60, false, false),
        ("NewCodeText", "对应新编码 ▸", 148, false, true),   // 15 位编码等宽 12px ≈ 138，126 会折成两行
        ("NewNameText", "对应新项目", 176, false, false),
        ("Note", "备注", 88, false, false),
    };

    /// <summary>
    /// <summary>
    /// 文本列一律单行（2026-10-30）：行高改为「紧凑 26 / 标准 34」后，换行会把两行文字
    /// 压进一行高度里互相重叠。长名称改用省略号 + 悬停提示（单元格 ToolTip 绑定原值），
    /// 点开详情卡仍可看全文并选中复制。
    /// </summary>
    private const bool WrapText = false;

    /// <summary>旧版物价视图的列（45号文对码表）；CurrentStd 为链接模板列。价格列居中，同现行项目表。</summary>
    internal static readonly (string Prop, string Header, double Width, bool Align, bool Mono)[] LegacyCols =
    {
        ("Code", "项目编码", 104, false, true),
        ("Name", "项目名称", 0, false, false),
        ("Category", "归集口径", 104, false, false),
        ("Unit", "单位", 58, false, false),
        // 旧版物价的价格列常放文字价：『自主定价』4 字、『暂自主定价』5 字（沃森肿瘤会诊等 6 行），
        // 5 字需 ≈ 87px + 内边距 20 = 107，故三级列取 108
        ("Price3", "三级", 108, true, true),
        ("Price2", "二级", 108, true, true),
        ("Price1", "一级", 108, true, true),
        ("Insurance", "医保", 58, false, false),
        ("SelfPay", "自付", 58, false, true),
        ("Status", "状态", 60, false, false),
        ("CurrentStd", "现行标准 ▸", 246, false, false),
        ("NewMark", "", 40, false, false),
    };

    /// <summary>新旧比对视图的列（差额 / 涨幅按正负着色，见 BuildCompareColumns）。
    /// <para>两侧编码都是链接列（新 → 现行项目 / 旧 → 旧版物价），表头加 ▸ 明示可点。</para></summary>
    internal static readonly (string Prop, string Header, double Width, bool Align, bool Mono)[] CmpCols =
    {
        ("NewCodeText", "新项目编码 ▸", 132, false, true),
        ("NewName", "现行项目名称", 0, false, false),
        ("Category", "类别", 122, false, false),
        // 表头 4~5 字，按「中文 17.4px/字 + 内边距 20」留足
        ("NewPrice3Text", "新三级价", 92, true, true),
        ("OldCodeText", "旧项目编码 ▸", 124, false, true),
        ("OldNameText", "旧项目名称", 190, false, false),
        ("OldPrice3Text", "旧三级价", 92, true, true),
        ("DeltaText", "差额", 74, true, true),
        ("RatioText", "涨幅", 66, true, true),
        ("SourceText", "基准来源", 92, false, false),
    };

    /// <summary>物价页的四个视图。</summary>
    private enum ViewKind { Live, Cancel, Legacy, Cmp }

    private bool _loaded;
    /// <summary>数据是否已首载（主窗口决定是否显示载入进度条）。</summary>
    internal bool DataReady => _loaded;
    private bool _building;
    private ViewKind _view = ViewKind.Live;   // 当前视图（默认现行项目）

    /// <summary>链接跳转前的完整现场（非空 = 正处于跳转态，显示「← 返回」按钮）。
    /// <para>只记视图名和关键词不够：从「新旧比对」第 3 页、按类别 + 比对范围筛选后点旧编码跳过去，
    /// 返回时把页码和筛选都丢了，等于让用户重新筛一遍。这里连页码 / 类别 / 比对范围 / 价格口径一起存。</para></summary>
    private BackState _back;

    /// <summary>跳转前的现场快照（「← 返回」按它原样恢复）。</summary>
    private sealed class BackState
    {
        public ViewKind View;
        public string Query = "";
        public int Page = 1;
        public string Cat = "";
        public string Status = "";
        public string CmpScope = "";
        public string CmpLevel = "3";
    }
    private string _doc = "";
    private string _cat = "";
    private string _kind = "";
    private string _ins = "";
    /// <summary>医院级别："" 不限 / "3" / "2" / "1" / "eq"（三档同价）。仅本次会话有效，不落盘（避免把一次误选记成长期偏好）。</summary>
    private string _level = "";
    private string _status = "";   // 旧版物价："" 全部 / dead 已作废 / live 现行
    private string _cmpScope = ""; // 新旧比对："" 全部 / priced 有旧价可比 / cancel 仅作废对照 / none 纯新增
    /// <summary>新旧比对的价格口径："3" 三级（默认）/ "2" 二级 / "1" 一级。本次会话有效，不落盘。</summary>
    private string _cmpLevel = "3";
    private bool _favOnly;
    private int _page = 1;

    /// <summary>当前页的原始数据（导出「仅当前页」时直接用，避免再从 VM 反查）</summary>
    private List<DezhouPriceItem> _pageItems = new();

    /// <summary>使用说明面板当前是否展开（默认收起，选择记进本机设置）</summary>
    private bool _usageOpen = SettingsStore.UsageOpen;
    // 「使用说明」面板聚焦态（详情卡「点此定位查看全部」设置）：优先于表格筛选决定面板内容；
    // 手动改下拉 / 清空 / 切视图即失效，回落到跟随筛选
    private string _usageFocusDoc;
    private string _usageFocusCat;

    /// <summary>键入防抖：停顿 280ms 才跑一次全表扫描</summary>
    private readonly Debounce _debounce;

    private DataGridColumn _nameCol;
    /// <summary>「扣减物耗」条件列（仅一般治疗类 11 条有值；SyncDeductCol 按筛选决定显隐）。</summary>
    private DataGridColumn _deductCol;
    private DataGridColumn _cancelNameCol;
    private DataGridColumn _legacyNameCol;
    private DataGridColumn _cmpNameCol;

    public DezhouPriceView()
    {
        InitializeComponent();
        BuildColumns();
        BuildCancelColumns();
        BuildLegacyColumns();
        BuildCompareColumns();
        // WPF DataGrid 的 star 列分配不可靠（会被压到最小宽 20，名称列整个消失），
        // 改为显式把「表格宽度 − 固定列合计」分给名称列
        DzGrid.SizeChanged += (_, _) => FitNameColumn();
        DzCancelGrid.SizeChanged += (_, _) => FitCancelNameColumn();
        DzLegacyGrid.SizeChanged += (_, _) => FitLegacyNameColumn();
        DzCmpGrid.SizeChanged += (_, _) => FitCmpNameColumn();
        _debounce = new Debounce(280, () =>
            RunBusy("正在检索物价项目…", SearchBusyHint(), () => Load(1)));
    }

    /// <summary>诊断专用（--shot-page dzfav）：勾选「仅看收藏」并触发筛选</summary>
    internal void DiagFavOnly() => FavOnly.IsChecked = true;

    /// <summary>诊断专用（--shot-page dzcancel）：切到「作废对照」视图</summary>
    internal void DiagCancel()
    {
        // ⚠ 离屏探针下 Checked 是异步派发的，只设 IsChecked 不会跑 OnViewChanged，
        //   截图仍是现行项目表（同 DiagCompareQuery）；必须直接 ApplyView。
        EnsureLoaded();
        ApplyView(ViewKind.Cancel);
    }

    /// <summary>诊断专用（--shot-page dzcancel:关键词）：切到「作废对照」视图并按关键词检索（核对新编码链接列）</summary>
    internal void DiagCancelQuery(string q)
    {
        EnsureLoaded();          // 先载数据再切视图：不载直接 Load，作废网格拿不到行
        ApplyView(ViewKind.Cancel);   // 同 DiagCancel：离屏下 Checked 不派发
        DzQuery.Text = q ?? "";
        Load(1);
    }

    /// <summary>诊断专用（--shot-page dzcdoc:文号子串）：切到作废对照并按批次下拉筛选（核对批次维度可见性）</summary>
    internal void DiagCancelDoc(string docPart)
    {
        EnsureLoaded();
        SegCancel.IsChecked = true;
        _building = true;
        foreach (ComboBoxItem i in DocSel.Items)
        {
            var tag = (i.Tag as string) ?? "";
            if (tag.Length > 0 && tag.Contains(docPart ?? "", StringComparison.OrdinalIgnoreCase))
            {
                DocSel.SelectedItem = i;
                _doc = tag;          // 绕过 SelectionChanged（_building 挡住了），这里显式同步筛选态
                break;
            }
        }
        _building = false;
        Load(1);
    }

    /// <summary>诊断专用（--shot-page dzlive:编码）：先停在「旧版物价」再模拟点链接跳转（真实来源是旧版/作废对照视图）</summary>
    internal void DiagGoLive(string code)
    {
        EnsureLoaded();
        ApplyView(ViewKind.Legacy);
        GoToLive(code);
    }

    /// <summary>诊断专用（--shot-page dzback:编码）：跳转现行项目后立即点「← 返回」（核对返回态与布局）</summary>
    internal void DiagJumpBack(string code)
    {
        EnsureLoaded();
        GoToLive(code);
        GoBack();
    }

    /// <summary>诊断专用（--shot-page dzlegacy）：切到「旧版物价」视图</summary>
    internal void DiagLegacy()
    {
        EnsureLoaded();          // 离屏下 Checked 不派发，必须同步切（同 DiagCancel）
        ApplyView(ViewKind.Legacy);
    }

    /// <summary>诊断专用（--shot-page dzlegacy:关键词）：切旧版物价并按编码/名称检索（核对作废状态着色）</summary>
    internal void DiagLegacyQuery(string q)
    {
        EnsureLoaded();
        ApplyView(ViewKind.Legacy);
        DzQuery.Text = q ?? "";
        Load(1);
    }

    /// <summary>诊断专用（--shot-page dzfind:关键词）：现行项目视图按关键词检索（核对加收项公式价换算展示）</summary>
    internal void DiagLiveQuery(string q)
    {
        EnsureLoaded();
        SegLive.IsChecked = true;
        DzQuery.Text = q ?? "";
        Load(1);
    }

    /// <summary>诊断专用（--shot-page dzlevel:3|2|1|eq[:关键词]）：选定医院级别并检索（核对筛选与列头标注）</summary>
    internal void DiagLevel(string level, string q)
    {
        EnsureLoaded();
        SegLive.IsChecked = true;
        for (var i = 0; i < LevelSel.Items.Count; i++)
            if ((LevelSel.Items[i] as ComboBoxItem)?.Tag as string == (level ?? ""))
                LevelSel.SelectedIndex = i;
        // ⚠ 离屏探针下事件是**异步**派发的：SetThreadDpiAwarenessContext + Render 之前没人泵消息，
        //    OnLevelChanged 可能一次都没跑到，于是 _level 仍是旧值、列表照旧不分级。
        //    诊断入口直接落值，不依赖事件时序（与 DiagSelect 同思路）。
        _level = level ?? "";
        ApplyLevelToColumns();
        DzQuery.Text = q ?? "";
        Load(1);
    }

    /// <summary>诊断专用（--shot-page dzcmp:关键词）：切到「新旧比对」视图（核对差额涨幅与三态标注）</summary>
    internal void DiagCompareQuery(string q)
    {
        // ⚠ 离屏探针下 Checked 事件是**异步**派发的，不泵消息就不会跑 OnViewChanged，
        //   只设 IsChecked 的话截图仍是现行项目表（与 DiagLevel 同一坑）。
        EnsureLoaded();
        ApplyView(ViewKind.Cmp);
        DzQuery.Text = q ?? "";
        Load(1);
    }

    /// <summary>诊断专用（--shot-page dzcmplv:2|1[:范围]）：切到比对视图并指定价格口径。</summary>
    internal void DiagCompareLevel(string level, string scope = "")
    {
        EnsureLoaded();
        ApplyView(ViewKind.Cmp);

        // ApplyView 内部按「空范围」Load 过一次，这里先落下目标范围再重取一次，最后统一下发口径。
        var nextScope = scope ?? "";
        for (var i = 0; i < CmpSel.Items.Count; i++)
            if ((CmpSel.Items[i] as ComboBoxItem)?.Tag as string == nextScope)
                CmpSel.SelectedIndex = i;
        for (var i = 0; i < CmpLevelSel.Items.Count; i++)
            if ((CmpLevelSel.Items[i] as ComboBoxItem)?.Tag as string == (level ?? ""))
                CmpLevelSel.SelectedIndex = i;
        _cmpScope = nextScope;
        _cmpLevel = string.IsNullOrEmpty(level) ? "3" : level;

        Load(1);
        ApplyCmpLevel();
        // 遮罩由事件触发，而离屏路径不跑消息循环、HideAfter 的定时器不 Tick，
        // 截图里会留一张半透明卡片压住表格。诊断是确定性渲染，直接收起即可。
        Busy.Hide();
    }

    /// <summary>
    /// 诊断 / 自检专用：真实点击「新旧比对」第 <paramref name="rowIndex"/> 行的新编码或旧编码链接。
    /// <returns>「visual:编码」= 点到了真实按钮；「fallback:编码」= 行未生成、退回业务方法；空串 = 没有该链接。</returns>
    /// <para>先在已生成的行容器里找那个按钮点（证明链接真的可点、不是只有业务方法通），
    /// 虚拟化没生成行时才回落到与点击同一条业务方法。</para>
    /// </summary>
    internal string ClickCmpCodeLink(int rowIndex, bool old)
    {
        if (DzCmpGrid.ItemsSource is not IEnumerable<DzCompareRowVm> rows) return "";
        var vm = rows.ElementAtOrDefault(rowIndex);
        if (vm == null) return "";
        var code = (old ? vm.OldCode : vm.NewCode) ?? "";
        if (string.IsNullOrWhiteSpace(code)) return "";

        DzCmpGrid.ScrollIntoView(vm);
        DzCmpGrid.UpdateLayout();
        if (DzCmpGrid.ItemContainerGenerator.ContainerFromItem(vm) is DataGridRow row)
        {
            foreach (var b in ButtonsIn(row))
            {
                if (b.Tag as string != code) continue;
                b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return "visual:" + code;
            }
        }
        if (old) GoToLegacy(code); else GoToLive(code);
        return "fallback:" + code;
    }

    private static IEnumerable<Button> ButtonsIn(DependencyObject root)
    {
        var n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (c is Button b) yield return b;
            foreach (var x in ButtonsIn(c)) yield return x;
        }
    }

    /// <summary>诊断 / 自检专用：模拟点「← 返回」。</summary>
    internal void DiagBack() => GoBack();

    /// <summary>诊断 / 自检专用：直接翻到指定页（不经过遮罩与防抖）。</summary>
    internal void DiagPage(int page) => Load(page);

    /// <summary>自检断言用：当前视图名 / 返回态 / 返回按钮文案与可见性 / 当前页。</summary>
    internal string CurrentViewName => ViewName(_view);
    internal string BackStateViewName => _back == null ? "" : ViewName(_back.View);
    internal string BackLabel => BackBtn.Content as string ?? "";
    internal Visibility BackVisible => BackBtn.Visibility;
    internal int CurrentPage => _page;

    /// <summary>
    /// 诊断 / 自检：导出按钮是不是「错行」了。
    /// 判据两条：① 导出范围与导出按钮**垂直中心**对齐（同一行，不被拆开）；
    /// ② 导出按钮不在筛选条里（在筛选条里就有被 WrapPanel 换行挤走的可能）。
    /// </summary>
    internal (double ScopeTop, double BtnTop, double ScopeLeft, double BtnLeft, bool SameRow, bool InFilterBar)
        DiagExportLayout()
    {
        var a = ExpScope.TransformToAncestor(this).Transform(new Point(0, 0));
        var b = ExpBtn.TransformToAncestor(this).Transform(new Point(0, 0));
        var ac = a.Y + ExpScope.ActualHeight / 2;
        var bc = b.Y + ExpBtn.ActualHeight / 2;
        return (a.Y, b.Y, a.X, b.X, Math.Abs(ac - bc) <= 2, FilterBar.IsAncestorOf(ExpBtn));
    }

    /// <summary>诊断 / 自检：导出组整块是否可见（现行项目 / 新旧比对可见，作废对照 / 旧版物价收起）。</summary>
    internal Visibility ExportBarVisibility => ExportBar.Visibility;

    /// <summary>诊断 / 自检：当前导出范围下拉的 Tag 序列（现行 [all,page,fav] / 比对 [all,page]）。</summary>
    internal IReadOnlyList<string> DiagExpScopeTags()
        => ExpScope.Items.Cast<ComboBoxItem>().Select(i => i.Tag as string ?? "").ToList();

    /// <summary>诊断 / 自检专用：切到「新旧比对」视图（同步切，不走进度条/淡入那条延时通道）。</summary>
    internal void DiagCompare()
    {
        EnsureLoaded();
        ApplyView(ViewKind.Cmp);
    }

    /// <summary>诊断 / 自检专用：切到「旧版物价」视图（同步切）。</summary>
    internal void DiagLegacyView()
    {
        EnsureLoaded();
        ApplyView(ViewKind.Legacy);
    }

    /// <summary>诊断 / 自检专用：切回「现行项目」视图（同步切）。</summary>
    internal void DiagLive()
    {
        EnsureLoaded();
        ApplyView(ViewKind.Live);
    }

    /// <summary>诊断专用（--shot-page dz:文号:类别）：模拟在下拉中选中指定文号与类别</summary>
    internal void DiagSelect(string doc, string cat)
    {
        EnsureLoaded();
        // ⚠ 不能拿 _building 屏蔽 SelectionChanged：那会连「使用说明面板」一起挡掉
        //   （自检第 73 项就是选中中医骨伤类后要看到 8 条条款）。这里让事件照常跑，
        //   再显式兜一次筛选态 + 面板 + 取数 —— 离屏探针下事件不派发时也不会「下拉变了、列表没变」。
        foreach (ComboBoxItem i in DocSel.Items)
            if ((i.Tag as string ?? "") == (doc ?? "")) { DocSel.SelectedItem = i; break; }
        foreach (ComboBoxItem i in CatSel.Items)
            if ((i.Tag as string ?? "") == (cat ?? "")) { CatSel.SelectedItem = i; break; }
        _doc = doc ?? "";
        _cat = cat ?? "";
        UpdateUsagePanel();
        Load(1);
    }

    /// <summary>名称列吃掉全部剩余宽度（保底 120，其余列固定紧凑宽）</summary>
    private void FitNameColumn()
    {
        if (_nameCol == null || DzGrid.ActualWidth <= 0) return;
        // 「扣减物耗」条件列显示时占 100px，必须从名称列的余量里扣掉，否则列合计超宽
        var deduct = _deductCol != null && _deductCol.Visibility == Visibility.Visible ? DeductColWidth : 0;
        var fixedSum = Cols.Where(c => c.Width > 0).Sum(c => c.Width) + deduct + 30 + FavColWidth;
        var w = DzGrid.ActualWidth - fixedSum;
        _nameCol.Width = new DataGridLength(Math.Max(80, w));
    }

    /// <summary>作废视图的「作废项目名称」列吃掉剩余宽度（保底 80；三表已禁横向滚动，列合计必须收敛）</summary>
    private void FitCancelNameColumn()
    {
        if (_cancelNameCol == null || DzCancelGrid.ActualWidth <= 0) return;
        var fixedSum = CancelCols.Where(c => c.Width > 0).Sum(c => c.Width) + 30;
        var w = DzCancelGrid.ActualWidth - fixedSum;
        _cancelNameCol.Width = new DataGridLength(Math.Max(80, w));
    }

    /// <summary>旧版视图的「项目名称」列吃掉剩余宽度（保底 80）</summary>
    private void FitLegacyNameColumn()
    {
        if (_legacyNameCol == null || DzLegacyGrid.ActualWidth <= 0) return;
        var fixedSum = LegacyCols.Where(c => c.Width > 0).Sum(c => c.Width) + 30;
        var w = DzLegacyGrid.ActualWidth - fixedSum;
        _legacyNameCol.Width = new DataGridLength(Math.Max(80, w));
    }

    /// <summary>比对视图的「现行项目名称」列吃掉剩余宽度（保底 80）</summary>
    private void FitCmpNameColumn()
    {
        if (_cmpNameCol == null || DzCmpGrid.ActualWidth <= 0) return;
        // ⚠ CmpCols 里没有「立项指南」列（运行时插的），漏算 92 会让名称列多占 92，
        //   总宽比视口宽 62px —— 关了横滚就是硬裁，最后一列「基准来源」只露出半个字。
        var fixedSum = CmpCols.Where(c => c.Width > 0).Sum(c => c.Width) + GuideColWidth + 30;
        var w = DzCmpGrid.ActualWidth - fixedSum;
        _cmpNameCol.Width = new DataGridLength(Math.Max(80, w));
    }

    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        BuildDocs();
        BuildCats();
        BuildKinds();
        BuildInsurance();
        BuildLevels();
        BuildStatuses();
        BuildCmpScopes();
        BuildPageSizes();
        UpdateStat();
        UpdateFavLabel();
        BuildExpScopes(ViewKind.Live);
        Load(1);
        UpdateUsagePanel();
    }

    /// <summary>
    /// 启动空闲预热：载入数据并强制布局一次，把「首次进入本页」的元素树构建成本提前消化
    /// （实测首屏 50 行首次布局约 0.5s，预热后点开页签几乎瞬时）。
    /// 页面在页签容器里可能仍是 Collapsed，手工 Measure/Arrange 不受 Visibility 影响。
    /// </summary>
    public void Prewarm(double w, double h)
    {
        EnsureLoaded();
        if (w <= 0) w = 1200;
        if (h <= 0) h = 700;
        Measure(new Size(w, h));
        Arrange(new Rect(0, 0, w, h));
        UpdateLayout();
    }

    private void BuildInsurance()
    {
        _building = true;
        InsSel.Items.Clear();
        InsSel.Items.Add(new ComboBoxItem { Content = "医保不限", Tag = "" });
        InsSel.Items.Add(new ComboBoxItem { Content = "医保支付", Tag = "是" });
        InsSel.Items.Add(new ComboBoxItem { Content = "非医保支付", Tag = "否" });
        InsSel.SelectedIndex = 0;
        _building = false;
    }

    /// <summary>
    /// 医院级别下拉。三级 / 二级 / 一级是**价格档位**（同一项目按医院等级定价），
    /// 选某级 = 只列该级有价的项目，且价格列高亮该档（见 <see cref="ApplyLevelToColumns"/>）。
    /// 选择记在 settings.json 里，下次打开沿用（物价人员通常长期只查自己那一级）。
    /// </summary>
    private void BuildLevels()
    {
        _building = true;
        _levelSyncing = true;   // 构建期间的 SelectionChanged（含排到构建后的那一发）不算用户选择
        LevelSel.Items.Clear();
        LevelSel.Items.Add(new ComboBoxItem { Content = "级别不限", Tag = "" });
        LevelSel.Items.Add(new ComboBoxItem { Content = "三级医院", Tag = "3" });
        LevelSel.Items.Add(new ComboBoxItem { Content = "二级医院", Tag = "2" });
        LevelSel.Items.Add(new ComboBoxItem { Content = "一级医院", Tag = "1" });
        LevelSel.Items.Add(new ComboBoxItem { Content = "各级同价", Tag = "eq" });
        LevelSel.SelectedIndex = 0;
        _level = "";
        _building = false;
        // 排队的 SelectionChanged 在下一轮 Dispatcher 才到，这里提前放行；真正的用户操作在输入队列之后
        Dispatcher.BeginInvoke(new Action(() => _levelSyncing = false),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>
    /// 级别切换：改筛选、并把「当前级别」标到价格列头上。
    /// ⚠ WPF 的 SelectionChanged 可能在 <see cref="BuildLevels"/> 的 <c>_building = false</c>
    /// **之后**才派发（下拉项是运行期新增的，事件排队），所以这里再判一次
    /// <see cref="_levelSyncing"/>，否则首次构建就会被当成「用户手选了」。
    /// </summary>
    private void OnLevelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_building || _levelSyncing) return;
        _level = (LevelSel.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        ApplyLevelToColumns();
        if (!_loaded) return;
        _usageFocusDoc = _usageFocusCat = null;
        UpdateUsagePanel();
        Load(1);
    }

    /// <summary><see cref="BuildLevels"/> 期间置位，构建结束后再放一轮排队的 SelectionChanged</summary>
    private bool _levelSyncing;

    /// <summary>
    /// 价格列表头随级别标注：选中某级时本档加「● 」前缀（96px 塞得下 5 字，不去撑列宽），
    /// 选中「各级同价」时只留三级价一列，避免三列数字完全一样。
    /// </summary>
    private void ApplyLevelToColumns()
    {
        var name = DezhouPriceStore.LevelName(_level);
        foreach (var c in DzGrid.Columns)
        {
            if (c.Header is not string h) continue;
            var label = h.StartsWith("● ", StringComparison.Ordinal) ? h[2..] : h;
            var isPrice = label is "三级价格" or "二级价格" or "一级价格";
            if (!isPrice) continue;
            var hit = (_level == "3" && label == "三级价格")
                   || (_level == "2" && label == "二级价格")
                   || (_level == "1" && label == "一级价格");
            c.Header = hit ? "● " + label : label;
            c.Visibility = _level == "eq" && label != "三级价格" ? Visibility.Collapsed : Visibility.Visible;
        }
        DzStatus.Text = _level.Length == 0 ? DzStatus.Text
            : $"当前：{name}医院价格口径";
    }

    private void BuildStatuses()
    {
        _building = true;
        StatusSel.Items.Clear();
        StatusSel.Items.Add(new ComboBoxItem { Content = "状态不限", Tag = "" });
        StatusSel.Items.Add(new ComboBoxItem { Content = "仅已作废", Tag = "dead" });
        StatusSel.Items.Add(new ComboBoxItem { Content = "仅现行", Tag = "live" });
        StatusSel.SelectedIndex = 0;
        _building = false;
    }

    /// <summary>比对范围下拉（仅新旧比对视图显示）。</summary>
    private void BuildCmpScopes()
    {
        _building = true;
        CmpSel.Items.Clear();
        CmpSel.Items.Add(new ComboBoxItem { Content = "范围：全部", Tag = "" });
        CmpSel.Items.Add(new ComboBoxItem { Content = "范围：有旧价可比", Tag = "priced" });
        CmpSel.Items.Add(new ComboBoxItem { Content = "范围：仅作废对照", Tag = "cancel" });
        CmpSel.Items.Add(new ComboBoxItem { Content = "范围：纯新增", Tag = "none" });
        CmpSel.SelectedIndex = 0;
        _building = false;

        // 比对口径下拉：三档价各自独立，差额 / 涨幅 / 涨跌统计都跟着这一档算
        _building = true;
        CmpLevelSel.Items.Clear();
        CmpLevelSel.Items.Add(new ComboBoxItem { Content = "口径：三级医院", Tag = "3" });
        CmpLevelSel.Items.Add(new ComboBoxItem { Content = "口径：二级医院", Tag = "2" });
        CmpLevelSel.Items.Add(new ComboBoxItem { Content = "口径：一级医院", Tag = "1" });
        CmpLevelSel.SelectedIndex = 0;
        _cmpLevel = "3";
        _building = false;
    }

    /// <summary>
    /// 比对口径切换（三级 / 二级 / 一级）。
    /// <para>不重新取数：三档差额在数据层已经一次算全（<c>FillDelta</c>），
    /// 这里只改每行的 <see cref="DzCompareRowVm.Level"/>、列头与统计条。
    /// 若走 <c>Load(1)</c> 重建 ItemsSource，用户刚滚到的位置与展开行都会被重置，
    /// 切个价格档位像重新进了一次页面。</para>
    /// </summary>
    private void OnCmpLevelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_building || !_loaded) return;
        var lv = (CmpLevelSel.SelectedItem as ComboBoxItem)?.Tag as string ?? "3";
        if (lv == _cmpLevel) return;
        _cmpLevel = lv;
        ApplyCmpLevel();
    }

    /// <summary>把当前口径下发到已加载的每一行，并同步表头与统计条。</summary>
    private void ApplyCmpLevel()
    {
        if (DzCmpGrid.ItemsSource is IEnumerable<DzCompareRowVm> rows)
            foreach (var r in rows) r.SetLevel(_cmpLevel);

        foreach (var c in DzCmpGrid.Columns)
        {
            if (c.Header is "新三级价" or "新二级价" or "新一级价")
                c.Header = _cmpLevel switch { "2" => "新二级价", "1" => "新一级价", _ => "新三级价" };
            else if (c.Header is "旧三级价" or "旧二级价" or "旧一级价")
                c.Header = _cmpLevel switch { "2" => "旧二级价", "1" => "旧一级价", _ => "旧三级价" };
        }

        UpdateStat();
        var name = DezhouPriceStore.LevelName(_cmpLevel);
        DzStatus.Text = $"比对口径：{name}医院价格（差额、涨幅与涨跌统计均按此档计算）";
    }

    private void BuildDocs()
    {
        _building = true;
        DocSel.Items.Clear();
        if (_view == ViewKind.Cancel)
        {
            var st = App.State.Dezhou.CancelStat();
            DocSel.Items.Add(new ComboBoxItem { Content = $"全部批次（{st.Total:N0}）", Tag = "" });
            foreach (var b in App.State.Dezhou.Batches())
            {
                var n = App.State.Dezhou.CancelRows(b.Doc, "", "").Count;
                if (n == 0) continue;   // 没有映射表的批次不下拉（如早期的独立物价批次）
                DocSel.Items.Add(new ComboBoxItem
                {
                    Content = $"{b.Doc}（{n:N0}条 · {b.Exec}）",
                    Tag = b.Doc,
                });
            }
        }
        else
        {
            var all = App.State.Dezhou.Items.Count;
            DocSel.Items.Add(new ComboBoxItem { Content = $"全部批次（{all:N0}）", Tag = "" });
            foreach (var b in App.State.Dezhou.Batches())
                DocSel.Items.Add(new ComboBoxItem
                {
                    Content = $"{b.Doc}（{b.Rows:N0}条 · {b.Exec}）",
                    Tag = b.Doc,
                });
        }
        DocSel.SelectedIndex = 0;
        _building = false;
    }

    private void BuildCats()
    {
        _building = true;
        CatSel.Items.Clear();
        CatSel.Items.Add(new ComboBoxItem { Content = "全部类别", Tag = "" });
        var cats = _view switch
        {
            ViewKind.Cancel => App.State.Dezhou.CancelCategories(_doc),
            ViewKind.Legacy => App.State.Dezhou.LegacyCategories(),
            ViewKind.Cmp => App.State.Dezhou.CompareRows()
                .Select(r => (r.Category ?? "").Trim())
                .Where(v => v.Length > 0).Distinct().OrderBy(c => c).ToList(),
            _ => App.State.Dezhou.Categories(_doc),
        };
        foreach (var c in cats)
            CatSel.Items.Add(new ComboBoxItem { Content = c, Tag = c });
        CatSel.SelectedIndex = 0;
        _building = false;
    }

    private void BuildKinds()
    {
        _building = true;
        KindSel.Items.Clear();
        KindSel.Items.Add(new ComboBoxItem { Content = "全部项目", Tag = "" });
        KindSel.Items.Add(new ComboBoxItem { Content = "仅主项目", Tag = "main" });
        KindSel.Items.Add(new ComboBoxItem { Content = "仅加收项", Tag = "add" });
        KindSel.Items.Add(new ComboBoxItem { Content = "仅扩展项", Tag = "ext" });
        KindSel.SelectedIndex = 0;
        _building = false;
    }

    private void UpdateStat()
    {
        switch (_view)
        {
            case ViewKind.Cancel:
            {
                var c = App.State.Dezhou.CancelStat();
                StatText.Text = c.Total == 0
                    ? "尚未合并作废对照数据"
                    : $"作废对照 {c.Total:N0} 行"
                      + (c.Kept > 0 ? $"（其中保留 {c.Kept:N0}·原文划线不作废）" : "")
                      + $" · 有替代 {c.Mapped:N0} · 直接废止 {c.Dead:N0} · {c.Batches} 批 / {c.Categories} 类";
                return;
            }
            case ViewKind.Legacy:
            {
                var l = App.State.Dezhou.LegacyStat();
                StatText.Text = l.Total == 0
                    ? "尚未合并旧版物价数据"
                    : $"旧版物价 {l.Total:N0} 条 · 已作废 {l.Dead:N0} · 现行 {l.Live:N0} · {l.Categories} 个归集口径";
                return;
            }
            case ViewKind.Cmp:
            {
                // 涨跌按当前口径统计（三级 / 二级 / 一级），与表格里展示的差额同口径
                var c = App.State.Dezhou.CompareStat(_cmpLevel);
                var lv = DezhouPriceStore.LevelName(_cmpLevel);
                StatText.Text = c.Total == 0
                    ? "暂无比对数据"
                    : $"比对 {c.Total:N0} 行 · 有旧价可比 {c.Priced:N0}（降 {c.Down:N0} / 涨 {c.Up:N0} / 平 {c.Flat:N0}）"
                      + $" · 仅作废对照 {c.CancelOnly:N0} · 纯新增 {c.BrandNew:N0} · 按{lv}医院价";
                return;
            }
        }
        var s = App.State.Dezhou.Stat();
        StatText.Text = s.Total == 0
            ? "尚未导入德州物价数据"
            : $"共 {s.Total:N0} 条 · 主项目 {s.Main:N0} · 加收项 {s.Add:N0} · 扩展项 {s.Ext:N0} · 医保支付 {s.Insured:N0} · {s.BatchCount} 批 / {s.CategoryCount} 类";
    }

    private void UpdateFavLabel()
    {
        var n = App.State.DezhouFav.Count;
        FavOnly.Content = n > 0 ? $"仅看收藏（{n}）" : "仅看收藏";
    }

    // ==================================================================
    // 类别「使用说明」
    // ==================================================================

    /// <summary>
    /// 使用说明面板内容：
    /// 选了具体类别 → 该类的说明；只选了文号 → 该文号下各类别的说明（按类别分组）；
    /// 都没选 → 全部批次的使用说明（按「文号 · 类别」分组）。
    /// 详情卡摘录提示「点页面上方『使用说明』查看全部」，因此面板任何筛选态下都必须有内容。
    /// </summary>
    private void UpdateUsagePanel()
    {
        if (!_loaded) return;
        if (_view != ViewKind.Live)
        {
            // 作废对照 / 旧版物价没有类别使用说明（说明条款只挂在现行项目表上）
            UsageCard.Visibility = Visibility.Collapsed;
            UsageList.ItemsSource = null;
            return;
        }

        var doc = _usageFocusDoc ?? _doc;
        var cat = _usageFocusCat ?? _cat;
        var rows = new List<UsageVm>();
        var title = "";

        if (cat.Length > 0)
        {
            foreach (var t in App.State.Dezhou.UsageOf(doc, cat))
                rows.Add(new UsageVm { No = rows.Count + 1 + ".", Text = t });
            title = doc.Length > 0 ? $"{doc} · {cat}" : cat;
            title += $"　共 {rows.Count} 条";
        }
        else if (doc.Length > 0)
        {
            var groups = App.State.Dezhou.UsageOfDoc(doc);
            foreach (var (name, list) in groups)
                for (var i = 0; i < list.Count; i++)
                    rows.Add(new UsageVm { No = i + 1 + ".", Group = i == 0 ? name : "", Text = list[i] });
            title = $"{doc}　{groups.Count} 个类别 / 共 {rows.Count} 条";
        }
        else
        {
            // 未筛选：全量展示（详情卡「查看全部」的落点），限高内滚不撑长页面
            foreach (var (d, name, list) in App.State.Dezhou.UsageAll())
                for (var i = 0; i < list.Count; i++)
                    rows.Add(new UsageVm { No = i + 1 + ".", Group = i == 0 ? $"{d} · {name}" : "", Text = list[i] });
            var cats = App.State.Dezhou.UsageAll().Select(g => g.Category).Distinct().Count();
            title = $"全部批次　{cats} 个类别 / 共 {rows.Count} 条";
        }

        if (rows.Count == 0)
        {
            UsageCard.Visibility = Visibility.Collapsed;
            UsageList.ItemsSource = null;
            return;
        }

        UsageTitle.Text = title;
        UsageCard.Visibility = Visibility.Visible;
        UsageList.ItemsSource = rows;
        ApplyUsageOpenState();
    }

    /// <summary>
    /// 详情卡「点此定位查看全部」：面板聚焦到该行的文号 + 类别并展开。
    /// 只改面板内容，不动表格筛选，不打断当前浏览。
    /// </summary>
    internal void FocusUsage(string doc, string cat)
    {
        _usageFocusDoc = doc;
        _usageFocusCat = cat;
        _usageOpen = true;
        UpdateUsagePanel();
        DzStatus.Text = $"「使用说明」已定位：{doc} · {cat}（点筛选或清空可退出定位）";
    }

    private void ApplyUsageOpenState()
    {
        UsageBody.Visibility = _usageOpen ? Visibility.Visible : Visibility.Collapsed;
        UsageToggle.Content = _usageOpen ? "收起" : "展开";
    }

    private void OnToggleUsage(object sender, RoutedEventArgs e)
    {
        _usageOpen = !_usageOpen;
        ApplyUsageOpenState();
        // 记住选择：下次开程序仍按用户上次的偏好，而不是每次都重新展开
        SettingsStore.SetUsageOpen(_usageOpen);
    }

    // ==================================================================
    // 常用收藏
    // ==================================================================

    /// <summary>
    /// 切换一行的收藏态并落盘：星标点击与右键菜单共用这一条通路。
    /// 返回切换后「是否已收藏」。
    /// </summary>
    internal static bool ToggleFav(DzRowVm vm)
    {
        if (vm == null) return false;
        var now = App.State.DezhouFav.Toggle(vm.Code, vm.Doc, vm.Name);
        vm.IsFav = now;
        vm.FavToggled?.Invoke(vm, now);
        return now;
    }

    /// <summary>点行首星标：加入 / 移出收藏并落盘；「仅看收藏」下取消收藏会立即从列表移除</summary>
    private void OnToggleFav(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not DzRowVm vm) return;
        ToggleFav(vm);
    }

    private void OnFavFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_building || !_loaded) return;
        _favOnly = FavOnly.IsChecked == true;
        RunBusy(_favOnly ? "正在筛选收藏项目…" : "正在载入全部项目…",
            $"仅看收藏 · 已收藏 {App.State.DezhouFav.Count} 条", () => Load(1));
    }

    // ==================================================================
    // 筛选与检索
    // ==================================================================

    /// <summary>视图切换：现行项目 / 作废对照 / 旧版物价 / 新旧比对。筛选条共用，各视图按需显隐。</summary>
    private void OnViewChanged(object sender, RoutedEventArgs e)
    {
        // InitializeComponent 期间 SegLive 的 IsChecked="True" 会先触发一次 Checked，
        // 此时表格等元素尚未构造完成；初始态就是「现行项目」，直接忽略即可。
        if (_building || !_loaded) return;
        var view = SegCancel.IsChecked == true ? ViewKind.Cancel
            : SegLegacy.IsChecked == true ? ViewKind.Legacy
            : SegCmp.IsChecked == true ? ViewKind.Cmp
            : ViewKind.Live;
        if (view == _view) return;
        SwitchViewAnimated(view);
    }

    /// <summary>
    /// 带进度的视图切换（点击分段的唯一入口）。
    ///
    /// <para>为什么需要遮罩：切视图要重建批次 / 类别下拉、重排 50 行，实测 200~400ms
    /// （拼音检索时批次重建更久）。这段时间全在主线程里同步跑，窗口一点不刷新，
    /// 鼠标点完到表格出现之间是纯静止画面 —— 用户的第一反应就是「没点上 / 卡死了」。</para>
    ///
    /// <para>做法：先亮遮罩并**把一帧让出去**（DispatcherPriority.Render 优先级回调排在
    /// 所有渲染之后，等它回调时遮罩已经真正画在屏幕上），再同步跑 ApplyView。
    /// 跑完至少让遮罩停留 <see cref="BusyMinMs"/>，让沙漏至少转一圈 ——
    /// 一闪而过的遮罩比没有遮罩更像故障。</para>
    /// </summary>
    private void SwitchViewAnimated(ViewKind view)
    {
        Busy.Show(BusyLabel(view), BusyHint(view), EstimateLoadMs());

        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                ApplyView(view);
                // 手动切换分段 = 放弃跳转返回态（GoToLive 内部走 ApplyView，被 _building 守卫拦住，不会到这里）
                _back = null;
                UpdateBackBtn();
            }
            finally
            {
                Busy.HideAfter();
            }
        }), DispatcherPriority.Render);
    }

    // ==================================================================
    // 切换遮罩（控件本体见 Controls/BusyVeil）
    // ==================================================================

    // 诊断入口：实机验证「点这四个页签会亮遮罩」时，需要真实触发 Checked
    internal RadioButton SegLiveButton => SegLive;
    internal RadioButton SegCancelButton => SegCancel;
    internal RadioButton SegLegacyButton => SegLegacy;
    internal RadioButton SegCmpButton => SegCmp;

    // 诊断入口：验证「查询 / 翻页」也走同一套遮罩（真实触发按钮的 Click，而不是直接调 RunBusy）
    internal Button SearchButton => SearchBtn;
    internal Button NextPageButton => DzNext;
    internal TextBox QueryBox => DzQuery;

    // 诊断入口：核对三档口径（行 Level 是否下发、表头是否随档、换档是否重建了 VM）
    internal IEnumerable<DzCompareRowVm> CmpRows => DzCmpGrid.ItemsSource as IEnumerable<DzCompareRowVm>
                                                    ?? Enumerable.Empty<DzCompareRowVm>();
    internal List<string> CmpHeaders => DzCmpGrid.Columns.Select(c => c.Header as string ?? "").ToList();
    internal string CmpLevelTag => _cmpLevel;
    internal string CmpScopeTag => _cmpScope;
    /// <summary>诊断 / 自检用：状态栏当前文案（跳转成功与否的判据）。</summary>
    internal string StatusForTest => DzStatus.Text;

    /// <summary>
    /// 自检 / 探针专用：真实触发某一行的「立项指南」链接点击（走 OnGuideLink 全部逻辑）。
    /// <para>为什么不用 <c>DataGridColumn.GetCellContent</c>：模板列里按钮是 Content，
    /// 外面还套了一层 DataGridCell，列对象拿到的往往是 null（行未虚拟化 / 单元格未生成）。
    /// 这里直接从行的可视化树里找那个按钮，找得到再点，找不到如实返回空串。</para>
    /// </summary>
    /// <returns>被点的项目名；没找到可点按钮时返回空串。</returns>
    internal string ClickGuideLink(int rowIndex)
    {
        if (DzCmpGrid.Items.Count <= rowIndex) return "";
        var vm = DzCmpGrid.Items[rowIndex] as DzCompareRowVm;
        if (vm == null) return "";

        // 宿主窗口里本页可能还是 Collapsed 且从未真正布局（自检 / 探针常见），
        // 行容器就一个都不会生成。先强制一次布局，再去找按钮。
        if (DzCmpGrid.ActualHeight <= 0 || ActualWidth <= 0)
        {
            var w = Math.Max(1, Parent is FrameworkElement pe && pe.ActualWidth > 0 ? pe.ActualWidth : 1400);
            var h = Math.Max(1, Parent is FrameworkElement pe2 && pe2.ActualHeight > 0 ? pe2.ActualHeight : 760);
            Measure(new Size(w, h));
            Arrange(new Rect(0, 0, w, h));
            UpdateLayout();
        }
        DzCmpGrid.ScrollIntoView(vm);
        DzCmpGrid.UpdateLayout();

        var row = DzCmpGrid.ItemContainerGenerator.ContainerFromItem(vm) as DataGridRow;
        if (row == null)
        {
            // 虚拟化没生成行：直接调业务方法（与点击按钮走的是同一条 OnGuideLink 逻辑）
            OnGuideLink(new Button { Tag = vm.NewName }, new RoutedEventArgs());
            return vm.NewName;
        }

        // 行内的按钮按 Content 文本区分：只有「立项指南」那一列会带 GuideText
        var btn = Descendants<Button>(row).FirstOrDefault(b => b.Content is string s &&
            (s == "立项指南" || s == "无立项指南"));
        if (btn == null)
        {
            OnGuideLink(new Button { Tag = vm.NewName }, new RoutedEventArgs());
            return vm.NewName;
        }

        btn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        return vm.NewName;
    }

    /// <summary>视觉树里找指定类型的后代（模板列内部的按钮用）。</summary>
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var x in Descendants<T>(c)) yield return x;
        }
    }

    /// <summary>
    /// 自检 / 探针专用：模拟用户点「口径」下拉（设置选中项 → 走 OnCmpLevelChanged 全套）。
    /// <para>直接调 <see cref="DiagCompareLevel"/> 会连范围一起重置、顺手重取一次数，
    /// 那测的是「切视图」而不是「换档」，验不出「换档不重建行」这条。</para>
    /// </summary>
    internal void PickCmpLevel(string level)
    {
        for (var i = 0; i < CmpLevelSel.Items.Count; i++)
            if ((CmpLevelSel.Items[i] as ComboBoxItem)?.Tag as string == level)
            {
                CmpLevelSel.SelectedIndex = i;
                break;
            }
        // 离屏 / 自检环境下 ComboBox 的 SelectionChanged 可能不派发：显式跑一遍换档逻辑
        if (_cmpLevel != level)
        {
            _cmpLevel = level;
            ApplyCmpLevel();
        }
    }

    /// <summary>进度文案按视图给出「要等什么」，比单纯的「加载中」更有信息量。</summary>
    private static string BusyLabel(ViewKind view) => view switch
    {
        ViewKind.Cancel => "正在汇总各批次作废对照…",
        ViewKind.Legacy => "正在载入旧版物价对码表…",
        ViewKind.Cmp => "正在比对现行与旧版价格…",
        _ => "正在载入现行项目…",
    };

    /// <summary>第二行补充「在做什么」，让等待有具体落点（重建下拉 / 重排表格 / 逐条配对）。</summary>
    private static string BusyHint(ViewKind view) => view switch
    {
        ViewKind.Cancel => "合并作废批次与替代项目",
        ViewKind.Legacy => "整理状态与对应新编码",
        ViewKind.Cmp => "逐条配对差额与涨跌幅",
        _ => "重建批次与类别筛选",
    };

    /// <summary>诊断专用（--shot-page dzbusy[:Cmp][:spill]）：把切换遮罩定格在截图上，核对版式。</summary>
    internal void DiagBusy(string which)
    {
        EnsureLoaded();
        var w = (which ?? "").ToLowerInvariant();
        var spill = w.Contains("spill");
        var view = w.StartsWith("cancel") ? ViewKind.Cancel
            : w.StartsWith("legacy") ? ViewKind.Legacy
            : w.StartsWith("cmp") ? ViewKind.Cmp
            : ViewKind.Live;
        Busy.DiagFreeze(BusyLabel(view), BusyHint(view), spill);
    }

    /// <summary>切到指定视图并复位筛选：segment 勾选 / 表格与筛选条显隐 / 下拉重建 / 重新加载。</summary>
    private void ApplyView(ViewKind view) => ApplyView(view, null);

    /// <summary>
    /// 切视图。<paramref name="back"/> 非空时不复位筛选，而是按快照把类别 / 状态 / 比对范围 /
    /// 价格口径与页码一起还原 —— 这是「← 返回」原路退回的实现。
    /// </summary>
    private void ApplyView(ViewKind view, BackState back)
    {
        _view = view;
        _building = true;   // 回写 segment 勾选时不再触发 OnViewChanged
        SegCancel.IsChecked = view == ViewKind.Cancel;
        SegLegacy.IsChecked = view == ViewKind.Legacy;
        SegCmp.IsChecked = view == ViewKind.Cmp;
        SegLive.IsChecked = view == ViewKind.Live;
        _building = false;

        DzGrid.Visibility = view == ViewKind.Live ? Visibility.Visible : Visibility.Collapsed;
        DzCancelGrid.Visibility = view == ViewKind.Cancel ? Visibility.Visible : Visibility.Collapsed;
        DzLegacyGrid.Visibility = view == ViewKind.Legacy ? Visibility.Visible : Visibility.Collapsed;
        DzCmpGrid.Visibility = view == ViewKind.Cmp ? Visibility.Visible : Visibility.Collapsed;
        var liveOnly = view == ViewKind.Live ? Visibility.Visible : Visibility.Collapsed;
        KindSel.Visibility = liveOnly;      // 项目性质只有现行项目表有
        InsSel.Visibility = liveOnly;       // 医保筛选同（旧版表格里展示但不做下拉筛选）
        LevelSel.Visibility = liveOnly;     // 医院级别只影响现行项目的价格列
        FavOnly.Visibility = liveOnly;      // 收藏只挂在现行项目上
        // 导出：现行项目与新旧比对两个视图可用；作废对照 / 旧版物价整块收起（只留状态文字）。
        // 两个控件各自隐藏会把操作行留一条空槽，所以只动整块。
        ExportBar.Visibility = view is ViewKind.Live or ViewKind.Cmp
            ? Visibility.Visible : Visibility.Collapsed;
        BuildExpScopes(view);   // 范围下拉随视图换口径（比对视图没有「仅收藏」）
        StatusSel.Visibility = view == ViewKind.Legacy ? Visibility.Visible : Visibility.Collapsed;
        CmpSel.Visibility = view == ViewKind.Cmp ? Visibility.Visible : Visibility.Collapsed;
        CmpLevelSel.Visibility = view == ViewKind.Cmp ? Visibility.Visible : Visibility.Collapsed;
        DocSel.Visibility = view == ViewKind.Live || view == ViewKind.Cancel
            ? Visibility.Visible : Visibility.Collapsed;   // 旧版 / 比对视图无批次维度

        // 切视图重建批次 / 类别下拉（各组数据的批次行数与类别清单不同）。
        // 正常切换一律复位筛选；返回态则把快照里的筛选原样放回（下拉重建后再选中，顺序不能反）。
        _doc = "";
        _cat = back?.Cat ?? "";
        _kind = "";
        _ins = "";
        _status = back?.Status ?? "";
        _cmpScope = back?.CmpScope ?? "";
        if (!string.IsNullOrEmpty(back?.CmpLevel)) _cmpLevel = back.CmpLevel;
        _usageFocusDoc = _usageFocusCat = null;   // 切视图退出面板定位态
        BuildDocs();
        BuildCats();
        _building = true;
        StatusSel.SelectedIndex = 0;
        CmpSel.SelectedIndex = 0;
        if (back != null)
        {
            // 数据可能变了：快照里的类别 / 状态在重建后的下拉里找不到就退回「全部」，否则会查出空列表
            if (!SelectTag(CatSel, _cat)) _cat = "";
            if (!SelectTag(StatusSel, _status)) _status = "";
            if (!SelectTag(CmpSel, _cmpScope)) _cmpScope = "";
            if (!SelectTag(CmpLevelSel, _cmpLevel)) _cmpLevel = "3";
        }
        _building = false;
        UpdateUsagePanel();
        UpdateStat();
        Load(back?.Page ?? 1);
    }

    /// <summary>
    /// 从「现行标准 / 对应新编码」链接跳到「现行项目」视图并按编码检索。
    /// 关键词走通用查询框（编码前缀匹配），新项目行随即置顶；
    /// 同时记住跳转前的整份现场，筛选条上出现「← 返回」按钮（点分段切换 / 清空则取消返回态）。
    /// </summary>
    private void GoToLive(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        if (_view != ViewKind.Live) _back = Snapshot();
        DzQuery.Text = code.Trim();
        ApplyView(ViewKind.Live);
        UpdateBackBtn();
        DzStatus.Text = $"已切换到「现行项目」，按编码「{code.Trim()}」检索，点「← 返回」可回到原视图";
    }

    /// <summary>
    /// 从「新旧比对」的旧项目编码跳到「旧版物价」并按旧编码检索。
    /// <para>与 <see cref="GoToLive"/> 对称：新项目编码去现行项目，旧项目编码去旧版物价，
    /// 用户在一行里就能把「新的长什么样 / 旧的原来什么样」对完，不用记住编码再切页签重搜。</para>
    /// </summary>
    private void GoToLegacy(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        if (_view != ViewKind.Legacy) _back = Snapshot();
        DzQuery.Text = code.Trim();
        ApplyView(ViewKind.Legacy);
        UpdateBackBtn();
        DzStatus.Text = $"已切换到「旧版物价」，按编码「{code.Trim()}」检索，点「← 返回」可回到原视图";
    }

    /// <summary>记下当前视图 / 检索词 / 页码 / 各筛选，供「← 返回」原样恢复。</summary>
    private BackState Snapshot() => new()
    {
        View = _view,
        Query = DzQuery.Text ?? "",
        Page = _page,
        Cat = _cat,
        Status = _status,
        CmpScope = _cmpScope,
        CmpLevel = _cmpLevel,
    };

    private static string ViewName(ViewKind v) => v switch
    {
        ViewKind.Cancel => "作废对照",
        ViewKind.Legacy => "旧版物价",
        ViewKind.Cmp => "新旧比对",
        _ => "现行项目",
    };

    /// <summary>按 Tag 选中下拉项；返回是否命中（未命中时调用方要把对应筛选字段清空，否则会查出空列表）。</summary>
    private static bool SelectTag(ComboBox cb, string tag)
    {
        for (var i = 0; i < cb.Items.Count; i++)
        {
            if (((cb.Items[i] as ComboBoxItem)?.Tag as string ?? "") != (tag ?? "")) continue;
            cb.SelectedIndex = i;
            return true;
        }
        cb.SelectedIndex = 0;
        return false;
    }

    /// <summary>返回按钮文案 / 可见性跟随跳转来源。</summary>
    private void UpdateBackBtn()
    {
        BackBtn.Visibility = _back != null ? Visibility.Visible : Visibility.Collapsed;
        BackBtn.Content = _back == null ? "← 返回" : _back.View switch
        {
            ViewKind.Cmp => "← 返回新旧比对",
            ViewKind.Legacy => "← 返回旧版物价",
            ViewKind.Cancel => "← 返回作废对照",
            _ => "← 返回",
        };
    }

    /// <summary>「← 返回」：回到跳转前的视图、检索词、页码与筛选。</summary>
    private void OnBack(object sender, RoutedEventArgs e) => GoBack();

    private void GoBack()
    {
        if (_back == null) return;
        var b = _back;
        _back = null;
        DzQuery.Text = b.Query;
        ApplyView(b.View, b);
        UpdateBackBtn();
        DzStatus.Text = $"已返回「{ViewName(b.View)}」第 {b.Page} 页";
    }

    /// <summary>现行标准 / 对应新编码链接点击：Tag 携带新项目编码。</summary>
    private void OnStdLink(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        GoToLive(b.Tag as string ?? "");
    }

    /// <summary>「新旧比对」新项目编码链接点击：Tag 携带新编码。</summary>
    private void OnCmpNewLink(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        GoToLive(b.Tag as string ?? "");
    }

    /// <summary>「新旧比对」旧项目编码链接点击：Tag 携带旧编码。</summary>
    private void OnCmpOldLink(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        GoToLegacy(b.Tag as string ?? "");
    }

    private void OnDocChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_building || !_loaded) return;
        _doc = (DocSel.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        BuildCats();
        _cat = "";
        _usageFocusDoc = _usageFocusCat = null;   // 手动筛选退出面板定位态
        UpdateUsagePanel();
        Load(1);
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_building || !_loaded) return;
        _cat = (CatSel.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        _kind = (KindSel.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        _ins = (InsSel.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        _usageFocusDoc = _usageFocusCat = null;   // 手动筛选退出面板定位态
        UpdateUsagePanel();
        RunBusy("正在按条件筛选…", FilterBusyHint(), () => Load(1));
    }

    /// <summary>比对范围切换（仅新旧比对视图的下拉）。</summary>
    private void OnCmpChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_building || !_loaded) return;
        _cmpScope = (CmpSel.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        RunBusy("正在按比对范围取数…", "配对旧价并计算差额", () => Load(1));
    }

    /// <summary>筛选遮罩的第二行：说清「按哪几个条件在筛」，比一句「加载中」有信息量。</summary>
    private string FilterBusyHint()
    {
        var parts = new List<string>();
        if (DocSel.SelectedIndex > 0) parts.Add($"文号 {(DocSel.SelectedItem as ComboBoxItem)?.Content}");
        if (!string.IsNullOrEmpty(_cat)) parts.Add($"类别 {_cat}");
        if (!string.IsNullOrEmpty(_kind)) parts.Add(_kind);
        if (!string.IsNullOrEmpty(_status)) parts.Add(_status);
        return parts.Count == 0 ? "复位筛选并取全部记录" : string.Join(" · ", parts);
    }

    /// <summary>键入即防抖检索（拼音全拼 / 首字母同样即时生效）</summary>
    private void OnQueryText(object sender, TextChangedEventArgs e)
    {
        if (_building || !_loaded) return;
        _debounce.Poke();
    }

    private void OnSearch(object sender, RoutedEventArgs e)
    {
        _debounce.Cancel();
        RunBusy("正在检索物价项目…", SearchBusyHint(), () => Load(1));
    }

    /// <summary>检索遮罩的第二行：区分「关键词命中」与「无条件全量」。</summary>
    private string SearchBusyHint()
    {
        var q = DzQuery.Text?.Trim() ?? "";
        return q.Length == 0 ? "未填关键词，取全部记录" : $"关键词「{q}」· 名称 / 编码 / 拼音";
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _debounce.Cancel();
        RunBusy("正在清空筛选…", "复位 8 个筛选条件并重取列表", () =>
        {
            _building = true;                 // 复位各下拉时不触发多次检索
            DzQuery.Text = "";
            _doc = ""; _cat = ""; _kind = ""; _ins = ""; _level = ""; _status = ""; _cmpScope = ""; _favOnly = false;
            _usageFocusDoc = _usageFocusCat = null;   // 清空同时退出面板定位态
            _back = null;                     // 清空同时退出跳转返回态
            UpdateBackBtn();
            BuildCats();
            BuildKinds();
            BuildInsurance();
            BuildLevels();
            BuildStatuses();
            DocSel.SelectedIndex = 0;
            CatSel.SelectedIndex = 0;
            KindSel.SelectedIndex = 0;
            InsSel.SelectedIndex = 0;
            LevelSel.SelectedIndex = 0;
            StatusSel.SelectedIndex = 0;
            CmpSel.SelectedIndex = 0;
            FavOnly.IsChecked = false;
            _building = false;
            DzStatus.Text = "";
            Load(1);
            UpdateUsagePanel();
            DzQuery.Focus();
        });
    }

    private void OnQueryKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _debounce.Cancel();
        RunBusy("正在检索物价项目…", SearchBusyHint(), () => Load(1));
    }

    private void OnPrev(object sender, RoutedEventArgs e) => PageTo(_page - 1, "上一页");

    private void OnNext(object sender, RoutedEventArgs e) => PageTo(_page + 1, "下一页");

    /// <summary>跳页：只接受正整数，越界由 Load 内部钳到最后一页；非数字还原成当前页</summary>
    private void OnJumpKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        JumpToTyped();
    }

    private void JumpToTyped()
    {
        if (int.TryParse((DzJump.Text ?? "").Trim(), out var p) && p > 0)
            PageTo(p, $"跳至第 {p} 页");
        else
            DzJump.Text = _page.ToString();
    }

    /// <summary>每页条数：改完回到第 1 页（停在第 5 页会跳到另一批数据，用户会以为筛选错了）</summary>
    private void OnPageSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_building) return;
        var tag = (DzPageSize.SelectedItem as ComboBoxItem)?.Tag as string;
        if (!int.TryParse(tag, out var n) || n <= 0 || n == _pageSize) return;
        _pageSize = n;
        Load(1);
    }

    /// <summary>把当前页码写回跳页框（翻页 / 筛选 / 改每页条数后都要同步）</summary>
    private void SyncJump() => DzJump.Text = _page.ToString();

    private void BuildPageSizes()
    {
        _building = true;
        DzPageSize.Items.Clear();
        foreach (var n in new[] { 50, 100, 200 })
            DzPageSize.Items.Add(new ComboBoxItem
            {
                Content = n + " 条",
                Tag = n.ToString(),
                IsSelected = n == _pageSize,
            });
        _building = false;
    }

    /// <summary>翻页：走同一套遮罩，文案带出目标页码，用户知道「翻到第几页了」。</summary>
    private void PageTo(int page, string dir)
    {
        var target = Math.Max(1, page);
        RunBusy($"正在翻到第 {target} 页…", $"{dir} · 逐行重排表格", () => Load(page));
    }

    /// <summary>粗估本次取数要等多久（ms）。只按记录量与视图类型粗略估，纯用于文案。</summary>
    private int EstimateLoadMs()
    {
        var n = App.State.Dezhou.Items.Count;
        // 比对视图要逐条配对旧价，比普通列多两趟；作废对照要合并批次
        var per = _view switch
        {
            ViewKind.Cmp => 0.028,
            ViewKind.Cancel => 0.020,
            ViewKind.Legacy => 0.018,
            _ => 0.012,
        };
        return (int)Math.Clamp(n * per, 160, 900);
    }

    /// <summary>
    /// 带进度遮罩执行一段「同步且会卡主线程」的取数/重建动作。
    ///
    /// <para>为什么不再各处直接调 <c>Load()</c>：筛选、检索、清空、翻页和分段切换走的是同一条
    /// <c>Load</c> 通路，都是纯同步的（重建下拉 → 重新筛选 → 重排 50 行）。只在分段切换上给反馈，
    /// 用户点「查询」或「下一页」时看到的仍然是「点完就静止」，同一个页面两套手感很割裂。</para>
    ///
    /// <para>顺序：先亮遮罩 → 把一帧让出去（<c>DispatcherPriority.Render</c> 排在所有渲染之后，
    /// 回调时遮罩已真正上屏）→ 再同步干活 → 至少停留 <see cref="BusyVeil.DefaultMinMs"/>。
    /// 让出的这一帧正是「用户能看见进度」的全部秘密：遮罩必须先画出来，后面的同步阻塞才有意义。</para>
    ///
    /// <para>进度条：先给一条按时长推进的定量动画（<see cref="BusyVeil.FillEstimate"/>），
    /// 预估时长走完还没好的话自动切成往复的不定量 —— 定量段告诉用户「大概这么久」，
    /// 超时后不定量段告诉用户「还在干，别急」。</para>
    /// </summary>
    private void RunBusy(string caption, string hint, Action work)
    {
        Busy.Show(caption, hint, EstimateLoadMs());
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { work(); }
            finally { Busy.HideAfter(); }
        }), DispatcherPriority.Render);
    }

    private void Load(int page)
    {
        _page = Math.Max(1, page);
        var q = DzQuery.Text?.Trim() ?? "";
        if (_view == ViewKind.Cancel)
        {
            LoadCancel(_page, q);
            SyncJump();
            return;
        }
        if (_view == ViewKind.Legacy)
        {
            LoadLegacy(_page, q);
            SyncJump();
            return;
        }
        if (_view == ViewKind.Cmp)
        {
            LoadCmp(_page, q);
            SyncJump();
            return;
        }
        try
        {
            var favSet = _favOnly ? App.State.DezhouFav.CodeSet() : null;
            var r = App.State.Dezhou.ListRows(_doc, _cat, _kind, q, _page, _pageSize, _ins,
                favSet == null ? null : it => favSet.Contains(it.Code), _level);

            // 取消收藏 / 改筛选后当前页可能已空：自动回退一页（最多一次，见下方 return）
            if (r.Items.Count == 0 && r.Total > 0 && _page > 1)
            {
                Load(_page - 1);
                return;
            }

            DzGrid.ItemsSource = r.Items.Select(ToVm).ToList();
            _pageItems = r.Items;
            SyncDeductCol(r.Items);

            var pages = Math.Max(1, (int)Math.Ceiling(r.Total / (double)r.PageSize));
            var label = r.Label + (_favOnly ? " · 仅收藏" : "");
            DzPageInfo.Text = $"{label} · 第 {r.Page}/{pages} 页 · 共 {r.Total:N0} 条";
            DzPrev.IsEnabled = r.Page > 1;
            DzNext.IsEnabled = r.Page < pages;
            SyncJump();

            DzStatus.Text = App.State.Dezhou.Items.Count == 0
                ? "未导入 data/dezhou-price.json"
                : r.Total == 0 ? (_favOnly ? "还没有收藏的项目（点行首 ★ 收藏）" : "无匹配记录") : "";
        }
        catch (Exception ex)
        {
            DzGrid.ItemsSource = null;
            _pageItems = new List<DezhouPriceItem>();
            DzPageInfo.Text = "";
            DzStatus.Text = "加载失败：" + ex.Message;
        }
    }

    /// <summary>作废对照视图加载（与现行项目共用筛选条与分页，数据源不同）。</summary>
    private void LoadCancel(int page, string q)
    {
        try
        {
            var all = App.State.Dezhou.CancelRows(_doc, _cat, q);
            var total = all.Count;
            var rows = all.Skip((page - 1) * _pageSize).Take(_pageSize).ToList();

            if (rows.Count == 0 && total > 0 && page > 1)
            {
                Load(page - 1);
                return;
            }

            DzCancelGrid.ItemsSource = rows.Select(ToCancelVm).ToList();

            var pages = Math.Max(1, (int)Math.Ceiling(total / (double)_pageSize));
            var label = (_doc.Length > 0 ? _doc : "全部批次")
                        + (_cat.Length > 0 ? " · " + _cat : "")
                        + " · 作废对照";
            DzPageInfo.Text = $"{label} · 第 {page}/{pages} 页 · 共 {total:N0} 条";
            DzPrev.IsEnabled = page > 1;
            DzNext.IsEnabled = page < pages;

            DzStatus.Text = App.State.Dezhou.Cancels.Count == 0
                ? "当前数据未含作废对照（到「后台管理 → 数据更新」拉取新数据包）"
                : total == 0 ? "无匹配记录" : "";
        }
        catch (Exception ex)
        {
            DzCancelGrid.ItemsSource = null;
            DzPageInfo.Text = "";
            DzStatus.Text = "加载失败：" + ex.Message;
        }
    }

    private DzCancelRowVm ToCancelVm(DezhouCancelItem it) => new()
    {
        Doc = it.Doc,
        Exec = it.Exec,
        Category = it.Category,
        OldCode = it.OldCode,
        OldName = Flatten(it.OldName),
        NewCode = it.NewCode,
        NewName = Flatten(it.NewName),
        Note = Flatten(it.Note),
        IsKeep = it.Keep,
    };

    /// <summary>旧版物价视图加载（45号文对码表；状态/现行标准与作废对照联查）。</summary>
    private void LoadLegacy(int page, string q)
    {
        try
        {
            var all = App.State.Dezhou.LegacyRows(_cat, _status, q);
            var total = all.Count;
            var rows = all.Skip((page - 1) * _pageSize).Take(_pageSize).ToList();

            if (rows.Count == 0 && total > 0 && page > 1)
            {
                Load(page - 1);
                return;
            }

            DzLegacyGrid.ItemsSource = rows.Select(ToLegacyVm).ToList();

            var pages = Math.Max(1, (int)Math.Ceiling(total / (double)_pageSize));
            var label = "旧版物价（德医保发〔2024〕45号 · 241230）"
                        + (_cat.Length > 0 ? " · " + _cat : "")
                        + (_status == "dead" ? " · 仅已作废" : _status == "live" ? " · 仅现行" : "");
            DzPageInfo.Text = $"{label} · 第 {page}/{pages} 页 · 共 {total:N0} 条";
            DzPrev.IsEnabled = page > 1;
            DzNext.IsEnabled = page < pages;

            DzStatus.Text = App.State.Dezhou.Legacy.Count == 0
                ? "当前数据未含旧版物价（到「后台管理 → 数据更新」拉取新数据包）"
                : total == 0 ? "无匹配记录" : "";
        }
        catch (Exception ex)
        {
            DzLegacyGrid.ItemsSource = null;
            DzPageInfo.Text = "";
            DzStatus.Text = "加载失败：" + ex.Message;
        }
    }

    /// <summary>新旧比对视图加载（现行 ↔ 旧版物价；差额涨幅已由数据层算好，这里只做筛选与分页）。</summary>
    private void LoadCmp(int page, string q)
    {
        try
        {
            var all = App.State.Dezhou.ListCompare(_cat, q, _cmpScope);
            var total = all.Count;
            var rows = all.Skip((page - 1) * _pageSize).Take(_pageSize).Select(ToCmpVm).ToList();

            if (rows.Count == 0 && total > 0 && page > 1)
            {
                Load(page - 1);
                return;
            }

            DzCmpGrid.ItemsSource = rows;

            var pages = Math.Max(1, (int)Math.Ceiling(total / (double)_pageSize));
            var label = "新旧比对（现行 ↔ 旧版物价 · 三级价）"
                        + (_cat.Length > 0 ? " · " + _cat : "")
                        + (_cmpScope switch
                        {
                            "priced" => " · 有旧价可比",
                            "cancel" => " · 仅作废对照",
                            "none" => " · 纯新增",
                            _ => "",
                        });
            DzPageInfo.Text = $"{label} · 第 {page}/{pages} 页 · 共 {total:N0} 条";
            DzPrev.IsEnabled = page > 1;
            DzNext.IsEnabled = page < pages;

            DzStatus.Text = App.State.Dezhou.Legacy.Count == 0
                ? "当前数据未含旧版物价（无法比价，到「后台管理 → 数据更新」拉取新数据包）"
                : total == 0 ? "无匹配记录" : "";
        }
        catch (Exception ex)
        {
            DzCmpGrid.ItemsSource = null;
            DzPageInfo.Text = "";
            DzStatus.Text = "加载失败：" + ex.Message;
        }
    }

    private DzCompareRowVm ToCmpVm(DezhouDraftCompareRow r)
    {
        // 立项指南命中与否决定「立项指南」那一列可点还是置灰：这里只查一次索引（有缓存），
        // 50 行一页可忽略不计；放到链接点击时再查的话，用户看到的是一个点下去没反应的链接。
        var guide = App.State.Lixiang.FindGuide(r.NewName);
        return new DzCompareRowVm
        {
            NewCode = r.NewCode,
            NewName = Flatten(r.NewName),
            Category = r.Category,
            Kind = r.NewKind,
            NewPrice3 = r.NewPrice3,
            NewPrice2 = r.NewPrice2,
            NewPrice1 = r.NewPrice1,
            OldCode = r.OldCode,
            OldName = Flatten(r.OldName),
            OldPrice3 = r.OldPrice3,
            OldPrice2 = r.OldPrice2,
            OldPrice1 = r.OldPrice1,
            Delta3 = r.Delta3,
            Ratio3 = r.Ratio3,
            Delta2 = r.Delta2,
            Ratio2 = r.Ratio2,
            Delta1 = r.Delta1,
            Ratio1 = r.Ratio1,
            Source = r.OldSource,
            Level = _cmpLevel,
            HasGuide = guide != null,
            GuideTip = guide == null ? "立项指南中未收录该项目" : LixiangStore.GuideTip(guide.Value.Row),
        };
    }

    private DzLegacyRowVm ToLegacyVm(DezhouLegacyItem it)
    {
        // 整行判定（含同族补判）：映射关系表引省对码，与本地对码表的后缀可能错位
        var dead = App.State.Dezhou.LegacyCanceled(it);
        // 现行标准明细：每条对应的新项目做成可点击链接（跳现行项目视图）
        var links = dead
            ? App.State.Dezhou.CancelOf(it)
                .Select(c => new DzLinkVm
                {
                    Code = c.NewCode,
                    Text = $"{c.NewCode} {Flatten(c.NewName)}",
                })
                .ToList()
            : new List<DzLinkVm>();
        return new DzLegacyRowVm
        {
            Code = it.Code,
            Name = Flatten(it.Name),
            Category = it.Category,
            Unit = it.Unit,
            Price3 = it.Price3,
            Price2 = it.Price2,
            Price1 = it.Price1,
            Insurance = it.Insurance,
            SelfPay = it.SelfPay,
            Status = dead ? "已作废" : "现行",
            IsDead = dead,
            CurrentStd = dead ? App.State.Dezhou.LegacyCurrentStd(it) : "",
            Links = links,
            DeadNoStd = dead && links.Count == 0,
            NewMark = it.IsNew == "是" ? "新" : "",
        };
    }

    // ==================================================================
    // 导出（当前筛选结果 / 当前页 / 仅收藏）
    // ==================================================================

    private void BuildExpScopes(ViewKind view)
    {
        _building = true;
        ExpScope.Items.Clear();
        if (view == ViewKind.Cmp)
        {
            ExpScope.Items.Add(new ComboBoxItem { Content = "导出：全部比对结果", Tag = "all" });
            ExpScope.Items.Add(new ComboBoxItem { Content = "导出：仅当前页", Tag = "page" });
        }
        else
        {
            ExpScope.Items.Add(new ComboBoxItem { Content = "导出：全部结果", Tag = "all" });
            ExpScope.Items.Add(new ComboBoxItem { Content = "导出：仅当前页", Tag = "page" });
            ExpScope.Items.Add(new ComboBoxItem { Content = "导出：仅收藏", Tag = "fav" });
        }
        ExpScope.SelectedIndex = 0;
        _building = false;
    }

    /// <summary>
    /// 按导出范围取数：all = 当前筛选（含医保 / 收藏开关）命中的全部，page = 本页 50 条，fav = 仅收藏。
    /// 抽出来是为了让自检能直接对账条数，而不必真的弹保存对话框。
    /// </summary>
    internal (List<DezhouPriceItem> Items, string Label) ExportScope(string tag)
    {
        if (tag == "page") return (_pageItems.ToList(), $"第{_page}页");

        var q = DzQuery.Text?.Trim() ?? "";
        var favSet = App.State.DezhouFav.CodeSet();
        var list = App.State.Dezhou.Items
            .Where(i => DezhouPriceStore.Matches(i, _doc, _cat, _kind, q, _ins, _level))
            .Where(i => tag != "fav" || favSet.Contains(i.Code))
            .ToList();
        return (list, tag == "fav" ? "仅收藏" : "全部结果");
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var tag = (ExpScope.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        if (_view == ViewKind.Cmp) { OnExportCmp(tag); return; }

        var (items, label) = ExportScope(tag);
        if (items.Count == 0)
        {
            DzStatus.Text = tag == "fav" ? "还没有收藏的项目，无可导出" : "当前条件下没有记录可导出";
            return;
        }

        var (bytes, defaultName) = DezhouExport.Front(App.State, items, label);
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出德州物价",
            FileName = defaultName,
            Filter = "Excel 文件|*.xlsx",
            DefaultExt = ".xlsx",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            File.WriteAllBytes(dlg.FileName, bytes);
            DzStatus.Text = $"已导出 {items.Count:N0} 条（{label}）→ {Path.GetFileName(dlg.FileName)}";
            RowMenu.Flash($"已导出 {items.Count:N0} 条");
        }
        catch (Exception ex)
        {
            DzStatus.Text = "导出失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 新旧比对视图导出：与界面同源走 <see cref="DezhouPriceStore.ListCompare"/>（同筛选、同排序源），
    /// 「仅当前页」按当前页码取同一窗口 —— 保证导出的就是用户看到的那些行。
    /// </summary>
    private void OnExportCmp(string tag)
    {
        var q = DzQuery.Text?.Trim() ?? "";
        var all = App.State.Dezhou.ListCompare(_cat, q, _cmpScope);
        if (all.Count == 0)
        {
            DzStatus.Text = "当前条件下没有比对记录可导出";
            return;
        }
        IEnumerable<DezhouDraftCompareRow> rows = all;
        var label = _cmpScope switch
        {
            "priced" => "有旧价可比",
            "cancel" => "仅作废对照",
            "none" => "纯新增",
            _ => "全部",
        };
        if (tag == "page")
        {
            rows = all.Skip((_page - 1) * _pageSize).Take(_pageSize);
            label = $"第{_page}页";
        }
        var list = rows.ToList();

        var (bytes, defaultName) = DezhouExport.Cmp(App.State, list, label);
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出新旧比对",
            FileName = defaultName,
            Filter = "Excel 文件|*.xlsx",
            DefaultExt = ".xlsx",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            File.WriteAllBytes(dlg.FileName, bytes);
            DzStatus.Text = $"已导出比对 {list.Count:N0} 条（{label}）→ {Path.GetFileName(dlg.FileName)}";
            RowMenu.Flash($"已导出比对 {list.Count:N0} 条");
        }
        catch (Exception ex)
        {
            DzStatus.Text = "导出失败：" + ex.Message;
        }
    }

    private DzRowVm ToVm(DezhouPriceItem it)
    {
        // 加收项原表价格是 Excel 公式（ROUND(主项*0.2,0)）：这里换算成金额再上表格，
        // 悬停给出「主项 × 比例 → 金额」的来历（与草稿视图同一套口径）。
        var store = App.State.Dezhou;
        var (p3, p2, p1) = store.ResolvePrices(it);
        return new DzRowVm
        {
            Seq = it.Seq,
            Code = it.Code,
            Name = Flatten(it.Name),
            Category = it.Category,
            Unit = it.Unit,
            Price3 = p3,
            Price2 = p2,
            Price1 = p1,
            Deduct = it.Deduct,
            Price3Tip = store.PriceNote(it, "price3") ?? Nz(p3),
            Price2Tip = store.PriceNote(it, "price2") ?? Nz(p2),
            Price1Tip = store.PriceNote(it, "price1") ?? Nz(p1),
            Insurance = it.Insurance,
            SelfPay = it.SelfPay,
            Doc = it.Doc,
            DocShort = ShortDoc(it.Doc),
            DocTip = $"{it.Doc}　执行日期 {it.Exec}",
            Kind = it.Kind,
            IsFav = App.State.DezhouFav.Has(it.Code),
            FavToggled = OnVmFavToggled,
            BlocksFactory = () => BuildBlocks(it, FocusUsage),
        };
    }

    private static string Nz(string v) => string.IsNullOrWhiteSpace(v) ? "—" : v;

    /// <summary>星标或右键菜单改收藏态后：刷新收藏计数，必要时（仅看收藏下取消）重载当前页</summary>
    private void OnVmFavToggled(DzRowVm vm, bool now)
    {
        UpdateFavLabel();
        if (_favOnly && !now) Load(_page);
    }

    /// <summary>「德医保发〔2025〕33号」→「〔2025〕33号」（列头已写明是落地文号，省 4 个字给名称列）</summary>
    internal static string ShortDoc(string doc)
    {
        var v = (doc ?? "").Trim();
        var i = v.IndexOf('〔');
        return i > 0 ? v[i..] : v;
    }

    private static string Flatten(string v) => Regex.Replace(v ?? "", @"\n+", "；");

    /// <summary>
    /// 详情字段块（末段为跨库关联块）。（关联弹窗复用：不传 <paramref name="focusUsage"/> 时
    /// 「查看全部」提示退化为纯文本；前台传入后渲染为可点击链接，定位到面板）
    /// </summary>
    internal static List<LxBlock> BuildBlocks(DezhouPriceItem it, Action<string, string> focusUsage = null)
    {
        var blocks = new List<LxBlock>();
        void Add(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            blocks.Add(new LxBlock
            {
                Label = label,
                Lines = new List<LxLine> { new() { Text = Flatten(value) } },
            });
        }
        Add("落地文件", $"{it.Doc}　执行日期 {it.Exec}");
        Add("服务产出", it.ServiceOutput);
        Add("价格构成", it.PriceComposition);

        // 加收项：原表价格列是 Excel 公式（ROUND(主项*0.2,0)），表格里已换算成金额，
        // 详情卡把「哪来的这个价」讲清楚，免得物价人员以为价格被改过。
        var formulaNote = App.State.Dezhou.PriceNote(it, "price3");
        if (formulaNote != null)
        {
            var (f3, f2, f1) = App.State.Dezhou.ResolvePrices(it);
            blocks.Add(new LxBlock
            {
                Label = "价格换算（加收项）",
                Lines = new List<LxLine>
                {
                    new() { Text = formulaNote },
                    new() { Text = $"同比例换算：二级 {Nz(f2)} · 一级 {Nz(f1)}（原表为 Excel 公式，界面已自动折算）" },
                },
            });
        }

        Add("限定支付范围", it.Note);
        // 「扣减基本物耗价格」只有一般治疗类有值（注射 / 清创缝合 / 换药 / 导尿 / 膀胱冲洗）：
        // 指该项目价格中可另行收费的基本物耗金额，收费时须从项目价中扣减，别让物价人员漏看。
        if (!string.IsNullOrWhiteSpace(it.Deduct))
            Add("扣减基本物耗价格", $"{it.Deduct} 元　（本项目价格中含可另行收费的基本物耗，收费时按此金额扣减）");
        Add("计价说明", it.Usage);
        Add("医保支付", string.IsNullOrWhiteSpace(it.Insurance)
            ? ""
            : $"{it.Insurance}　自付比例 {it.SelfPay}");

        // 该类别的「使用说明」摘录（类别级通用规则，详情卡只放前 2 条；全文见页面上方「使用说明」面板）
        var cat = App.State.Dezhou.UsageOf(it.Doc, it.Category);
        if (cat.Count > 0)
        {
            var take = cat.Take(2).Select((t, i) => new LxLine { Text = $"{i + 1}. {t}" }).ToList();
            if (cat.Count > 2)
            {
                var hint = new LxLine { Text = $"…… 本类使用说明共 {cat.Count} 条 · 点此定位查看全部 ▸" };
                if (focusUsage != null) hint.Click = () => focusUsage(it.Doc, it.Category);
                take.Add(hint);
            }
            blocks.Add(new LxBlock { Label = $"{it.Category} · 使用说明", Lines = take });
        }

        blocks.AddRange(CrossMap.ForDezhou(it.Name));
        return blocks;
    }

    private void BuildColumns()
    {
        var mono = (FontFamily)FindResource("Font.Mono");
        var baseCell = (Style)FindResource("DgCell");

        // 收藏星标列（最前，固定窄列；模板见 XAML 的 FavCell）
        var favCell = new Style(typeof(DataGridCell), baseCell);
        favCell.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        favCell.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        DzGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "★",
            Width = new DataGridLength(FavColWidth),
            CellTemplate = (DataTemplate)FindResource("FavCell"),
            CellStyle = favCell,
            // 显式挂样式：留空会让这一列掉回系统默认表头模板（白底），与右侧灰蓝表头断开成两块
            HeaderStyle = (Style)FindResource("DgHeadCenter"),
        });

        foreach (var (prop, header, width, align, isMono) in Cols)
        {
            // 价格三列悬停走 *Tip：加收项显示「主项 × 比例 → 金额」，其余项等同于价格本身
            var tipProp = prop switch
            {
                "Price3" => "Price3Tip", "Price2" => "Price2Tip", "Price1" => "Price1Tip",
                "DocShort" => "DocTip", _ => "",
            };
            var col = MakeTextCol(prop, header, width, align, isMono, mono, baseCell, tipProp,
                wrap: false);
            if (width <= 0) _nameCol = col;
            DzGrid.Columns.Add(col);
            if (prop == "Price1")
            {
                // 「扣减物耗」条件列：插在一级价格之后（原表列序），默认折叠，显隐由 SyncDeductCol 决定
                _deductCol = MakeTextCol("Deduct", "扣减物耗", DeductColWidth, true, true, mono, baseCell, wrap: false);
                _deductCol.Visibility = Visibility.Collapsed;
                DzGrid.Columns.Add(_deductCol);
            }
        }
        FitNameColumn();
    }

    /// <summary>
    /// 「扣减物耗」列显隐：筛到「一般治疗」必显示（该类 11 条全有值）；
    /// 其余筛选下当前页出现带值行（如搜「换药费」）也显示，空场景保持折叠、不挤名称列。
    /// </summary>
    private void SyncDeductCol(IEnumerable<DezhouPriceItem> items)
    {
        if (_deductCol == null) return;
        var show = _cat == "一般治疗" || (items?.Any(x => !string.IsNullOrWhiteSpace(x.Deduct)) ?? false);
        _deductCol.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        FitNameColumn();     // 列显隐改变合计宽度，名称列要重新吃余量（三表禁横向滚动，超宽即被裁）
    }

    /// <summary>作废对照视图的列（无收藏列，布局同现行项目表）；「对应新编码」为链接模板列。</summary>
    private void BuildCancelColumns()
    {
        var mono = (FontFamily)FindResource("Font.Mono");
        var baseCell = (Style)FindResource("DgCell");
        foreach (var (prop, header, width, align, isMono) in CancelCols)
        {
            DataGridColumn col;
            if (prop == "NewCodeText")
                col = MakeLinkCol(header, width, baseCell, "NewLinks", "NewNameText",
                    disabledPath: "HasNewLink");
            else if (prop == "Batch")
                col = MakeTemplateCol(header, width, baseCell, "BatchCell");   // 两行：文号 / 执行日期
            else if (prop == "State")
            {
                // 状态着色：保留绿 / 作废红（与旧版物价「现行绿 / 已作废红」同色义，换肤跟随）
                col = MakeTextCol(prop, header, width, align, isMono, mono, baseCell,
                    tipProp: "StateTip", wrap: false);
                var st = ((DataGridTextColumn)col).ElementStyle;
                st.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
                st.Triggers.Add(new DataTrigger
                {
                    Binding = new Binding("IsKeep"),
                    Value = false,
                    Setters = { new Setter(TextBlock.ForegroundProperty, (Brush)FindResource("B.Danger")) },
                });
                st.Triggers.Add(new DataTrigger
                {
                    Binding = new Binding("IsKeep"),
                    Value = true,
                    Setters = { new Setter(TextBlock.ForegroundProperty, (Brush)FindResource("B.Ok")) },
                });
            }
            else
                col = MakeTextCol(prop, header, width, align, isMono, mono, baseCell,
                    wrap: false);
            if (width <= 0) _cancelNameCol = col;
            DzCancelGrid.Columns.Add(col);
        }
    }

    /// <summary>旧版物价视图的列；「状态」列按 IsDead 着色（已作废红 / 现行绿），跟随换肤；
    /// 「现行标准」列为链接模板列（点击跳现行项目视图）。</summary>
    private void BuildLegacyColumns()
    {
        var mono = (FontFamily)FindResource("Font.Mono");
        var baseCell = (Style)FindResource("DgCell");
        foreach (var (prop, header, width, align, isMono) in LegacyCols)
        {
            DataGridColumn col;
            if (prop == "CurrentStd")
            {
                col = MakeLinkCol(header, width, baseCell, "Links", "CurrentStd", disabledPath: null);
            }
            else
            {
                col = MakeTextCol(prop, header, width, align, isMono, mono, baseCell,
                    wrap: false);
                if (prop == "Status")
                {
                    // 状态着色：DataTrigger 挂在文本元素样式上（DynamicResource 取色，换肤跟随）
                    var st = ((DataGridTextColumn)col).ElementStyle;
                    st.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
                    st.Triggers.Add(new DataTrigger
                    {
                        Binding = new Binding("IsDead"),
                        Value = true,
                        Setters = { new Setter(TextBlock.ForegroundProperty, (Brush)FindResource("B.Danger")) },
                    });
                    st.Triggers.Add(new DataTrigger
                    {
                        Binding = new Binding("IsDead"),
                        Value = false,
                        Setters = { new Setter(TextBlock.ForegroundProperty, (Brush)FindResource("B.Ok")) },
                    });
                }
            }
            if (width <= 0) _legacyNameCol = col;
            DzLegacyGrid.Columns.Add(col);
        }
    }

    /// <summary>
    /// 新旧比对视图的列；「差额 / 涨幅」按正负着色（涨红跌绿，中国行情惯例）：
    /// DataTrigger 挂在文本元素样式上（DynamicResource 取色，换肤跟随）。
    /// </summary>
    private void BuildCompareColumns()
    {
        var mono = (FontFamily)FindResource("Font.Mono");
        var baseCell = (Style)FindResource("DgCell");
        foreach (var (prop, header, width, align, isMono) in CmpCols)
        {
            if (prop == "NewCodeText")
            {
                // 新项目编码 → 现行项目；旧项目编码 → 旧版物价。两侧都能点，才叫「新旧比对」
                DzCmpGrid.Columns.Add(MakeCodeLinkCol(header, width, baseCell, prop, "NewCode", "HasNewCode",
                    "点击查看该项目在「现行项目」中的完整价格与医保信息", OnCmpNewLink));
                continue;
            }
            if (prop == "OldCodeText")
            {
                DzCmpGrid.Columns.Add(MakeCodeLinkCol(header, width, baseCell, prop, "OldCode", "HasOldCode",
                    "点击查看该项目在「旧版物价」（45 号对码表）中的原始记录", OnCmpOldLink));
                continue;
            }

            var col = MakeTextCol(prop, header, width, align, isMono, mono, baseCell,
                wrap: false);
            if (width <= 0) _cmpNameCol = col;
            if (prop is "DeltaText" or "RatioText")
            {
                var st = ((DataGridTextColumn)col).ElementStyle;
                st.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
                st.Triggers.Add(new DataTrigger
                {
                    Binding = new Binding("IsUp"),
                    Value = true,
                    Setters = { new Setter(TextBlock.ForegroundProperty, (Brush)FindResource("B.Danger")) },
                });
                st.Triggers.Add(new DataTrigger
                {
                    Binding = new Binding("IsDown"),
                    Value = true,
                    Setters = { new Setter(TextBlock.ForegroundProperty, (Brush)FindResource("B.Ok")) },
                });
                st.Triggers.Add(new DataTrigger
                {
                    Binding = new Binding("HasDelta"),
                    Value = false,
                    Setters = { new Setter(TextBlock.ForegroundProperty, (Brush)FindResource("B.Muted")) },
                });
            }
            DzCmpGrid.Columns.Add(col);

            // 「立项指南」紧跟在名称后面：它是这一行的出处，跳过去看服务产出 / 价格构成最顺手，
            // 放到最后（差额涨幅之后）用户得横跨整张表再点回来。
            if (prop == "NewName") DzCmpGrid.Columns.Add(MakeGuideCol(baseCell));
        }
    }

    /// <summary>
    /// 「立项指南」链接列：命中可点（跳立项指南页并定位到该条目），未命中置灰显示「无立项指南」。
    /// <para>为什么另加一列而不是让名称本身可点：名称是可选中复制的文本（右键菜单「复制名称」依赖它），
    /// 改成链接会毁掉这个能力；单独一列也让「哪些项目有指南出处」一眼可扫。</para>
    /// </summary>
    private DataGridTemplateColumn MakeGuideCol(Style baseCell)
    {
        var cellStyle = new Style(typeof(DataGridCell), baseCell);
        cellStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        cellStyle.Setters.Add(new Setter(ToolTipService.ToolTipProperty, new Binding("GuideTip")));

        var btn = new FrameworkElementFactory(typeof(Button));
        btn.SetResourceReference(FrameworkElement.StyleProperty, "Btn.Link");
        btn.SetValue(ContentControl.ContentProperty, new Binding("GuideText"));
        btn.SetValue(FrameworkElement.TagProperty, new Binding("NewName"));
        btn.SetValue(Control.PaddingProperty, new Thickness(0));
        btn.SetValue(Control.FontSizeProperty, 12.0);
        btn.SetValue(Control.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        // 未命中时置灰但仍可点：点了给一句「立项指南未收录」的提示，比一个点不动也不说话的控件好。
        // 用 TextBlock 之外的「禁用态样式」而不是 IsEnabled=false —— 禁用后连提示都弹不出来。
        var dim = new Style(typeof(Button), (Style)Application.Current.FindResource("Btn.Link"));
        dim.Triggers.Add(new DataTrigger
        {
            Binding = new Binding("HasGuide"),
            Value = false,
            Setters = { new Setter(UIElement.OpacityProperty, 0.45) },
        });
        btn.SetValue(FrameworkElement.StyleProperty, dim);
        btn.AddHandler(Button.ClickEvent, new RoutedEventHandler(OnGuideLink));

        return new DataGridTemplateColumn
        {
            Header = "立项指南",
            Width = new DataGridLength(GuideColWidth),
            CellTemplate = new DataTemplate { VisualTree = btn },
            CellStyle = cellStyle,
            HeaderStyle = (Style)Application.Current.FindResource("DgHeadCenter"),
        };
    }

    /// <summary>
    /// 「立项指南」链接点击：切到立项指南页并定位。
    /// <para>Tag 只带项目名（不带区段）：行数据里没有区段字段，区段由名称反查得到，
    /// 反查用的是同一个 <see cref="LixiangStore.FindGuide"/>，与判定「可不可点」时完全一致。</para>
    /// </summary>
    private void OnGuideLink(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        var name = b.Tag as string ?? "";
        if (name.Length == 0) return;

        var guide = App.State.Lixiang.FindGuide(name);
        if (guide == null)
        {
            DzStatus.Text = $"「{name}」在立项指南中未收录（可能是地方新增或已更名项目）";
            return;
        }

        // 宿主窗口负责切页签；本页不自己拿 Window 强转 —— 离屏自检 / 截图里没有宿主，强转会崩。
        if (Window.GetWindow(this) is MainWindow mw && mw.OpenLixiangAt(guide.Value.Tab, name))
            DzStatus.Text = $"已跳到立项指南：{guide.Value.Tab} · {name}";
        else
            DzStatus.Text = $"立项指南条目已不存在：{name}（可在「立项指南」页检索）";
    }

    /// <summary>
    /// 「编码」链接列（新旧比对专用）：单元格里只有一个可点的编码。
    /// <para>与 <see cref="MakeLinkCol"/> 的区别：后者是「一对多 + 编码名称混排」的容器，
    /// 这里一行只有一个编码、旁边已有独立名称列，直接一个链接按钮即可，列宽也更好控。</para>
    /// <para>空编码（纯新增 / 无可比旧项目）显示「—」并按 <paramref name="enabledPath"/> 置灰：
    /// 留一个点下去没反应的蓝字，比灰掉更像故障。</para>
    /// </summary>
    private DataGridTemplateColumn MakeCodeLinkCol(string header, double width, Style baseCell,
        string textPath, string tagPath, string enabledPath, string tip, RoutedEventHandler onClick)
    {
        var cellStyle = new Style(typeof(DataGridCell), baseCell);
        cellStyle.Setters.Add(new Setter(ToolTipService.ToolTipProperty, tip));

        var btnStyle = new Style(typeof(Button), (Style)Application.Current.FindResource("Btn.Link"));
        btnStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        btnStyle.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
        btnStyle.Setters.Add(new Setter(Control.FontFamilyProperty, (FontFamily)FindResource("Font.Mono")));
        btnStyle.Setters.Add(new Setter(Control.HorizontalAlignmentProperty, HorizontalAlignment.Left));
        btnStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
        btnStyle.Triggers.Add(new DataTrigger
        {
            Binding = new Binding(enabledPath),
            Value = false,
            Setters =
            {
                new Setter(UIElement.IsEnabledProperty, false),
                new Setter(UIElement.OpacityProperty, 0.45),
            },
        });

        var btn = new FrameworkElementFactory(typeof(Button));
        btn.SetValue(FrameworkElement.StyleProperty, btnStyle);
        btn.SetValue(ContentControl.ContentProperty, new Binding(textPath));
        btn.SetValue(FrameworkElement.TagProperty, new Binding(tagPath));
        btn.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        btn.AddHandler(Button.ClickEvent, onClick);

        return new DataGridTemplateColumn
        {
            Header = header,
            Width = new DataGridLength(width),
            CellTemplate = new DataTemplate { VisualTree = btn },
            CellStyle = cellStyle,
            SortMemberPath = textPath,
            HeaderStyle = (Style)Application.Current.FindResource("DgHead"),
        };
    }

    /// <summary>
    /// 「现行项目」链接模板列：单元格内为若干可点链接（WrapPanel 自动换行），
    /// 点击 → 跳「现行项目」视图并按编码检索；disabledPath 非空时按该 bool 禁用链接。
    /// </summary>
    private DataGridTemplateColumn MakeLinkCol(string header, double width, Style baseCell,
        string linksPath, string tipPath, string disabledPath)
    {
        var cellStyle = new Style(typeof(DataGridCell), baseCell);
        cellStyle.Setters.Add(new Setter(ToolTipService.ToolTipProperty, new Binding(tipPath)));

        // 单个链接：Btn.Link 全局样式（跟随换肤），Tag 携带新项目编码
        var itemTpl = new DataTemplate();
        var btn = new FrameworkElementFactory(typeof(Button));
        btn.SetResourceReference(FrameworkElement.StyleProperty, "Btn.Link");
        btn.SetValue(Button.ContentProperty, new Binding("Text"));
        btn.SetValue(Button.TagProperty, new Binding("Code"));
        btn.SetValue(Control.PaddingProperty, new Thickness(0));
        btn.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 2));
        btn.SetValue(Control.FontSizeProperty, 12.0);
        btn.SetValue(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left);
        // 链接内容（「编码 + 名称」）经常超出一列宽：用可换行的文本模板，别让名称被裁掉
        var textTpl = new DataTemplate();
        var tbText = new FrameworkElementFactory(typeof(TextBlock));
        tbText.SetValue(TextBlock.TextProperty, new Binding());
        tbText.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        tbText.SetValue(TextBlock.FontSizeProperty, 12.0);
        textTpl.VisualTree = tbText;
        btn.SetValue(ContentControl.ContentTemplateProperty, textTpl);
        if (disabledPath != null)
            btn.SetBinding(UIElement.IsEnabledProperty, new Binding(disabledPath));
        btn.AddHandler(Button.ClickEvent, new RoutedEventHandler(OnStdLink));
        itemTpl.VisualTree = btn;

        // 链接容器：WrapPanel 自动换行（一对多时逐条展示）
        var ic = new FrameworkElementFactory(typeof(ItemsControl));
        ic.SetValue(ItemsControl.ItemsSourceProperty, new Binding(linksPath));
        ic.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var panelTpl = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
        ic.SetValue(ItemsControl.ItemsPanelProperty, panelTpl);
        ic.SetValue(ItemsControl.ItemTemplateProperty, itemTpl);

        // 直接废止（无替代）时给占位说明，链接区为空；文本为空时折叠（否则空行会把内容撑出固定行高之外）
        // 上下各留 3px：行高自适应时，两行链接正好贴住上下边框会显得挤（2026-11-17）
        var cell = new FrameworkElementFactory(typeof(StackPanel));
        cell.SetValue(StackPanel.OrientationProperty, Orientation.Vertical);
        cell.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        cell.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 3));
        cell.AppendChild(ic);
        var noStdStyle = new Style(typeof(TextBlock));
        noStdStyle.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
        noStdStyle.Triggers.Add(new DataTrigger
        {
            Binding = new Binding("NoStdText"),
            Value = "",
            Setters = { new Setter(TextBlock.VisibilityProperty, Visibility.Collapsed) },
        });
        var noStd = new FrameworkElementFactory(typeof(TextBlock));
        noStd.SetValue(FrameworkElement.StyleProperty, noStdStyle);
        noStd.SetValue(TextBlock.TextProperty, new Binding("NoStdText"));
        noStd.SetResourceReference(TextBlock.ForegroundProperty, "B.Muted");
        cell.AppendChild(noStd);

        return new DataGridTemplateColumn
        {
            Header = header,
            Width = new DataGridLength(width),
            CellTemplate = new DataTemplate { VisualTree = cell },
            CellStyle = cellStyle,
            // 同 MakeTextCol：模板列也必须显式挂表头样式，否则白底表头会在灰蓝表头里开个缺口
            HeaderStyle = (Style)Application.Current.FindResource("DgHead"),
        };
    }

    /// <summary>文本列统一构建：等宽字体 / 右对齐 / 悬停全文提示。</summary>
    /// <summary>模板列（内容交给 XAML DataTemplate，如两行「批次 / 执行」）。</summary>
    private DataGridTemplateColumn MakeTemplateCol(string header, double width, Style baseCell, string templateKey)
    {
        var cellStyle = new Style(typeof(DataGridCell), baseCell);
        return new DataGridTemplateColumn
        {
            Header = header,
            Width = new DataGridLength(width <= 0 ? 120 : width),
            CellTemplate = (DataTemplate)FindResource(templateKey),
            CellStyle = cellStyle,
            HeaderStyle = (Style)Application.Current.FindResource("DgHead"),
        };
    }

    /// <summary>
    /// 造一列文本。对齐由 <c>align</c> 决定（见 <see cref="AlignmentOf"/>）：
    /// 价格 / 比例 / 涨幅 / 序号这类短数值列**居中**——物价人员是「按列扫数字」，
    /// 居中后同一列的数字共用一条中线，比右对齐好对位；长文本列（名称 / 文号 / 备注）仍左对齐。
    /// </summary>
    private DataGridTextColumn MakeTextCol(string prop, string header, double width, bool align, bool isMono,
        FontFamily mono, Style baseCell, string tipProp = "", bool wrap = false)
    {
        var binding = new Binding(prop);
        var alignment = AlignmentOf(align, wrap);

        var textStyle = new Style(typeof(TextBlock));
        textStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        if (wrap)
        {
            // 换行显示全文（长名称 / 括号里的限定语不能截断），行高由 RowHeight="NaN" 自动撑开
            textStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            textStyle.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(0, 3, 0, 3)));
        }
        else
        {
            textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        }
        if (alignment != TextAlignment.Left)
        {
            // TextBlock 默认按内容宽自适应，只设 TextAlignment 不会动它 —— 必须撑满单元格，
            // 对齐才在**列宽**内生效（否则永远贴着左边）。
            textStyle.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
            textStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, alignment));
        }
        if (isMono)
        {
            textStyle.Setters.Add(new Setter(TextBlock.FontFamilyProperty, mono));
            textStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 12.0));
        }

        var cellStyle = new Style(typeof(DataGridCell), baseCell);
        if (alignment != TextAlignment.Left)
            cellStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,
                alignment == TextAlignment.Center ? HorizontalAlignment.Center : HorizontalAlignment.Right));
        cellStyle.Setters.Add(new Setter(ToolTipService.ToolTipProperty,
            string.IsNullOrEmpty(tipProp) ? binding : new Binding(tipProp)));

        return new DataGridTextColumn
        {
            Header = header,
            Width = width <= 0 ? new DataGridLength(120) : new DataGridLength(width),
            Binding = binding,
            ElementStyle = textStyle,
            CellStyle = cellStyle,
            // 数值列的表头也跟着居中，否则「头靠左、数居中」看起来还是错位。
            // ⚠ 左对齐列不能写 HeaderStyle = null：列自身的 HeaderStyle 会**显式覆盖**
            //   DataGrid 的 ColumnHeaderStyle，null 之后这些列的表头就没有 DgHead 模板了，
            //   背景退回表头容器色（白），和灰蓝的数值列表头拼出「背景块不一样」的分段（2026-10-27）。
            HeaderStyle = (Style)Application.Current.FindResource(
                alignment == TextAlignment.Center ? "DgHeadCenter" : "DgHead"),
        };
    }

    /// <summary>
    /// 列定义里的对齐标志 → 实际对齐。
    /// <list type="bullet">
    /// <item>带换行的列（名称 / 文号 / 备注）恒左对齐：居中的多行文本读起来会散。</item>
    /// <item>其余标记为「数值列」的列统一居中：三级/二级/一级价格、扣减基本物耗、医保支付、自付比例、
    ///       差额、涨幅、序号等 —— 同一列数字共用中线，扫列时不需要左右找。</item>
    /// </list>
    /// </summary>
    internal static TextAlignment AlignmentOf(bool align, bool wrap) =>
        !align || wrap ? TextAlignment.Left : TextAlignment.Center;
}
