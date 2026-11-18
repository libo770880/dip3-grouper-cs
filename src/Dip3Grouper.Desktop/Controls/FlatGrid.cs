using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Dip3Grouper;
using Dip3GrouperApp.Desktop.Converters;

namespace Dip3GrouperApp.Desktop.Controls;

/// <summary>
/// 按区段列定义动态生成 DataGrid 列。
///
/// <para><b>两种列宽模式</b>：</para>
/// <para>① 传统模式（不传 <c>rows</c>）：按配置里的 <c>Width</c> 固定，钳制 64..280，与网页版一致。</para>
/// <para>② 智能模式（传 <c>rows</c>）：先按「表头宽度 / 样本内容宽度」量出每列的理想值，
/// 短列（编码、序号、布尔）给固定像素，长文本列（诊断名称、手术名称…）给 Star 权重，
/// 让它们吃掉窗口剩余宽度。这样既不会右侧大片留白，拖宽窗口也不会挤成一条缝。</para>
///
/// <para><b>为什么一定要 Star</b>：全用像素列时，1086 宽的窗口里 5 列只占 660，
/// 剩下 380 是空白，而「诊断名称」还在用 150px 硬截成「未特指病因的胃…」。
/// Star 列把余量还给内容，才是「列宽跟着窗口走」的正确做法。</para>
///
/// <para>用户手动拖过列宽后，该列会被 WPF 自动转成像素值，Star 权重不再管它 —— 这是预期行为，
/// 调用方（DiseaseLibView）会按区段记住这些像素值，翻页 / 重查时原样恢复。</para>
/// </summary>
internal static class FlatGrid
{
    /// <summary>列宽下限：再窄表头就只剩一个字了</summary>
    private const double MinCol = 64;

    /// <summary>列宽上限：再宽一行字太长，眼睛回行会串行</summary>
    private const double MaxCol = 460;

    /// <summary>
    /// 编码列上限。
    /// <para>编码是多值竖线串（<c>44.1300x001|45.1300x004|45.2302</c>），按内容原样量能到 250+，
    /// 一条编码列就吃掉名称列的口粮。编码本身有悬停提示、能整段复制，
    /// 所以宁可截断也要把宽度让给「诊断名称 / 手术名称」这类必须读全的列。</para>
    /// </summary>
    private const double MaxCodeCol = 150;

    /// <summary>次要编码列（「相关手术编码」等）更窄一档：它是查证用，不是主读列</summary>
    private const double MaxCodeColSub = 130;

    /// <summary>单元格左右 padding（DgCell 各 10）+ 一点点呼吸位</summary>
    private const double CellPad = 24;

    /// <summary>表头参与测宽的上限：表头允许折行，不必为了放下整串「手术编码（空=保守行）」把列撑爆</summary>
    private const double HeadCap = 124;

    /// <summary>只拿前 N 行做样本，避免 1.7 万行全量测量把首屏拖慢</summary>
    private const int SampleRows = 60;

    /// <summary>
    /// 可伸缩列（建完列后由页面按表格实际宽度二次分配）。
    ///
    /// <para><b>为什么不用 DataGrid 的 Star 列</b>：本项目的表统一开了
    /// <c>ScrollViewer.HorizontalScrollBarVisibility="Auto"</c>，此时 ScrollContentPresenter
    /// 用无限宽测量内容，Star 列拿不到「视口宽度」这个基准，会一路塌到 MinWidth
    /// （实测 1086 宽窗口里三列各卡在 140，右侧还空着 90）。
    /// 与其和 WPF 的测量顺序较劲，不如自己按 <c>DataGrid.ActualWidth</c> 算像素，
    /// 结果可预测，窗口缩放时再算一遍即可。</para>
    /// </summary>
    internal sealed class FlexCol
    {
        public DataGridColumn Column { get; init; }

        /// <summary>分配权重：按理想宽度占比分剩余空间，内容长的列自然分得多</summary>
        public double Weight { get; init; }

        /// <summary>收缩下限</summary>
        public double Min { get; init; }
    }

