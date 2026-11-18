using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using Dip3Grouper;
using System.Windows;
using Dip3GrouperApp.Desktop.Views;
using Dip3GrouperApp.Desktop.Views.Admin;

namespace Dip3GrouperApp.Desktop;

/// <summary>
/// 性能采样（诊断用，命令行 <c>--bench</c>）：把「页面打开 + 检索」链路上各阶段的耗时打到控制台。
/// 用于回答「某页为什么慢」——区分是数据载入、过滤扫描、拼音转换，还是详情卡关联索引。
/// </summary>
internal static class Bench
{
    public static void Run(AppState state)
    {
        var sw = Stopwatch.StartNew();
        Report("AppState.Init（立项指南 + 德州物价 + 病种库 + 分组引擎）", sw);

        sw.Restart();
        state.Guifan.EnsureLoaded();
        Report($"技术规范载入（{state.Guifan.Items.Count:N0} 条 / 10.6MB JSON）", sw);

        // ---------------- 三个库的列表检索 ----------------
        Bench1("技术规范 · 空条件", sw, () => state.Guifan.ListRows("", "", "", 1, 50));
        Bench1("技术规范 · 关键词「诊察」", sw, () => state.Guifan.ListRows("", "", "诊察", 1, 50));
        Bench1("技术规范 · 拼音「zc」", sw, () => state.Guifan.ListRows("", "", "zc", 1, 50));
        Bench1("技术规范 · 拼音「xzyz」", sw, () => state.Guifan.ListRows("", "", "xzyz", 1, 50));

        Bench1("德州物价 · 空条件", sw, () => state.Dezhou.ListRows("", "", "", "", 1, 50));
        Bench1("德州物价 · 拼音「xzyz」", sw, () => state.Dezhou.ListRows("", "", "", "xzyz", 1, 50));

        var tab = state.Lixiang.State.Sections.Keys.First();
        Bench1("立项指南 · 空条件", sw, () => state.Lixiang.ListRows("", "", 1, 50));
        Bench1("立项指南 · 拼音「xzyz」", sw, () => state.Lixiang.ListRows("", "xzyz", 1, 50));
        Bench1("立项指南 · 单区段 " + tab, sw, () => state.Lixiang.ListRows(tab, "", 1, 50));

        // ---------------- 详情卡里的跨库关联（页面打开时为每行 VM 构建） ----------------
        sw.Restart();
        CrossMap.ForGuifan(state.Guifan.Items[0].Name);
        Report("跨库关联索引首次构建 + 1 次查询", sw);

        sw.Restart();
        for (var i = 0; i < 50; i++) CrossMap.ForGuifan(state.Guifan.Items[i].Name);
        Report("跨库关联 50 行查询（技术规范首页）", sw);

        sw.Restart();
        for (var i = 0; i < 50; i++) CrossMap.ForDezhou(state.Dezhou.Items[i].Name);
        Report("跨库关联 50 行查询（德州物价首页）", sw);

        // ---------------- 页面各处一次性扫描成本 ----------------
        Bench1("技术规范 · Categories()", sw, () => state.Guifan.Categories());
        Bench1("技术规范 · Chapters()", sw, () => state.Guifan.Chapters());
        Bench1("技术规范 · DistinctUnits()", sw, () => state.Guifan.DistinctUnits());
        Bench1("技术规范 · Stat()", sw, () => state.Guifan.Stat());
        Bench1("立项指南 · Categories()", sw, () => state.Lixiang.Categories());

        // ---------------- 强制布局一个真实页面（拆成构造 / 布局 / 载入三段） ----------------
        GuifanView gv = null;
        Bench1("技术规范页 · 构造 + 建列", sw, () => gv = new GuifanView());
        Bench1("技术规范页 · 首次布局（1440×900）", sw, () =>
        {
            gv.Measure(new Size(1440, 900)); gv.Arrange(new Rect(0, 0, 1440, 900)); gv.UpdateLayout();
            return gv;
        });
        Bench1("技术规范页 · EnsureLoaded（下拉 + 首页 50 行）", sw, () => { gv.EnsureLoaded(); return gv; });
        Bench1("技术规范页 · 载入后重新布局（50 行渲染）", sw, () =>
        {
            gv.Measure(new Size(1440, 900)); gv.Arrange(new Rect(0, 0, 1440, 900)); gv.UpdateLayout();
            return gv;
        });
        Bench1("技术规范页 · 第二批渲染（换页 50 行，无 JIT）", sw, () =>
        {
            gv.Measure(new Size(1440, 900)); gv.Arrange(new Rect(0, 0, 1440, 900));
            gv.UpdateLayout();
            return gv;
        });

        DezhouPriceView dv = null;
        Bench1("德州物价页 · 构造 + 建列", sw, () => dv = new DezhouPriceView());
        Bench1("德州物价页 · 首次布局", sw, () =>
        {
            dv.Measure(new Size(1440, 900)); dv.Arrange(new Rect(0, 0, 1440, 900)); dv.UpdateLayout();
            return dv;
        });
        Bench1("德州物价页 · EnsureLoaded（下拉 + 首页 50 行）", sw, () => { dv.EnsureLoaded(); return dv; });
        Bench1("德州物价页 · 载入后重新布局（50 行渲染）", sw, () =>
        {
            dv.Measure(new Size(1440, 900)); dv.Arrange(new Rect(0, 0, 1440, 900)); dv.UpdateLayout();
            return dv;
        });

        // 病种库查询页（1.1 万+ 行，预热队列最优先的一页）
        DiseaseLibView libv = null;
        Bench1("病种库页 · 构造 + 建列", sw, () => libv = new DiseaseLibView());
        Bench1("病种库页 · EnsureLoaded + 首次渲染（50 行）", sw, () =>
        {
            libv.Prewarm(1440, 900);
            return libv;
        });

        LixiangView lv = null;
        Bench1("立项指南页 · 构造 + 建列", sw, () => lv = new LixiangView());
        Bench1("立项指南页 · 首次布局", sw, () =>
        {
            lv.Measure(new Size(1440, 900)); lv.Arrange(new Rect(0, 0, 1440, 900)); lv.UpdateLayout();
            return lv;
        });
        Bench1("立项指南页 · EnsureLoaded（下拉 + 首页 50 行）", sw, () => { lv.EnsureLoaded(); return lv; });
        Bench1("立项指南页 · 载入后重新布局（50 行渲染）", sw, () =>
        {
            lv.Measure(new Size(1440, 900)); lv.Arrange(new Rect(0, 0, 1440, 900)); lv.UpdateLayout();
            return lv;
        });
        Bench1("立项指南页 · 检索一次（关键词「心脏」）", sw, () => { lv.LxQuery.Text = "心脏"; return lv; });

        // ---------------- 拼音缓存冷启动（首次按拼音检索的隐藏成本） ----------------
        PinyinHelper.WarmAll(state.Dezhou.Items.Select(i => i.Name));  // 先耗掉其它库的转换
        Console.WriteLine($"[bench] 缓存条目 {PinyinHelper.CacheCount:N0}");

        Console.WriteLine("== bench done ==");
    }