    /// <summary>
    /// 生成列。
    /// </summary>
    /// <param name="grid">目标表</param>
    /// <param name="columns">区段列定义</param>
    /// <param name="owner">用于取字体资源与 DPI 的宿主元素</param>
    /// <param name="rows">当前页数据；传了就启用智能列宽，传 null 则退回固定像素</param>
    /// <param name="savedWidths">用户手动拖过的列宽（key → 像素），按区段各自记忆</param>
    /// <returns>可伸缩列清单；空列表表示按固定像素即可（后台页等不需要铺满的表）</returns>
    public static List<FlexCol> BuildColumns(DataGrid grid, List<ColumnDef> columns, FrameworkElement owner,
        IList<Dictionary<string, object>> rows = null,
        IReadOnlyDictionary<string, double> savedWidths = null)
    {
        var flexCols = new List<FlexCol>();
        grid.Columns.Clear();
        if (columns == null || columns.Count == 0) return flexCols;

        var mono = (FontFamily)owner.FindResource("Font.Mono");
        var ui = (FontFamily)owner.FindResource("Font.UI");
        var baseCell = (Style)owner.FindResource("DgCell");
        var headCenter = (Style)owner.FindResource("DgHeadCenter");
        var dpi = VisualTreeHelper.GetDpi(owner).PixelsPerDip;

        var smart = rows != null;
        var ideals = smart ? MeasureIdeals(columns, rows, ui, mono, dpi) : null;

        foreach (var c in columns)
        {
            var isSeq = string.Equals(c.Key, "seq", StringComparison.OrdinalIgnoreCase);
            var isCode = c.Mono == true
                || Regex.IsMatch((c.Key ?? "") + (c.Label ?? ""), "code|编码|序号|seq",
                    RegexOptions.IgnoreCase);

            var binding = new Binding($"[{c.Key}]") { Converter = CellValueConverter.Instance };

            var textStyle = new Style(typeof(TextBlock));
            textStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            if (isCode)
            {
                textStyle.Setters.Add(new Setter(TextBlock.FontFamilyProperty, mono));
                textStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 12.0));
            }
            if (isSeq)
            {
                // 序号居中 + 降透明度：它是行号不是数据，抢视线反而干扰读编码。
                // TextAlignment 管文字在 TextBlock 内的位置，HorizontalAlignment 先把
                // TextBlock 拉满单元格 —— 两个都要，否则 TextBlock 收缩到文字宽，居中是空操作。
                textStyle.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
                textStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
                textStyle.Setters.Add(new Setter(TextBlock.OpacityProperty, 0.55));
            }