    /// <summary>
    /// 页面离屏取样（诊断用，命令行 --shot-page lx|dz|gf[:文号:类别] &lt;输出.png&gt;）：
    /// 载入数据 + 强制布局后渲染成 PNG，无需人工点页签即可核对筛选栏与表格版式。
    /// dz 支持 <c>dz:文号:类别</c> 指定选中项（类别留空 = 该文号全部类别的使用说明分组视图）。
    ///
    /// <para>后台页（<c>adm*</c> 前缀）比前台页长得多（多张卡片），默认画布高度更大；
    /// 带 <c>p</c> 后缀的两个（<c>admverp</c> / <c>admauditp</c>）用**只在内存里的假数据**填充，
    /// 因为真实库里可能还没有历史版本、审计日志也可能只有寥寥几条，看不出表格版式。</para>
    /// </summary>
    public static string ShotPage(string which, string outPath)
    {
        var seg = which.Split(':');
        var admin = seg[0].StartsWith("adm", StringComparison.Ordinal);
        var size = seg[0] == "sg" ? new Size(1500, 1080)
                 : admin ? new Size(1500, 1180)
                 : new Size(1500, 760);
        // 任意页面支持 @宽 / @宽x高 后缀（如 dzsel@1000、admverp@1200x1600）：
        // 核对窄窗口下的版式（横向溢出 / 换行），或把长页面一次性放进画布
        var at = seg[0].IndexOf('@');
        if (at >= 0)
        {
            var dim = seg[0][(at + 1)..];
            var x = dim.IndexOf('x');
            if (x >= 0)
            {
                if (double.TryParse(dim[..x], out var cw)) size.Width = cw;
                if (double.TryParse(dim[(x + 1)..], out var ch)) size.Height = ch;
            }
            else if (double.TryParse(dim, out var w))
            {
                size.Width = w;
            }
            seg[0] = seg[0][..at];
        }
        FrameworkElement view = seg[0] switch
        {
            "sg" => new SingleGroupView(),      // 单条分组：填入病案并分组（核对结果卡与「分组溯源」）
            "lx" => new LixiangView(),
            "lxsel" => new LixiangView(),      // 价格项目：展开首行详情（核对同款详情卡是否撑宽）
            "lxjiedu" => new LixiangView(),   // 解读辅导（lxjiedu[:关键词[:行号]]，核对详情卡版式与横向滚动）
            "dz" => new DezhouPriceView(),
            "dzfav" => new DezhouPriceView(),   // 预注入收藏 + 勾选「仅看收藏」
            "dzsel" => new DezhouPriceView(),   // 展开首行详情卡（核对可选中文本版式）
            "dzcancel" => new DezhouPriceView(),// 切到「作废对照」视图（核对映射表版式）
            "dzcdoc" => new DezhouPriceView(),  // 切作废对照 + 按批次下拉筛选（核对批次维度可见性）
            "dzlegacy" => new DezhouPriceView(),// 切到「旧版物价」视图（核对状态着色与现行标准列）
            "dzcmp" => new DezhouPriceView(),   // 切到「新旧比对」视图（核对差额涨幅与三态标注）
            "dzcmplv" => new DezhouPriceView(), // 同上，按三级 / 二级 / 一级口径渲染（核对换档与表头）
            "dzfind" => new DezhouPriceView(),  // 现行项目视图按关键词检索（核对加收项公式价换算）
            "dzlevel" => new DezhouPriceView(), // 现行项目 + 指定医院级别（核对级别筛选与列头「本级」标注）
            "dzlive" => new DezhouPriceView(),  // 模拟点「现行标准/对应新编码」链接跳转（核对跳转与返回按钮）
            "dzcmpjump" => new DezhouPriceView(),  // 比对视图点旧编码 → 旧版物价（核对返回按钮文案）
            "dzcmpjumpn" => new DezhouPriceView(), // 比对视图点新编码 → 现行项目
            "dzback" => new DezhouPriceView(),  // 跳转后立即返回（核对返回态与查询词恢复）
            "dzuse" => new DezhouPriceView(),   // 展开首行详情卡并点「点此定位查看全部」链接（核对面板定位）
            "dzbusy" => new DezhouPriceView(),  // 定格「页面渐显」：核对内容压淡 + 顶部进度线
            "lib" => new DiseaseLibView(),      // 病种库（lib:outlib 指定区段，核对新增「目录库外病种」tab）
            "batch" => new BatchGroupView(),    // 批量分组（喂样例文本，核对「目录库外病种」列与摘要）
            "draftbusy" => new DraftPriceView(),// 同上，征求意见页
            "lxbusy" => new LixiangView(),      // 同上，立项指南页
            "admlxbusy" => new AdminLixiangView(), // 同上，后台立项指南页
            "draft" => new DraftPriceView(),        // 新项目征求意见：新增视图（核对黄底 + 角标）
            "draftsel" => new DraftPriceView(),     // 展开首行详情卡（核对服务产出/映射原项目块）
            "draftcancel" => new DraftPriceView(),  // 切到「拟作废对照」视图（核对替代项目回填）
            "draftmap" => new DraftPriceView(),     // 切到「映射关系」视图（核对两段式表头列）
            "draftcmp" => new DraftPriceView(),     // 切到「新旧价格比对」视图（核对比对列与涨跌）
            "draftcmpn" => new DraftPriceView(),    // 比对视图 + 只看「纯新增项目」
            "draftcmps" => new DraftPriceView(),    // 比对视图 + 按指定列排序（draftcmps:涨幅）
            "draftq" => new DraftPriceView(),       // 带检索词（核对命中计数与筛选态）
            "draftdetail" => new DraftPriceView(),  // 检索指定编码 → 展开详情卡 → 点「定位查看全部」（核对说明面板定位）
            // 工具栏抽屉首页（病理卡 + 预留位）
            "tool" => new ToolPanel(App.State).DiagRoot(null, null, null),
            // 工具栏 → 病理开具指引（guide:系统:关键词:计价模式:价格档 → guide:女性生殖系统::细胞:3）
            "guide" => new ToolPanel(App.State).DiagRoot(ToolPanel.ToolPathology,
                           seg.Length >= 2 ? seg[1] : null, seg.Length >= 3 ? seg[2] : null,
                           seg.Length >= 4 ? seg[3] : null, null, seg.Length >= 5 ? seg[4] : null),
            // 工具栏 → 基本物耗指引（wuhao:页签:关键词 → wuhao:judge:输液器、wuhao:basic:试剂、wuhao:charge、wuhao:rule）
            "wuhao" => new ToolPanel(App.State).DiagRoot(ToolPanel.ToolWuhao,
                           seg.Length >= 2 ? seg[1] : null, seg.Length >= 3 ? seg[2] : null),
            // 工具栏 → 物价解读 AI（priceai / priceai:set / priceai:ask:关键词）
            // ask 只跑本机检索，不调用模型，截图里能看到气泡与真实证据编号。
            "priceai" => new ToolPanel(App.State).DiagRoot(ToolPanel.ToolPriceAi,
                           seg.Length >= 2 ? seg[1] : null, seg.Length >= 3 ? seg[2] : null),
            "admover" => new AdminOverviewView(),
            "admdata" => new AdminDataView(),   // 核对「数据更新」五张卡，含新增的客户端更新卡
            "admmap" => new AdminMappingView(),
            "admlx" => new AdminLixiangView(),
            "admdz" => new AdminDezhouView(),
            "admdx" => new AdminDiseasesView(),
            "admset" => new AdminSettingsView(),
            "admver" => new AdminVersionView(), // 真实状态（历史版本为空时看空态与按钮禁用）
            "admverp" => new AdminVersionView(),// 内存假数据：版本列表 + 增删改差异行
            "admaudit" => new AdminAuditView(), // 真实审计日志
            "admauditp" => new AdminAuditView(),// 内存假数据：含失败行与超长详情（核对换行）
            _ => new GuifanView(),
        };
        if (seg[0] == "dzfav")
            App.State.DezhouFav.DiagInject(App.State.Dezhou.Items.Take(3)
                .Select(i => (i.Code, i.Doc, i.Name)));
        switch (view)
        {
            case DiseaseLibView v:
                v.DiagTab(seg.Length >= 2 ? seg[1] : "bingxiang");
                break;
            case BatchGroupView v:
                v.DiagBatch(seg.Length >= 2 ? string.Join(':', seg[1..])
                    : "病案号\t主要诊断编码\t主要手术操作编码\t其他诊断编码\n"
                      + "MZ001\tH65\t22.0200\t\n"
                      + "MZ002\tM17\t77.2700x001\t\n"
                      + "MZ003\tI21\t99.1000\t\n"
                      + "MZ004\tA09.901\t44.1300x001\t\n"
                      + "MZ005\tP58.9\t\t");
                break;
            case SingleGroupView v:
                v.DiagGroup(seg.Length >= 2 ? seg[1] : "",
                            seg.Length >= 3 ? seg[2] : null,
                            seg.Length >= 4 ? seg[3] : null);
                break;
            case LixiangView v:
                v.Prewarm(size.Width, size.Height);
                if (seg[0] == "lxbusy")
                    FindVeil(v)?.DiagFreeze("正在载入解读辅导…", "解析问答并建列表");
                if (seg[0] == "lxsel")
                {
                    v.DiagItemsRow(0);
                    v.Measure(new Size(size.Width, size.Height));
                    v.Arrange(new Rect(0, 0, size.Width, size.Height));
                    v.UpdateLayout();
                    Console.WriteLine("[lxsel] " + v.DiagItemsMetrics());
                }
                if (seg[0] == "lxjiedu")
                {
                    var q = seg.Length >= 2 ? seg[1] : "";
                    var si = seg.Length >= 3 && int.TryParse(seg[2], out var rr) ? rr : 0;
                    v.DiagJiedu(q, si);
                    v.Measure(new Size(size.Width, size.Height));
                    v.Arrange(new Rect(0, 0, size.Width, size.Height));
                    v.UpdateLayout();
                    Console.WriteLine("[lxjiedu] " + v.DiagJieduMetrics());
                }
                break;
            case DezhouPriceView v:
                v.Prewarm(size.Width, size.Height);
                if (seg[0] == "dzfav") v.DiagFavOnly();
                else if (seg[0] == "dzcancel" && seg.Length >= 2) v.DiagCancelQuery(seg[1]);
                else if (seg[0] == "dzcancel") v.DiagCancel();
                else if (seg[0] == "dzcdoc" && seg.Length >= 2) v.DiagCancelDoc(seg[1]);
                else if (seg[0] == "dzlegacy" && seg.Length >= 2) v.DiagLegacyQuery(seg[1]);
                else if (seg[0] == "dzlegacy") v.DiagLegacy();
                else if (seg[0] == "dzcmp") v.DiagCompareQuery(seg.Length >= 2 ? seg[1] : "");
                else if (seg[0] == "dzcmplv")   // 比对视图按指定价格口径渲染（2 / 1 / 3[:范围]）
                    v.DiagCompareLevel(seg.Length >= 2 ? seg[1] : "3",
                                       seg.Length >= 3 ? seg[2] : "");
                else if (seg[0] == "dzfind" && seg.Length >= 2) v.DiagLiveQuery(seg[1]);
                else if (seg[0] == "dzlevel" && seg.Length >= 2)
                    v.DiagLevel(seg[1], seg.Length >= 3 ? seg[2] : "");
                if (seg[0] == "dzfind" && seg.Length >= 3 && seg[2].StartsWith("sel", StringComparison.Ordinal))
                {
                    // dzfind:词:sel / dzfind:词:sel1 —— 展开首行 / 第 N 行详情卡（核对加收项换算块）
                    var tail = seg[2][3..];
                    v.DzGrid.SelectedIndex = tail.Length == 0 ? 0 : int.TryParse(tail, out var ri) ? ri : 0;
                }
                else if (seg[0] == "dzlive" && seg.Length >= 2) v.DiagGoLive(seg[1]);
                else if (seg[0] == "dzback" && seg.Length >= 2) v.DiagJumpBack(seg[1]);
                else if (seg[0] == "dzcmpjump" || seg[0] == "dzcmpjumpn")
                {
                    // 比对视图里真实点一行编码：dzcmpjump = 旧码→旧版物价，dzcmpjumpn = 新码→现行项目。
                    // 核对跳转后的页面版式、状态栏提示与「← 返回新旧比对」按钮。
                    v.DiagCompareLevel("3", "priced");
                    var jumpRows = v.CmpRows.ToList();
                    var wantOld = seg[0] == "dzcmpjump";
                    var ji = jumpRows.FindIndex(r => !string.IsNullOrWhiteSpace(wantOld ? r.OldCode : r.NewCode));
                    if (ji < 0) ji = 0;
                    Console.WriteLine("[cmpjump] " + v.ClickCmpCodeLink(ji, wantOld));
                }
                // ⚠ dzbusy 必须排在「seg.Length>=3 → DiagSelect」之前：它自己也吃三段
                //   （dzbusy:Cmp:spill），否则会被当成「选文号 / 类别」，顺手重取一次数把渐显抹掉。
                else if (seg[0] == "dzbusy")
                {
                    // 定格页面渐显（dzbusy / dzbusy:Cmp 指定目标视图文案 / dzbusy:Cmp:spill 定格「超时转呼吸」）。
                    // ⚠ 只调 ShowBusy 不调 ApplyView：渐显要保持在「正在切」那一帧，
                    //   切完就收起了，截图里什么都看不到。
                    // ⚠ 传 seg[1..] 整段而不是 seg[1]：spill 是第三段，只传 seg[1] 会把它丢掉。
                    v.DiagBusy(seg.Length >= 2 ? string.Join(':', seg[1..]) : "Live");
                }
                else if (seg.Length >= 3) v.DiagSelect(seg[1], seg[2]);   // 模拟选中文号 / 类别（核对使用说明面板）
                if (seg[0] == "dzsel") v.DzGrid.SelectedIndex = 0;        // 展开首行详情卡
                else if (seg[0] == "dzuse")
                {
                    // 展开首行详情卡 → 找到「点此定位查看全部」链接行并真实触发点击
                    v.DzGrid.SelectedIndex = 0;
                    var vm = v.DzGrid.Items.Cast<DzRowVm>().FirstOrDefault();
                    vm?.Blocks.SelectMany(b => b.Lines).FirstOrDefault(l => l.Click != null)?.Click?.Invoke();
                }
                break;
            case GuifanView v: v.Prewarm(size.Width, size.Height); break;
            case DraftPriceView v:
                v.Prewarm(size.Width, size.Height);
                if (seg[0] == "draftcancel") v.DiagCancel();
                else if (seg[0] == "draftmap") v.DiagMap();
                else if (seg[0] == "draftcmp") v.DiagCompare();
                else if (seg[0] == "draftcmpn") v.DiagCompareScope("new");
                else if (seg[0] == "draftcmps" && seg.Length >= 2) v.DiagCompareSort(seg[1]);
                else if (seg[0] == "draftq" && seg.Length >= 2) v.DiagQuery(seg[1]);
                else if (seg[0] == "draftdetail" && seg.Length >= 2) v.DiagDetailUsage(seg[1]);
                if (seg[0] == "draftbusy")
                    FindVeil(v)?.DiagFreeze("正在整理映射关系表…", "还原通知附件两列结构");
                if (seg[0] == "draftsel") v.DraftGrid.SelectedIndex = 0;  // 展开首行详情卡
                break;
            case AdminVersionView v:
                v.EnsureLoaded();
                if (seg[0] == "admverp") v.DiagPreview();
                break;
            case AdminAuditView v:
                v.EnsureLoaded();
                if (seg[0] == "admauditp") v.DiagPreview();
                break;
            case AdminDataView v: v.EnsureLoaded(); break;
            case AdminMappingView v: v.EnsureLoaded(); break;
            case AdminLixiangView v:
                v.EnsureLoaded();
                if (seg[0] == "admlxbusy")
                    FindVeil(v)?.DiagFreeze("正在载入解读辅导…", "重新载入问答列表");
                break;
            case AdminDezhouView v: v.EnsureLoaded(); break;
            case AdminDiseasesView v: v.EnsureLoaded(); break;
            case AdminSettingsView v: v.EnsureLoaded(); break;
            case AdminOverviewView v: v.EnsureLoaded(); break;
        }
        view.Measure(size);
        view.Arrange(new Rect(size));
        view.UpdateLayout();

        // 分段按钮的白块靠 Dispatcher 回调定位，而离屏渲染不泵消息 → 这里同步补一次
        Controls.Segment.SyncAll(view);

        // 列宽按 ActualWidth 算，但离屏布局不会派发 SizeChanged → 量完高度后补算一次
        if (view is DiseaseLibView dlvFill)
        {
            dlvFill.DiagFillFlex();
            view.UpdateLayout();
        }

        var host = new Border
        {
            Background = (System.Windows.Media.Brush)Application.Current.Resources["B.Bg"],
            Child = view,
            Width = size.Width,
            Height = size.Height,
        };
        host.Measure(size);
        host.Arrange(new Rect(size));
        host.UpdateLayout();

        if (view is GuifanView gv)
            foreach (var c in gv.GfGrid.Columns)
                Console.WriteLine($"[cols] {c.Header}  width={c.ActualWidth:0}  desired={c.Width.DisplayValue:0.#}  vis={c.Visibility}");
        if (view is DiseaseLibView dlv)
            foreach (var c in dlv.Grid.Columns)
                Console.WriteLine($"[cols] {c.Header}  width={c.ActualWidth:0}  desired={c.Width.DisplayValue:0.#}  unit={c.Width.UnitType}  min={c.MinWidth:0}");
        if (view is DezhouPriceView dzv)
        {
            foreach (var c in dzv.DzGrid.Columns)
                Console.WriteLine($"[cols] {c.Header}  width={c.ActualWidth:0}  desired={c.Width.DisplayValue:0.#}  vis={c.Visibility}");
        }
        if (view is LixiangView lxv)
            foreach (var c in lxv.LxGrid.Columns)
                Console.WriteLine($"[cols] {c.Header}  width={c.ActualWidth:0}  desired={c.Width.DisplayValue:0.#}  vis={c.Visibility}");
        if (view is AdminDezhouView adv)
            foreach (var c in adv.Grid.Columns)
                Console.WriteLine($"[cols] {c.Header}  width={c.ActualWidth:0}  desired={c.Width.DisplayValue:0.#}");
        if (view is DraftPriceView dpv)
            foreach (var g in dpv.DraftGrids)
                foreach (var c in g.Columns)
                    Console.WriteLine($"[cols] draft/{g.Name}/{c.Header}  width={c.ActualWidth:0}");

        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)size.Width, (int)size.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(host);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using (var fs = File.Create(outPath)) enc.Save(fs);
        return null;
    }

    /// <summary>
    /// 右键菜单离屏取样（诊断用，命令行 --shot-menu dz|lx|gf &lt;输出.png&gt;）：
    /// 构建首页第一行的右键菜单并直接渲染（不开窗、不真弹 Popup），核对菜单样式与条目集合。
    /// </summary>
    /// <summary>可视化树里深度优先找渐显控件（诊断截图用：把指定页面定格在渐显状态）。</summary>
    private static Controls.BusyVeil FindVeil(DependencyObject root)
    {
        if (root is Controls.BusyVeil hit) return hit;
        var n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var found = FindVeil(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// 前台主题选择离屏取样（诊断用，命令行 <c>--shot-theme &lt;主题id&gt; &lt;输出前缀&gt;</c>）：
    /// 产出 <c>&lt;前缀&gt;-bar.png</c>（标题栏）与 <c>&lt;前缀&gt;-panel.png</c>（配色浮层）。
    /// Popup 未打开时其子树不在视觉树里，必须单独 Measure/Arrange 后再渲染，不能直接截窗口。
    /// </summary>
    public static string ShotTheme(string themeId, string outPrefix)
    {
        var win = new MainWindow();
        var th = Theming.ThemeCatalog.Get(themeId);
        Theming.ThemeManager.Apply(th.Id);

        var root = (FrameworkElement)win.Content;
        root.Measure(new Size(1300, 870));
        root.Arrange(new Rect(0, 0, 1300, 870));
        root.UpdateLayout();

        var full = new System.Windows.Media.Imaging.RenderTargetBitmap(1300, 870, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        full.Render(root);
        SavePng(new System.Windows.Media.Imaging.CroppedBitmap(full,
            new Int32Rect(0, 0, 1300, 52)), outPrefix + "-bar.png");

        var panel = win.ThemePanel;
        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var w = (int)Math.Ceiling(panel.DesiredSize.Width);
        var h = (int)Math.Ceiling(panel.DesiredSize.Height);
        panel.Arrange(new Rect(0, 0, w, h));
        panel.UpdateLayout();
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(panel);
        SavePng(bmp, outPrefix + "-panel.png");

        var selected = win.SelectedThemeId;
        win.Close();
        return $"{th.Id} 选中={selected} bar=1300×52 panel={w}×{h}";
    }

    private static void SavePng(System.Windows.Media.Imaging.BitmapSource src, string path)
    {
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    public static string ShotMenu(string which, string outPath)
    {
        object item;
        FrameworkElement owner;
        switch (which)
        {
            case "dz":
                {
                    var dv = new DezhouPriceView(); dv.Prewarm(1500, 760);
                    item = dv.DzGrid.Items.Cast<DzRowVm>().First(); owner = dv;
                    break;
                }
            case "lx":
                {
                    var lv = new LixiangView(); lv.Prewarm(1500, 760);
                    item = lv.LxGrid.Items.Cast<LxRowVm>().First(); owner = lv;
                    break;
                }
            case "lib":
                {
                    // 病种库：菜单按区段列动态生成，owner 必须是 DataGrid 才能取到列名
                    var dv = new DiseaseLibView(); dv.Prewarm(1500, 760);
                    item = dv.Grid.Items.Cast<Dictionary<string, object>>().First(); owner = dv.Grid;
                    break;
                }
            default:
                {
                    var gv = new GuifanView(); gv.Prewarm(1500, 760);
                    item = gv.GfGrid.Items.Cast<GfRowVm>().First(); owner = gv;
                    break;
                }
        }

        var menu = RowMenu.Build(item, owner, owner)
                  ?? throw new InvalidOperationException("右键菜单没有生成任何条目");
        menu.ApplyTemplate();
        menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var w = Math.Ceiling(menu.DesiredSize.Width);
        var h = Math.Ceiling(menu.DesiredSize.Height);
        if (w < 40 || h < 20) throw new InvalidOperationException($"菜单尺寸异常 {w}×{h}（模板未生效？）");
        menu.Arrange(new Rect(0, 0, w, h));
        menu.UpdateLayout();

        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)w, (int)h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(menu);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using (var fs = File.Create(outPath)) enc.Save(fs);
        Console.WriteLine($"[shot-menu] {which} 菜单 {w:0}×{h:0} · {menu.Items.Count} 项");
        return null;
    }

    /// <summary>
    /// 分段白块「排布留痕」（诊断用，命令行 <c>--seg-trace</c>）：
    /// 挂真实窗口 → 等页面渲染稳定 → 用 <see cref="Controls.Segment.Trace"/> 记录从 Checked 到
    /// 补间启动的每一步（起点钉在哪、等了哪一轮布局、白块当时的插值/基值是多少）。
    ///
    /// <para>与 <see cref="SegDemo"/> 的区别：SegDemo 需要 CompositionTarget.Rendering 推动画时钟，
    /// 在无人值守环境里可能一帧都不回调（表现为卡死两分钟）；这里的留痕全在 LayoutUpdated /
    /// Checked 回调里写，不需要渲染帧，纯粹回答「动画到底有没有被启动、From 是多少」。</para>
    /// </summary>
    /// <summary>
    /// 实机（真实窗口 + 真实消息循环）验证「点分段页签会压淡页面」。
    /// <para>为什么非要在窗口里跑：渐显的全部机制都依赖消息循环 ——
    /// <c>Dispatcher.BeginInvoke(Render)</c> 要让出一帧才会执行，动画时钟要随渲染帧推进。
    /// 离屏路径两样都没有，只能证明版式，证明不了「点下去真的会亮」。</para>
    /// <para>做法：ShowActivated=false 的隐形窗口 → 真实触发分段 RadioButton 的 Checked →
    /// 在点击后的前几帧采样 <c>BusyVeil.IsBusy</c> 与文案文本 → 记录出现的帧数与时序。</para>
    /// 输出 &lt;outDir&gt;/veil-trace.log。
    /// </summary>
    public static string VeilTrace(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var log = Path.Combine(outDir, "veil-trace.log");
        var file = new StreamWriter(log, false, System.Text.Encoding.UTF8) { AutoFlush = true };
        var t0 = Stopwatch.StartNew();
        void W(string m) => file.WriteLine($"[{t0.ElapsedMilliseconds,7}ms] {m}");

        var size = new Size(1500, 760);
        var view = new DezhouPriceView();
        view.Prewarm(size.Width, size.Height);

        var win = new Window
        {
            Content = view,
            Width = size.Width,
            Height = size.Height,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = 0,
            Top = 0,
            Opacity = 0,
            Topmost = false,   // 诊断窗口不该压在任何窗口上面
        };
        win.Show();
        Pump(1500);

        var veil = FindVeil(view);
        if (veil == null)
        {
            W("FAIL 视觉树里找不到 BusyVeil");
            file.Dispose();
            return log;
        }
        W($"渐显控件就位 · 初始 IsBusy={veil.IsBusy}");

        // 首屏载入自己也会亮一次遮罩。不等它收起就开始采样，第一次「点页签」会撞上那一轮：
        // 在忙守卫把新的 Show 吃掉，随后旧的 HideAfter 把遮罩收起 → 误判成「点了没反应」。
        var idleUntil = Environment.TickCount64 + 8000;
        while (veil.IsBusy && Environment.TickCount64 < idleUntil) Pump(80);
        Pump(300);
        W($"首屏遮罩已收起 · 开始采样（IsBusy={veil.IsBusy}）");

        // 依次点四个分段：每次点完在 0~900ms 内逐帧看内容是否被压淡、何时淡回
        var segs = new (string Name, Func<RadioButton> Get)[]
        {
            ("作废对照", () => view.SegCancelButton),
            ("旧版物价", () => view.SegLegacyButton),
            ("新旧比对", () => view.SegCmpButton),
            ("现行项目", () => view.SegLiveButton),
        };
        var allOk = true;
        foreach (var (name, get) in segs)
        {
            var rb = get();
            if (rb == null) { W($"跳过 {name}：找不到入口"); allOk = false; continue; }
            if (!Probe(veil, name, () => rb.IsChecked = true, W, name == "作废对照" ? 2600 : 900))
                allOk = false;
        }

        // 同一套渐显也必须覆盖「查询 / 翻页」：这两处才是用户点得最频繁的入口。
        // 这里真实触发 Button.Click（而不是直接调 RunBusy），确保走的是用户那条通路。
        var probeQ = view.QueryBox;
        var probeNext = view.NextPageButton;
        if (view.SearchButton != null && probeQ != null)
        {
            probeQ.Text = "移植";    // 造一个能筛出结果的条件，让 Load 真正干活
            if (!Probe(veil, "查询", () => view.SearchButton.RaiseEvent(
                    new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)), W))
                allOk = false;
        }
        if (probeNext != null)
        {
            if (!Probe(veil, "下一页", () => probeNext.RaiseEvent(
                    new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)), W))
                allOk = false;
        }

        W(allOk ? "=== 全部通过 [PASS]" : "=== 存在失败项 [FAIL]");
        win.Close();
        file.Dispose();
        return log;
    }

    /// <summary>
    /// 联想输入框实机采样（命令行 <c>--suggest-trace &lt;输出目录&gt;</c>）。
    ///
    /// <para>为什么必须开真窗口：下拉是否弹出取决于 180ms 防抖计时器，而计时器只有在**消息循环**
    /// 真跑起来时才会触发。离屏 Measure/Arrange 永远等不到 Tick，也就永远看不到「选完又弹回来」
    /// 这个回归 —— 只能靠真窗口 + 真 Dispatcher 把时间轴推起来。</para>
    ///
    /// <para>采样四件事：① 键入会弹下拉；② 选中后下拉不再自动重开（旧版 180ms 后必然重开）；
    /// ③ 单选字段选中后整段被选中，直接键入即替换；④ 水印与输入文字左对齐（同一套文本引擎）。</para>
    /// </summary>
    public static string SuggestTrace(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var log = Path.Combine(outDir, "suggest-trace.log");
        var file = new StreamWriter(log, false, System.Text.Encoding.UTF8) { AutoFlush = true };
        var t0 = Stopwatch.StartNew();
        void W(string m) => file.WriteLine($"[{t0.ElapsedMilliseconds,7}ms] {m}");

        var diag = new Controls.SuggestBox { Kind = "diag", Placeholder = "A09.901 或 胃肠炎", Width = 300, Height = 32 };
        var proc = new Controls.SuggestBox { Kind = "proc", Placeholder = "44.1300 或 胃镜", Width = 300, Height = 32 };
        var host = new StackPanel { Margin = new Thickness(20) };
        host.Children.Add(diag);
        host.Children.Add(proc);

        var win = new Window
        {
            Content = host,
            Width = 360,
            Height = 160,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = 0,
            Top = 0,
            Opacity = 0,         // 完全透明：看得见行为，看不见窗口
            Topmost = false,   // 诊断窗口不该压在任何窗口上面
        };
        try
        {
            win.Show();
            Pump(300);

            // ① 键入 → 防抖 180ms 后弹下拉
            diag.DiagType("胃肠");
            Pump(500);
            W(diag.DiagPopupOpen ? "OK  ① 键入后下拉弹出" : "FAIL ① 键入后下拉没弹出来");
            if (!diag.DiagPopupOpen) return log;

            // ② 选中一项 → 下拉保持关闭，且文本就是选中编码
            var pick = GrouperService.SuggestDiag(App.State, "胃肠", 3).FirstOrDefault();
            if (pick == null) { W("FAIL ② 检索不到「胃肠」的诊断"); return log; }
            diag.DiagPick(pick.Code);
            Pump(600);           // 远超 180ms 防抖：旧版会在这里重新弹出来
            W(diag.DiagPopupOpen
                ? "FAIL ② 选中后下拉又被弹回来了（用户得再点一次才能接着输入）"
                : "OK  ② 选中后下拉保持关闭");
            W(diag.Text == pick.Code ? $"OK  ② 回填的是编码 {pick.Code}" : $"FAIL ② 回填文本是 {diag.Text}");

            // ③ 整段选中：接着敲键就是替换，不用先清空
            var sel = (diag.FindName("Input") as TextBox)?.SelectionLength ?? -1;
            W(sel == pick.Code.Length
                ? $"OK  ③ 选中后整段选中（{sel} 字符），键入即替换"
                : $"FAIL ③ 只选中了 {sel} 字符（应为 {pick.Code.Length}）");

            // ④ 水印与真正输入的文字共用同一套排版 → 偏移必须为 0
            var off = diag.DiagWatermarkOffset;
            W(diag.DiagWatermarkIsTextBox && Math.Abs(off.X) < 0.5 && Math.Abs(off.Y) < 0.5
                ? $"OK  ④ 水印与输入框同模板（偏移 {off.X:0.#},{off.Y:0.#}、左内边距 {diag.DiagWatermarkPadding:0}）"
                : $"FAIL ④ 水印偏移 {off.X:0.#},{off.Y:0.#}（文本框={diag.DiagWatermarkIsTextBox}）");

            // ⑤ 名称回显
            W(string.IsNullOrEmpty(diag.DiagEcho)
                ? "FAIL ⑤ 编码没有回显名称"
                : $"OK  ⑤ 回显名称 {diag.DiagEcho}");

            // ⑥ 清空后仍能重新检索（静音开关不能把用户键入也吞掉）
            diag.DiagType("胃");
            Pump(500);
            W(diag.DiagPopupOpen ? "OK  ⑥ 再次键入照常弹下拉" : "FAIL ⑥ 再次键入没弹下拉（静音开关没复位）");

            // ⑦ 手术联想同样不重开
            proc.DiagType("胃镜");
            Pump(500);
            var p = GrouperService.SuggestProc(App.State, "胃镜", 3).FirstOrDefault();
            if (p == null) W("FAIL ⑦ 检索不到「胃镜」的手术");
            else
            {
                proc.DiagPick(p.Code);
                Pump(600);
                W(proc.DiagPopupOpen ? "FAIL ⑦ 手术框选中后下拉重开" : "OK  ⑦ 手术框选中后同样不再重开");
            }
        }
        catch (Exception ex)
        {
            W("FAIL 异常：" + ex.Message);
        }
        finally
        {
            win.Close();
            file.Dispose();
        }
        return log;
    }

    /// <summary>
    /// 一次「点击 → 压淡」采样：点之前记时，点完逐帧（16ms）读 <c>IsBusy</c> 与同级内容的不透明度，
    /// 记下首次可见时刻、亮起帧数、收起时刻与文案。
    /// <para>判定条件：亮过（onFrames&gt;0）且停留不小于最短停留 − 60ms（容差给渲染抖动）。</para>
    /// </summary>
    private static bool Probe(Dip3GrouperApp.Desktop.Controls.BusyVeil veil, string name,
                              Action click, Action<string> w, int waitMs = 900)
    {
        var frames = 0;
        var onFrames = 0;
        var offAt = -1L;
        var caption = "";
        var started = Environment.TickCount64;

        // 渐显的实质是「内容的不透明度变了」，只查 IsBusy 等于查了个开关：
        // 这里直接读同级的页面内容，证明它真的被压淡、又真的淡回来。
        var panel = veil.Parent as Panel;
        double MinSiblingOpacity()
        {
            if (panel == null) return 1;
            var min = 1.0;
            foreach (UIElement c in panel.Children)
            {
                if (ReferenceEquals(c, veil)) continue;
                if (c.Opacity < min) min = c.Opacity;
            }
            return min;
        }

        click();              // 触发 Checked / Click → Busy.Show + BeginInvoke(Render)

        // Show 与压淡都是同步做的：还没让出这一帧，遮罩已经是亮的、内容已经压淡。
        // 冷启动首次切「作废对照」要现算整张映射表，那一次的工作量会把整个采样窗口一口吞掉
        // （Show→干活→Hide 全发生在同一帧内），只在帧之间采样就永远读不到 —— 会被误判成「点了没反应」。
        var shownSync = veil.IsBusy;
        var dimmedSync = MinSiblingOpacity();

        var visibleAt = shownSync ? 0L : -1L;
        var dimmed = dimmedSync;
        // 首次切换（冷启动）要现算作废对照数据，UI 线程被占住时 900ms 窗口会先到点：
        // 冷启动那条给足 2.6s，其余仍按 900ms 判「点下去就该亮」。
        while (Environment.TickCount64 - started < waitMs)
        {
            Pump(16);
            frames++;
            if (veil.IsBusy)
            {
                onFrames++;
                if (visibleAt < 0) visibleAt = Environment.TickCount64 - started;
                caption = veil.CaptionText;
                var op = MinSiblingOpacity();
                if (op < dimmed) dimmed = op;
            }
            else if (onFrames > 0)
            {
                offAt = Environment.TickCount64 - started;
                break;
            }
        }
        // 收起后要留足淡回动画的时间（220ms）再读末值
        Pump(360);
        var endOp = MinSiblingOpacity();

        var min = Dip3GrouperApp.Desktop.Controls.BusyVeil.DefaultMinMs;
        var shown = shownSync || onFrames > 0;
        var ok = shown && dimmed <= 0.45 && endOp >= 0.98
                 && (offAt < 0 ? !veil.IsBusy : offAt > min - 60);
        w($"{(ok ? "OK  " : "FAIL")} {name}: 可见于 {visibleAt}ms（点击即亮）· 亮起帧 {onFrames}/{frames} · "
          + $"收起 {offAt}ms（最短停留 {min}ms）· 压淡到 {dimmed:0.00} · 收尾 {endOp:0.00} · 文案「{caption}」");
        return ok;
    }

    public static string SegTrace(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var log = Path.Combine(outDir, "seg-trace.log");
        var file = new StreamWriter(log, false, System.Text.Encoding.UTF8) { AutoFlush = true };
        var t0 = Stopwatch.StartNew();
        void W(string m) => file.WriteLine($"[{t0.ElapsedMilliseconds,7}ms] {m}");

        var size = new Size(1500, 760);
        var view = new DezhouPriceView();
        view.Prewarm(size.Width, size.Height);

        var win = new Window
        {
            Content = view,
            Width = size.Width,
            Height = size.Height,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = 0,
            Top = 0,
            Opacity = 0,
            Topmost = false,   // 诊断窗口不该压在任何窗口上面
        };
        win.Show();

        // 先把页面渲染稳：白块必须已被 Sync 到「现行项目」，否则第一次 Checked 读不到起点
        var settleUntil = Environment.TickCount64 + 4000;
        Border bar = null;
        while (Environment.TickCount64 < settleUntil)
        {
            Pump(40);
            bar = Controls.Segment.Find(view);
            if (bar != null && bar.ActualWidth > 1) break;
        }
        Pump(300);   // 再给布局几帧，确保字重与宽度都已终态

        if (bar == null)
        {
            W("FAIL 视觉树里找不到挂 Segment.Track 的容器");
            win.Close(); file.Dispose();
            return "ERR";
        }
        Controls.Segment.Sync(bar);
        Pump(120);
        Controls.Segment.Measure(bar, out var l0, out var w0);
        W($"初态 Left={l0:0.0} 宽={w0:0.0}（切换前应落在「现行项目」）");

        // 诊断把时长拉长，便于肉眼/截图确认；留痕本身与时长无关
        Controls.Segment.SlideMsOverride = 600;
        Controls.Segment.Trace = m => W(m);
        try
        {
            W("— 触发切换：现行项目 → 新旧比对 —");
            view.DiagCompareQuery("");
            // 泵 1.2s 的消息：LayoutUpdated / Checked 全在这期间发生
            var until = Environment.TickCount64 + 1200;
            while (Environment.TickCount64 < until) Pump(20);
        }
        finally
        {
            Controls.Segment.Trace = null;
            Controls.Segment.SlideMsOverride = 0;
        }

        Controls.Segment.Measure(bar, out var l1, out var w1);
        W($"终态 Left={l1:0.0} 宽={w1:0.0}");
        W($"位移 {l1 - l0:0.0}px（>1 才算真的换了位置）");
        win.Close();
        file.Dispose();

        Console.WriteLine("[seg-trace] 留痕写入 " + log);
        return null;
    }

    /// <summary>
    /// 分段白块滑动采样（诊断用，命令行 <c>--seg-demo &lt;输出目录&gt;</c>）：
    /// 把「德州市物价」页挂进一个挪到屏幕外的真实窗口，等一切就绪后把分段
    /// 从「现行项目」切到「新旧比对」，逐帧采白块的 Margin.Left 并落盘 PNG。
    ///
    /// <para>踩过的三个坑（都表现为「白块纹丝不动」，其实界面没问题）：</para>
    /// <list type="number">
    /// <item>离屏 <c>Measure/Arrange/UpdateLayout</c> 不跑 Dispatcher：
    ///   Checked → <c>BeginInvoke</c> 排的排布回调永远不执行。要真跑消息循环。</item>
    /// <item>光有窗口不够，还要**泵消息**：<c>Dispatcher.Invoke(空委托)</c> 在同线程是内联快路径，
    ///   什么也不泵；必须 <c>DispatcherFrame</c> + <c>PushFrame</c>。</item>
    /// <item>动画时钟由 MediaContext 每帧推进，只在**真实渲染**时走。所以这里不 Sleep 轮询，
    ///   而是挂 <c>CompositionTarget.Rendering</c>（每渲染帧回调一次），
    ///   在回调里按墙钟时间采样 —— 这才是界面上真正发生的事。</item>
    /// </list>
    ///
    /// <para>判定：若 Left 落在一串介于起点与终点之间的中间值上 → 有滑动过渡（渐变）；
    /// 若只有「起点」「终点」两个值 → 仍是硬切。</para>
    /// </summary>
    public static string SegDemo(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var size = new Size(1500, 760);
        var view = new DezhouPriceView();
        view.Prewarm(size.Width, size.Height);

        // 真实窗口、屏幕内、几乎全透明。
        // ⚠ 光有窗口还不够：必须真的产生渲染帧，动画时钟才走。
        //   曾试过 Left=-20000（离屏）与 Opacity=0.01，两种都测过：
        //   离屏时 CompositionTarget.Rendering 一次不回调；屏幕内 + 0.02 才有帧。
        var win = new Window
        {
            Content = view,
            Width = size.Width,
            Height = size.Height,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = 0,
            Top = 0,
            Opacity = 0,
            Topmost = false,   // 诊断窗口不该压在任何窗口上面
        };
        win.Show();

        Border bar = null;
        var samples = new System.Collections.Generic.List<(long T, double Left, double Width, bool Anim)>();
        var fromLeft = double.NaN;
        var sw = new Stopwatch();

        // ---- 1) 等真实布局成形（白块落在「现行项目」）--------------------------
        var settleUntil = Environment.TickCount64 + 3000;
        while (Environment.TickCount64 < settleUntil)
        {
            Pump(40);
            bar = Controls.Segment.Find(view);
            if (bar != null && bar.ActualWidth > 1) break;
        }
        if (bar == null || !Controls.Segment.Measure(bar, out fromLeft, out var fromW))
        {
            Console.WriteLine("[seg-demo] FAIL 视觉树里找不到挂 Segment.Track 的容器（或结构不符）");
            win.Close();
            return "ERR";
        }
        Console.WriteLine($"[seg-demo] 起点 Left={fromLeft:0.0} 宽={fromW:0.0}  切换前选中：{CheckedText(bar)}");

        // 诊断把时长拉长，让 60fps 的渲染帧能采到足够多的中间态（产品仍是 180ms）
        Controls.Segment.SlideMsOverride = 600;

        // ---- 2) 真实切换到「新旧比对」 -----------------------------------------
        sw.Restart();
        view.DiagCompareQuery("");

        // ---- 3) 按渲染帧采样 ---------------------------------------------------
        // CompositionTarget.Rendering 每渲染帧回调一次，那一刻动画时钟已被
        // MediaContext 推进到当前墙钟，取到的就是界面上真实显示的位置。
        // ⚠ 不能用 Sleep/自旋轮询代替：时钟只在渲染帧推进（实测 Sleep(300) 后
        //   属性仍停在初始快照）。也不能只看 GetValue 一次 —— 道理相同。
        var done = false;
        EventHandler onFrame = null;
        onFrame = (_, _) =>
        {
            Controls.Segment.Measure(bar, out var l, out var w);
            samples.Add((sw.ElapsedMilliseconds, l, w,
                bar.Child is Panel p && p.Children[0] is Border sl && sl.HasAnimatedProperties));
            if (sw.ElapsedMilliseconds > 1200) done = true;
        };
        System.Windows.Media.CompositionTarget.Rendering += onFrame;
        try
        {
            var guard = Environment.TickCount64 + 6000;
            while (!done && Environment.TickCount64 < guard) Pump(8);
        }
        finally
        {
            System.Windows.Media.CompositionTarget.Rendering -= onFrame;
            Controls.Segment.SlideMsOverride = 0;   // 复原，别带到下一次真实切换

            win.Close();
        }

        if (samples.Count < 3)
        {
            Console.WriteLine($"[seg-demo] FAIL 采样不足（{samples.Count} 帧；窗口没在屏幕上渲染？）");
            return "FAIL";
        }

        foreach (var s in samples)
            Console.WriteLine($"[seg-demo] t={s.T,4}ms  Left={s.Left,7:0.0}  宽={s.Width,5:0.0}  anim={s.Anim}");

        var toLeft = samples[^1].Left;
        var middles = samples
            .Where(x => Math.Abs(x.Left - fromLeft) > 0.6 && Math.Abs(x.Left - toLeft) > 0.6)
            .Select(x => Math.Round(x.Left, 1))
            .Distinct()
            .OrderBy(x => x)
            .ToList();
        var moved = Math.Abs(toLeft - fromLeft) > 1.0;
        var animated = moved && middles.Count >= 3;

        Console.WriteLine($"[seg-demo] 终点 Left={toLeft:0.0}  位移={toLeft - fromLeft:0.0}px  采样 {samples.Count} 帧");
        Console.WriteLine($"[seg-demo] 中间态 {middles.Count} 个：{string.Join(", ", middles.Take(30))}");
        Console.WriteLine(moved
            ? (animated ? "[seg-demo] PASS 白块有滑动过渡（存在中间态）" : "[seg-demo] FAIL 白块是硬切（无中间态）")
            : "[seg-demo] WARN 白块根本没有移动（检查选中项是否切换）");
        return animated ? null : "FAIL";
    }

    /// <summary>诊断辅助：列出轨道内当前被选中的标签文字（判断切换是否真的生效）。</summary>
    private static string CheckedText(Border bar)
    {
        if (bar.Child is not Panel host || host.Children.Count < 2 || host.Children[1] is not Panel labels)
            return "(结构不符)";
        var names = new System.Collections.Generic.List<string>();
        foreach (var c in labels.Children)
            if (c is RadioButton rb && rb.IsChecked == true)
                names.Add((rb.Content as string) ?? "?");
        return names.Count == 0 ? "(无)" : string.Join(" / ", names);
    }

    /// <summary>
    /// 泵消息 <paramref name="ms"/> 毫秒。⚠ <c>Dispatcher.Invoke(空委托)</c> 不行：
    /// 同线程调用走内联快路径，直接执行、不入队，等于什么都没泵。
    /// 必须起一个 <c>DispatcherFrame</c> 再 <c>PushFrame</c>，让队列里的 Loaded 优先级
    /// 回调（Segment 的排布就是排在这里）真正跑起来。
    /// </summary>
    private static void Pump(int ms)
    {
        var until = Environment.TickCount64 + Math.Max(1, ms);
        while (Environment.TickCount64 < until)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            System.Threading.Thread.Sleep(1);
        }
    }

    private static void Bench1(string label, Stopwatch sw, Func<object> body)
    {
        sw.Restart();
        var r = body();
        Report(label, sw, r is null ? "" : "");
    }

    private static void Report(string label, Stopwatch sw, string extra = "")
        => Console.WriteLine($"[bench] {label,-52} {sw.Elapsed.TotalMilliseconds,8:0.0} ms{(extra.Length > 0 ? "  " + extra : "")}");
}