            var cellStyle = new Style(typeof(DataGridCell), baseCell);
            cellStyle.Setters.Add(new Setter(ToolTipService.ToolTipProperty, binding));
            // 序号列靠单元格的内容对齐居中（DgCell 模板已把 ContentPresenter 绑到该属性）
            if (isSeq)
                cellStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,
                    HorizontalAlignment.Center));

            var col = new DataGridTextColumn
            {
                Header = FormatHeader(c.Label),
                SortMemberPath = c.Key,     // 拖拽结束时靠它回查是哪一列（见 DiseaseLibView.OnColResized）
                Binding = binding,
                ElementStyle = textStyle,
                CellStyle = cellStyle,
            };

            double? saved = savedWidths != null && c.Key != null && savedWidths.TryGetValue(c.Key, out var sw)
                ? sw : null;

            if (saved is > 0)
            {
                // 用户拖过：尊重手上这个宽度，不参与二次分配
                col.Width = new DataGridLength(saved.Value);
                col.MinWidth = Math.Min(saved.Value, MinCol);
            }
            else if (!smart)
            {
                var width = Math.Max(64, Math.Min(c.Width ?? 120, 280));
                col.Width = new DataGridLength(width);
            }
            else if (isSeq)
            {
                // 序号列内容最多 5 位，固定窄列即可，剩下的宽度留给名称
                col.Width = new DataGridLength(Math.Max(52, c.Width ?? 52));
                col.MinWidth = 48;
            }
            else
            {
                var ideal = ideals.TryGetValue(c.Key ?? "", out var v) ? v : 140;
                // 名称 / 说明类列一律可伸缩：内容短不代表它次要
                // （「相关手术名称」经常整列为空，按内容量只有 95px，但一旦有值就是长文本）
                var flex = !isCode && (ideal >= 150 || IsTextColumn(c));
                // 先按理想宽度铺像素，等表格量出实际宽度后由调用方补足剩余空间
                col.Width = new DataGridLength(ideal);
                col.MinWidth = Math.Min(ideal, MinCol);
                if (flex)
                    flexCols.Add(new FlexCol
                    {
                        Column = col,
                        // 权重压在 160..280：内容特别长的列不该垄断余量，否则并排的三个名称列
                        // 会拉成「一个 400 / 两个 120」，反而不如均分好读
                        Weight = Math.Clamp(ideal, 160, 280),
                        Min = 140,
                    });
            }

            if (isSeq) col.HeaderStyle = headCenter;
            grid.Columns.Add(col);
        }

        // 一列可伸缩的都没有（整张表都是短码字段）：退而求其次，让最后一列吃掉余量，
        // 否则窗口拉宽时右侧又是一条白带。
        if (smart && flexCols.Count == 0)
        {
            var last = grid.Columns.LastOrDefault(x =>
                !string.Equals(x.SortMemberPath, "seq", StringComparison.OrdinalIgnoreCase));
            if (last != null)
                flexCols.Add(new FlexCol { Column = last, Weight = 1, Min = Math.Min(last.Width.Value, 150) });
        }
        return flexCols;
    }

    /// <summary>
    /// 把表格的剩余宽度按权重补给可伸缩列。
    /// <para>调用时机：建完列后一次、<c>DataGrid.SizeChanged</c> 时一次。
    /// 用 <c>ActualWidth</c> 减去固定列之和得到余量，再按权重摊分；
    /// 余量不够时压到各自下限，超出视口的部分交给横向滚动条。</para>
    /// </summary>
    public static void FillFlex(DataGrid grid, List<FlexCol> flexCols)
    {
        if (grid == null || flexCols == null || flexCols.Count == 0) return;

        var avail = grid.ActualWidth;
        if (avail <= 0) return;
        // 竖向滚动条浮在表内，不扣掉会让最后一列被压出横向滚动条
        if (grid.Template?.FindName("DG_ScrollViewer", grid) is ScrollViewer sv
            && sv.ComputedVerticalScrollBarVisibility == Visibility.Visible)
            avail -= SystemParameters.VerticalScrollBarWidth;
        if (avail <= 0) return;

        var flexSet = new HashSet<DataGridColumn>(flexCols.Select(f => f.Column));
        var fixedW = grid.Columns.Where(c => !flexSet.Contains(c))
            .Sum(c => c.Width.IsAbsolute ? c.Width.Value : c.ActualWidth);

        var remain = avail - fixedW;
        var totalMin = flexCols.Sum(f => f.Min);
        if (remain < totalMin) remain = totalMin;

        var weightSum = flexCols.Sum(f => f.Weight);
        if (weightSum <= 0) return;

        foreach (var f in flexCols)
        {
            var w = Math.Max(f.Min, Math.Round(remain * f.Weight / weightSum));
            if (Math.Abs(f.Column.Width.Value - w) > 0.5)
                f.Column.Width = new DataGridLength(w);
        }
    }

    /// <summary>按表头文字 + 前若干行内容，量出每列「不截断需要多宽」</summary>
    private static Dictionary<string, double> MeasureIdeals(List<ColumnDef> columns,
        IList<Dictionary<string, object>> rows, FontFamily ui, FontFamily mono, double dpi)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var c in columns)
        {
            if (c.Key == null) continue;
            var isCode = c.Mono == true
                || Regex.IsMatch(c.Key + (c.Label ?? ""), "code|编码|序号|seq", RegexOptions.IgnoreCase);

            var head = HeaderWidth(c.Label ?? "", ui, dpi);

            double body = 0;
            var n = 0;
            foreach (var r in rows)
            {
                if (n++ >= SampleRows) break;
                var s = r != null && r.TryGetValue(c.Key, out var v) ? Format(v) : "";
                if (s.Length == 0) continue;
                body = Math.Max(body, Measure(s, isCode ? mono : ui, isCode ? 12.0 : 12.5, dpi));
            }

            var cap = isCode
                ? (IsSecondaryCode(c) ? MaxCodeColSub : MaxCodeCol)
                : MaxCol;
            map[c.Key] = Math.Clamp(Math.Max(head, body + CellPad), MinCol, cap);
        }
        return map;
    }

    /// <summary>
    /// 长表头按左括号主动断行：「手术编码（空=保守行）」交给 WPF 自动折行会断成
    /// 「手术编码（空=保守 / 行）」，一个孤零零的「行）」挂在第二行。
    /// 在括号前断开，两行正好是「手术编码」和「（空=保守行）」，顺带把列宽需求从 160 降到 110。
    /// </summary>
    private static string FormatHeader(string label)
    {
        if (string.IsNullOrEmpty(label) || label.Length <= 10) return label;
        var i = label.IndexOf('（');
        return i > 0 ? label[..i] + "\n" + label[i..] : label;
    }

    /// <summary>表头换行后取最长那一行的宽度</summary>
    private static double HeaderWidth(string label, FontFamily ui, double dpi)
    {
        var text = FormatHeader(label);
        var w = 0.0;
        foreach (var line in text.Split('\n'))
            w = Math.Max(w, Measure(line, ui, 12.5, dpi));
        return Math.Min(w, HeadCap) + CellPad;
    }

    /// <summary>「相关手术编码 / 其他诊断」这类查证用的编码列，宽度让一档给主读列</summary>
    private static bool IsSecondaryCode(ColumnDef c)
        => (c.Key ?? "").StartsWith("rel", StringComparison.OrdinalIgnoreCase)
           || (c.Label ?? "").Contains("相关");

    /// <summary>名称 / 说明 / 备注 / 参考类列：属于「要读全」的列，宽度跟着窗口走</summary>
    private static bool IsTextColumn(ColumnDef c)
    {
        var label = c.Label ?? "";
        var key = c.Key ?? "";
        return label.Contains("名称") || label.Contains("说明") || label.Contains("内涵")
            || label.Contains("备注") || label.Contains("参考") || label.Contains("建议")
            || key is "name" or "diagName" or "procName" or "relNames" or "note" or "suggest" or "desc";
    }

    /// <summary>用 FormattedText 精确测宽（比「中文 13px / 西文 7px」的估算可靠，且能处理混排）</summary>
    private static double Measure(string text, FontFamily family, double size, double dpi)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size, Brushes.Black, dpi);
        return ft.WidthIncludingTrailingWhitespace;
    }

    /// <summary>
    /// 把一行扁平字典按列定义转成「输入框对话框」所需的字段列表。
    /// 长文本字段（usage / serviceOutput / priceComposition / note / relNames …）用多行输入。
    /// </summary>
    public static List<RowEditDialog.Field> ToEditFields(List<ColumnDef> columns,
        Dictionary<string, object> row, IEnumerable<string> immutableKeys = null)
    {
        var frozen = new HashSet<string>(immutableKeys ?? Array.Empty<string>(), StringComparer.Ordinal);
        var fields = new List<RowEditDialog.Field>();
        foreach (var c in columns)
        {
            var value = row != null && row.TryGetValue(c.Key, out var v) ? Format(v) : "";
            fields.Add(new RowEditDialog.Field(
                c.Key,
                c.Label ?? c.Key,
                value,
                Multiline: IsLongText(c.Key),
                ReadOnly: c.Immutable == true || frozen.Contains(c.Key)));
        }
        return fields;
    }

    private static bool IsLongText(string key) => key is
        "usage" or "serviceOutput" or "priceComposition" or "guide" or "guideUsage"
        or "note" or "relNames" or "procName" or "diagName" or "name"
        or "suggest";   // 目录库外病种：可入组组合可能上百字，单行输入框改不动

    /// <summary>字典值 → 纯文本（数组用 | 连接，bool 用 是/否）。</summary>
    public static string Format(object v)
    {
        switch (v)
        {
            case null: return "";
            case string s: return s;
            case bool b: return b ? "是" : "否";
            case System.Text.Json.JsonElement je:
                return je.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => je.GetString() ?? "",
                    System.Text.Json.JsonValueKind.True => "是",
                    System.Text.Json.JsonValueKind.False => "否",
                    System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined => "",
                    System.Text.Json.JsonValueKind.Array => string.Join("|",
                        je.EnumerateArray().Select(x => x.ValueKind == System.Text.Json.JsonValueKind.String
                            ? x.GetString() ?? "" : x.ToString())),
                    _ => je.ToString(),
                };
            case System.Collections.IEnumerable e:
                return string.Join("|", e.Cast<object>().Select(Format).Where(x => x.Length > 0));
            default: return v.ToString();
        }
    }
}
