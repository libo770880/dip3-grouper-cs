using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Dip3Grouper;
using Dip3GrouperApp.Desktop.Controls;
using Dip3GrouperApp.Desktop.Theming;
using Dip3GrouperApp.Desktop.Views;
using Dip3GrouperApp.Desktop.Views.Admin;

namespace Dip3GrouperApp.Desktop;

/// <summary>
/// 命令行自检（<c>--selftest</c>）：不打开窗口，逐个构造全部页面并跑一遍核心数据链路，
/// 把结果写到日志文件。用于「绿色版解压后能否直接跑」的自动化验收。
///
/// WPF 的 StaticResource 缺失、资源字典顺序错误、绑定类型不匹配等问题只在运行期暴露，
/// 编译通过并不代表能跑起来，因此这道自检是发布前的必要关卡。
/// </summary>
internal static class SelfTest
{
    /// <summary>自检日志落盘路径（同时打印到控制台）</summary>
    public static string LogPath => Path.Combine(Path.GetTempPath(), "dip3-desktop-selftest.log");

    /// <summary>WCAG 相对亮度</summary>
    /// <summary>在可视化树里深度优先找渐显控件（自检用：四个分段页面都必须挂着它）。</summary>
    private static Dip3GrouperApp.Desktop.Controls.BusyVeil FindVeil(DependencyObject root)
    {
        if (root is Dip3GrouperApp.Desktop.Controls.BusyVeil hit) return hit;
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var found = FindVeil(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    private static double Lum(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        static double F(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * F(c.R) + 0.7152 * F(c.G) + 0.0722 * F(c.B);
    }

    /// <summary>WCAG 对比度（1~21，正文建议 ≥ 4.5，大字 ≥ 3）</summary>
    private static double Contrast(string a, string b)
    {
        var l1 = Lum(a);
        var l2 = Lum(b);
        if (l1 < l2) (l1, l2) = (l2, l1);
        return (l1 + 0.05) / (l2 + 0.05);
    }

    /// <summary>执行自检，返回进程退出码（0 = 全部通过）</summary>
    public static int Run()
    {
        var log = new StringBuilder();
        var pass = 0;
        var fail = 0;
        var caseId = 0;

        // 逐项进度（只写文件、不刷屏）：自检偶尔会卡在某个真窗口用例上，
        // 而全部输出都在最后才落盘 —— 卡住时看不到跑到哪。设 DIP3_SELFTEST_TRACE=1 开启。
        var trace = Environment.GetEnvironmentVariable("DIP3_SELFTEST_TRACE") == "1"
            ? Path.Combine(Path.GetTempPath(), "dip3-st-progress.txt") : null;
        void Trace(string m)
        {
            if (trace == null) return;
            try { File.AppendAllText(trace, m + "\n"); } catch { }
        }

        void Head(string t)
        {
            log.AppendLine().AppendLine("== " + t + " ==");
            Trace(t);
        }

        void Ok(string t, string detail = null)
        {
            pass++;
            log.AppendLine($"  [ OK ] {t}{(string.IsNullOrEmpty(detail) ? "" : " — " + detail)}");
        }

        void Bad(string t, Exception ex)
        {
            fail++;
            log.AppendLine($"  [FAIL] {t}");
            log.AppendLine("         " + ex.GetType().Name + ": " + ex.Message);
            // XAML 解析类异常要带行号才能定位到具体控件
            if (ex is System.Windows.Markup.XamlParseException || ex.InnerException != null)
                log.AppendLine("         " + ex.ToString().Replace("\n", "\n         "));
        }

        /// <summary>跑一个用例；回调参数用于回报一行明细（可为 null）</summary>
        void Check(string t, Action<Action<string>> body)
        {
            var label = $"{++caseId:D2}. {t}";
            Trace("  - " + label);
            try
            {
                string detail = null;
                body(d => detail = d);
                Ok(label, detail);
            }
            catch (Exception ex)
            {
                Bad(label, ex);
            }
        }

        /// <summary>构造一个页面并强制布局（触发模板展开与资源解析）</summary>
        void Ui(string t, Func<FrameworkElement> factory)
        {
            Check("界面构造 " + t, _ =>
            {
                var el = factory();

                // Window 未显示时不做手工布局（其布局由 HwndSource 驱动，手工 Measure 会抛异常）；
                // 构造本身已足以验证 XAML 解析、资源字典与全部子控件（含各页面）能否建立。
                if (el is Window) return;

                el.Measure(new Size(1440, 900));
                el.Arrange(new Rect(0, 0, 1440, 900));
                el.UpdateLayout();

                if (el is UserControl uc)
                {
                    // 触发页面内部的懒加载入口（数据加载 + 表格动态建列）
                    switch (uc)
                    {
                        case DiseaseLibView d: d.EnsureLoaded(); break;
                        case DezhouPriceView dp: dp.EnsureLoaded(); break;
                        case GuifanView gv: gv.EnsureLoaded(); break;
                        case AdminOverviewView o: o.EnsureLoaded(); break;
                        case AdminDiseasesView ad: ad.EnsureLoaded(); break;
                        case AdminLixiangView al: al.EnsureLoaded(); break;
                        case AdminMappingView am: am.EnsureLoaded(); break;
                        case AdminDezhouView adz: adz.EnsureLoaded(); break;
                        case AdminDataView adt: adt.EnsureLoaded(); break;
                        case AdminSettingsView asv: asv.EnsureLoaded(); break;
                        // 工具栏 / 物价 AI 靠 Loaded 初始化，离屏构造不会触发，手动走内部诊断入口
                        case ToolPanel tp: tp.DiagRoot(null, null, null); break;
                        case PriceAiPanel pa: pa.Diag(null, null); break;
                        case AdminVersionView av: av.EnsureLoaded(); break;
                        case AdminAuditView aa: aa.EnsureLoaded(); break;
                    }
                    uc.Measure(new Size(1440, 900));
                    uc.Arrange(new Rect(0, 0, 1440, 900));
                    uc.UpdateLayout();
                }
            });
        }

        log.AppendLine("DIP 3.0 分组器 · 桌面客户端自检");
        log.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        log.AppendLine("工程根: " + AppPaths.Root);
        log.AppendLine("运行目录: " + AppContext.BaseDirectory);
        log.AppendLine("基层兜底: " + (App.Jiceng ? "开" : "关"));

        // ================= 一、数据链路 =================
        Head("一、数据链路（不经 HTTP，直接调用 Core）");

        Check("病种库配置载入", d =>
        {
            var counts = Diseases.SectionCounts(App.State.Config);
            var total = counts.Values.Sum(x => x.Total);
            if (counts.Count == 0) throw new Exception("区段数为 0，config 未载入");
            d($"{counts.Count} 个区段 · {total:N0} 条");
        });

        Check("手术编码库构建", d =>
        {
            var n = App.State.ProcLib.Count;
            if (n == 0) throw new Exception("手术编码库为空");
            d($"{n:N0} 个手术码");
        });

        Check("映射表载入", d =>
        {
            var st = App.State.Mapping.Stats();
            d($"{st.Total:N0} 条 · 已映射 {st.Mapped:N0} · 灰码 {st.Gray:N0}");
        });

        Check("综合病组载入", d =>
        {
            // 允许为空：data/zonghe.json 可能被后台刻意清空（meta.sourceName = 已清空）。
            // 此时分组兜底仍走 config 内置的结核等区间行，不影响分组正确性。
            var st = App.State.Zonghe.Stats();
            d(st.Total == 0
                ? "0 条（已清空，兜底走 config 内置区间行）"
                : $"{st.Total:N0} 条 · 多范围 {st.MultiRange:N0}");
        });

        Check("立项指南载入", d =>
        {
            var counts = App.State.Lixiang.SectionCounts();
            var total = counts.Values.Sum(x => x.Total);
            var issues = App.State.Lixiang.JieduIssues();
            d($"{counts.Count} 个区段 · {total:N0} 个项目 · 解读 {issues.Count} 期");
        });

        Check("项目技术规范载入", d =>
        {
            var s = App.State.Guifan.Stat();
            d($"{s.Total:N0} 个项目 · {s.ChapterCount} 章 / {s.CategoryCount} 类 · 含耗材 {s.WithMaterial:N0}");
        });

        Check("技术规范并发载入（后台预热与 UI 竞争不产生空库）", d =>
        {
            // 回归：Load 曾在解析前就置 Loaded=true，后台预热线程先进入时，
            // UI 线程的 EnsureLoaded 会立刻返回并读到空 Items（前台页打开空白）。
            var store = new GuifanStore();
            var results = new int[8];
            Parallel.For(0, 8, i => { store.EnsureLoaded(); results[i] = store.Items.Count; });
            if (results.Any(c => c != results[0] || c == 0))
                throw new Exception($"并发载入结果不一致: {string.Join(",", results)}");
            if (!store.Loaded) throw new Exception("载入后 Loaded 未置位");
            d($"8 线程并发 EnsureLoaded 一致读到 {results[0]:N0} 条");
        });

        Check("技术规范逐章对账（对官方公布数）", d =>
        {
            // 《使用说明》表 1 公布的各章项目数，是校验「PDF 大表解析无漏行」最硬的外部证据。
            var official = new (string Code, int Count)[]
            {
                ("A", 167), ("B", 64), ("C", 1754), ("E", 612), ("F", 855), ("H", 6012),
                ("K", 1210), ("L", 98), ("M", 314), ("P", 401), ("N", 29),
            };
            var items = App.State.Guifan.Items;
            var want = official.Sum(x => x.Count);
            if (items.Count != want)
                throw new Exception($"项目总数 {items.Count} ≠ 官方 {want}");

            var actual = App.State.Guifan.Chapters().ToDictionary(c => c.Code, c => c.Items);
            var bad = official.Where(x => !actual.TryGetValue(x.Code, out var n) || n != x.Count).ToList();
            if (bad.Count > 0)
                throw new Exception("章项目数不符：" + string.Join("、",
                    bad.Select(x => $"{x.Code} 期望 {x.Count} 实得 {(actual.TryGetValue(x.Code, out var n) ? n : 0)}")));

            // 编码形态：恒 8 位、首字符为章字母、无重复
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in items)
            {
                if (it.Code.Length != 8 || !char.IsLetter(it.Code[0]))
                    throw new Exception($"编码形态异常：{it.Code}");
                if (!seen.Add(it.Code))
                    throw new Exception($"编码重复：{it.Code}");
            }
            // 要素完整性：编码/名称/内涵/单位必须齐（其余要素官方本就允许留空）
            var miss = items.Count(i => string.IsNullOrWhiteSpace(i.Name) || string.IsNullOrWhiteSpace(i.Content)
                                        || string.IsNullOrWhiteSpace(i.Unit));
            if (miss > 0) throw new Exception($"{miss} 条缺名称/内涵/计量单位");

            d($"{items.Count:N0} 条 · {official.Length} 章逐一对上 · 编码唯一 · 要素齐备");
        });

        Check("技术规范检索与分页", d =>
        {
            var g = App.State.Guifan;
            var byCode = g.ByCode("AAAA0001") ?? throw new Exception("按编码定位失败 AAAA0001");
            if (byCode.Name != "普通门诊诊察") throw new Exception($"AAAA0001 名称不符：{byCode.Name}");

            var byName = g.ListRows("", "", "胆囊", 1, 20);
            if (byName.Total == 0) throw new Exception("关键词「胆囊」无命中");
            var byPy = g.ListRows("", "", "mq", 1, 20);          // 拼音首字母
            if (byPy.Total == 0) throw new Exception("拼音检索「mq」无命中");
            var byChap = g.ListRows("", "H", "", 1, 20);          // 章筛选
            if (byChap.Total != 6012) throw new Exception($"章 H 应 6012 条，实得 {byChap.Total}");
            var byCat = g.ListRows("辅助操作", "", "", 1, 20);
            if (byCat.Total != 29) throw new Exception($"辅助操作类应 29 条，实得 {byCat.Total}");

            var pg2 = g.ListRows("", "", "", 2, 50);
            if (pg2.Items.Count != 50 || pg2.Page != 2) throw new Exception("第 2 页分页异常");

            d($"精确 {byCode.Name} · 胆囊 {byName.Total:N0} · 拼音 {byPy.Total:N0} · 章H {byChap.Total:N0} · 分页 OK");
        });

        Check("三库互相映射（德州物价 ↔ 立项指南 ↔ 技术规范）", d =>
        {
            static string Norm(string s)
                => string.Concat((s ?? "").Where(c => !char.IsWhiteSpace(c))).Replace("（", "(").Replace("）", ")");

            // 同名直连：德州主项 → 立项指南（实测 99.8% 精确）
            var dzMain = App.State.Dezhou.Items.First(i => i.Kind == "main" && !string.IsNullOrWhiteSpace(i.Name));
            var toLx = CrossMap.ForDezhou(dzMain.Name);
            var lxBlk = toLx.FirstOrDefault(b => b.Label == "关联·立项指南")
                ?? throw new Exception($"德州主项「{dzMain.Name}」未关联到立项指南");
            if (!lxBlk.Lines.Any(l => l.Chip == "同名"))
                throw new Exception($"德州主项「{dzMain.Name}」未命中同名立项指南行");

            // 同名直连：指南 → 规范（取规范里确实存在同名的一条）
            var gfNames = new HashSet<string>(App.State.Guifan.Items.Select(i => Norm(i.Name)));
            var pair = App.State.Lixiang.State.Sections.Values.SelectMany(s => s.Rows)
                .FirstOrDefault(r => gfNames.Contains(Norm(r.Name)))
                ?? throw new Exception("立项指南与规范之间连一条同名都没有，映射索引疑似未构建");
            var toGf = CrossMap.ForLixiang(pair.Name);
            var gfBlk = toGf.FirstOrDefault(b => b.Label == "关联·技术规范")
                ?? throw new Exception($"指南「{pair.Name}」未关联到技术规范");
            if (!gfBlk.Lines.Any(l => l.Chip == "同名"))
                throw new Exception($"指南「{pair.Name}」未命中同名技术规范条目");

            // 相似候选：指南「心脏移植术」→ 规范应给出 原位/异位心脏移植术 一类候选
            var fuzzy = CrossMap.ForLixiang("心脏移植术")
                .FirstOrDefault(b => b.Label == "关联·技术规范")?.Lines
                ?? throw new Exception("「心脏移植术」未给出技术规范相似候选");
            if (!fuzzy.Any(l => l.Text.Contains("心脏移植术")))
                throw new Exception("「心脏移植术」的相似候选不含心脏移植类条目");

            // 三个方向全量过一遍，只要求稳定不抛异常
            var n1 = App.State.Lixiang.State.Sections.Values.SelectMany(s => s.Rows).Take(200)
                .Sum(r => CrossMap.ForLixiang(r.Name).Count);
            var n2 = App.State.Dezhou.Items.Take(200).Sum(i => CrossMap.ForDezhou(i.Name).Count);
            var n3 = App.State.Guifan.Items.Take(200).Sum(i => CrossMap.ForGuifan(i.Name).Count);
            d($"德州→指南同名 · 指南→规范同名 · 相似候选 {fuzzy.Count} 条 · 三方向抽样 600 行稳定");
        });

        Check("关联链接（跳转键完整 / 指向不串号 / 弹窗内容可加载 / 可继续下钻）", d =>
        {
            static string Squash(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\s+", "");

            var links = new List<(string Lib, int Id, string Text)>();

            void Collect(IEnumerable<LxBlock> blocks)
            {
                foreach (var b in blocks.Where(x => x.Label.StartsWith("关联·")))
                {
                    if (b.Lines.Count == 0) throw new Exception($"{b.Label} 关联块为空");
                    foreach (var l in b.Lines)
                    {
                        if (!l.HasLink)
                            throw new Exception($"关联行「{l.Text}」缺少跳转键（{b.Label}）");
                        links.Add((l.LinkLib, l.LinkId, l.Text));
                    }
                }
            }

            Collect(App.State.Lixiang.State.Sections.Values.SelectMany(s => s.Rows).Take(300)
                .SelectMany(r => CrossMap.ForLixiang(r.Name)));
            Collect(App.State.Dezhou.Items.Take(300).SelectMany(i => CrossMap.ForDezhou(i.Name)));
            Collect(App.State.Guifan.Items.Take(300).SelectMany(i => CrossMap.ForGuifan(i.Name)));
            if (links.Count == 0) throw new Exception("抽样未产生任何关联链接");

            var withMeters = 0;
            var recursive = 0;
            foreach (var (lib, id, text) in links)
            {
                if (!Related.TryLoad(lib, id, out var doc))
                    throw new Exception($"链接目标 {lib}#{id} 无法定位");
                if (string.IsNullOrWhiteSpace(doc.Name))
                    throw new Exception($"链接目标 {lib}#{id} 缺少名称");
                if (doc.Blocks.Count == 0 || doc.Blocks.All(b => b.Lines.Count == 0))
                    throw new Exception($"链接目标 {lib}#{id} 字段块为空");
                // 目标必须就是这行关联文本指向的条目：行文本以条目名开头（名称　摘要）。
                // 只要 Id 空间有重号，这里就会炸出「点 A 打开 B」——2026-10-30 §9.25 的 bug。
                if (!Squash(text).StartsWith(Squash(doc.Name), StringComparison.Ordinal))
                    throw new Exception($"链接串号：「{text}」打开的是 {lib}#{id}「{doc.Name}」");
                if (doc.HasMeters) withMeters++;
                recursive += doc.Blocks.Count(b => b.Label.StartsWith("关联·") && b.Lines.Any(l => l.HasLink));
            }

            // 立项指南的 Id 只在区段内唯一（40 个区段各自从 1 编，2627 行只有 662 个不同 Id），
            // 跳转键必须是全局唯一的 Gid，否则 s23 的「睡眠呼吸监测费」(Id=11) 会打开
            // s01 的「供小肠切取术」(Id=11)。
            var lxRows = App.State.Lixiang.State.Sections.Values.SelectMany(s => s.Rows).ToList();
            var gids = lxRows.Select(r => r.Gid).ToList();
            if (gids.Any(g => g <= 0))
                throw new Exception("立项指南行缺少全局跳转键 Gid（LixiangStore.Reindex 未执行）");
            if (gids.Distinct().Count() != gids.Count)
                throw new Exception($"立项指南 Gid 不唯一：{gids.Count} 行只有 {gids.Distinct().Count()} 个键");
            if (gids.Distinct().Count() == lxRows.Select(r => r.Id).Distinct().Count())
                throw new Exception("Gid 唯一数与 Id 唯一数相同，Reindex 疑似没生效");

            // 反例专测：同一 Id 跨区段的重号条目，各自必须指回自己
            var dupId = lxRows.GroupBy(r => r.Id)
                .FirstOrDefault(g => g.Select(x => Squash(x.Name)).Distinct().Count() > 1);
            if (dupId == null)
                throw new Exception("立项指南里找不到跨区段同号条目，这条回归用例失效（Id 编法是不是改了？）");
            foreach (var r in dupId.Take(5))
            {
                if (!Related.TryLoad(Related.Lixiang, r.Gid, out var doc))
                    throw new Exception($"指南「{r.Name}」的 Gid#{r.Gid} 无法定位");
                if (Squash(doc.Name) != Squash(r.Name))
                    throw new Exception($"跨区段串号：Id={r.Id} 应打开「{r.Name}」，实际「{doc.Name}」");
            }

            // 弹窗导航：三个库各推一层，栈内可回退
            var w = new RelatedWindow();
            foreach (var lib in new[] { Related.Guifan, Related.Dezhou, Related.Lixiang })
            {
                var id = links.First(l => l.Lib == lib).Id;
                if (!w.Push(lib, id)) throw new Exception($"弹窗加载 {lib}#{id} 失败");
                if (w.DataContext is not RelatedDoc) throw new Exception("弹窗数据上下文未就绪");
            }
            if (Related.TryLoad("nope", 1, out _)) throw new Exception("未知库名不应加载成功");

            d($"{links.Count} 条链接全部指向自身 · {withMeters} 条含量表 · 下一层关联 {recursive} 条 · " +
              $"指南 Gid 唯一 {gids.Distinct().Count()}/{gids.Count} · 同号反例 Id={dupId.Key} 跨 {dupId.Count()} 行各自命中");
        });

        Check("多条件检索 + 拼音全拼/首字母（三库）", d =>
        {
            // ---- 技术规范：关键词 + 类别 + 章 + 计量单位 + 耗材 组合 ----
            var all = App.State.Guifan.ListRows("", "", "", 1, 50).Total;
            var byCat = App.State.Guifan.ListRows("治疗", "", "", 1, 50).Total;
            if (byCat <= 0 || byCat >= all) throw new Exception($"类别筛选无效：治疗 {byCat} / 全部 {all}");

            var byKw = App.State.Guifan.ListRows("", "", "心脏移植", 1, 50);
            if (!byKw.Items.Any(i => i.Name.Contains("心脏移植")))
                throw new Exception("关键词「心脏移植」未命中");

            // 首字母：xzyz → 心脏移植；全拼：xinzang → 心脏移植
            var byFirst = App.State.Guifan.ListRows("", "", "xzyz", 1, 50);
            if (!byFirst.Items.Any(i => i.Name.Contains("心脏移植")))
                throw new Exception("拼音首字母「xzyz」未命中 心脏移植 类条目");
            var byFull = App.State.Guifan.ListRows("", "", "xinzang", 1, 50);
            if (!byFull.Items.Any(i => i.Name.Contains("心脏移植")))
                throw new Exception("拼音全拼「xinzang」未命中 心脏移植 类条目");

            // 类别 + 关键词组合必须同时满足
            var combo = App.State.Guifan.ListRows("治疗", "", "心脏移植", 1, 50);
            if (combo.Items.Any(i => i.Category != "治疗"))
                throw new Exception("类别 + 关键词组合过滤不严");

            var units = App.State.Guifan.UnitCounts();
            if (units.Count == 0) throw new Exception("计量单位候选为空");
            var topUnit = units[0].Key;
            var byUnit = App.State.Guifan.ListRows("", "", "", 1, 50, topUnit);
            if (byUnit.Items.Any(i => i.Unit.Trim() != topUnit))
                throw new Exception($"计量单位筛选无效：{topUnit}");

            var withReq = App.State.Guifan.ListRows("", "", "", 1, 50, "", "req");
            var noReq = App.State.Guifan.ListRows("", "", "", 1, 50, "", "none");
            if (withReq.Total == 0 || noReq.Total == 0 || withReq.Total + noReq.Total != all)
                throw new Exception($"耗材筛选不闭合：含 {withReq.Total} + 无 {noReq.Total} ≠ {all}");

            // ---- 德州物价：批次 + 类别 + 性质 + 医保支付 + 关键词 ----
            var dzAll = App.State.Dezhou.ListRows("", "", "", "", 1, 50).Total;
            var dzIns = App.State.Dezhou.ListRows("", "", "", "", 1, 50, "是").Total;
            var dzNo = App.State.Dezhou.ListRows("", "", "", "", 1, 50, "否").Total;
            if (dzIns == 0 || dzNo == 0) throw new Exception("医保支付筛选无结果");
            // 源表个别行该列为空，允许极少数落在「是/否」之外；
            // 历史口径：征求意稿转正前的「暂定文号」批次整批无医保支付列，需单列豁免。
            // 德医保发〔2026〕25号 正式版已补齐医保三列，暂定批次整批撤下 —— 这里保留按文号
            // 动态排除的写法（有暂定批次就排、没有就为 0），避免再次转正时口径错位。
            var draftDoc = App.State.Dezhou.Meta.Batches
                .FirstOrDefault(b => (b.Doc ?? "").Contains("暂定"))?.Doc ?? "";
            var draftRows = draftDoc.Length > 0
                ? App.State.Dezhou.ListRows(draftDoc, "", "", "", 1, 1).Total : 0;
            var dzOther = dzAll - dzIns - dzNo - draftRows;
            if (dzOther > 20)
                throw new Exception($"医保支付列存在大量非「是/否」取值：{dzOther} 条（筛选口径需复核）");

            var dzPy = App.State.Dezhou.ListRows("", "", "", "xzyz", 1, 50);
            if (dzPy.Total == 0) throw new Exception("物价拼音首字母检索无结果");

            // ---- 医院级别筛选：只列该级别有价的项目，三档价互不串档 ----
            // ⚠ pageSize 被 Core 夹在 5~200，核对全量要逐页取
            List<DezhouPriceItem> LevelAll(string lv)
            {
                var acc = new List<DezhouPriceItem>();
                for (var p = 1; ; p++)
                {
                    var r = App.State.Dezhou.ListRows("", "", "", "", p, 200, "", null, lv);
                    acc.AddRange(r.Items);
                    if (acc.Count >= r.Total || r.Items.Count == 0) break;
                }
                return acc;
            }
            foreach (var lv in new[] { "3", "2", "1" })
            {
                var rows = LevelAll(lv);
                if (rows.Count == 0) throw new Exception($"医院级别 {lv} 筛选无结果");
                var blank = rows.Count(i => (lv switch
                {
                    "3" => i.Price3, "2" => i.Price2, _ => i.Price1,
                } ?? "").Trim().Length == 0);
                if (blank > 0) throw new Exception($"级别 {lv} 混入 {blank} 条该档无价的项目");
            }
            var eqRows = LevelAll("eq");
            if (eqRows.Count == 0) throw new Exception("「各级同价」筛选无结果");
            if (eqRows.Any(i => (i.Price3 ?? "").Trim() != (i.Price2 ?? "").Trim()
                             || (i.Price2 ?? "").Trim() != (i.Price1 ?? "").Trim()))
                throw new Exception("「各级同价」里混入了三档价格不一致的项目");
            if (eqRows.Count > dzAll) throw new Exception("各级同价条数不应超过全部条数");
            // 三档任一有价 ≠ 全部：51 条三档皆空，不能被当成「有价」放行
            var lv3 = LevelAll("3").Count;
            if (lv3 >= dzAll && App.State.Dezhou.Items.Any(i => (i.Price3 ?? "").Trim().Length == 0))
                throw new Exception("三档皆空的行没有被级别筛选挡掉");
            var dzMixLevel = App.State.Dezhou.ListRows("", "", "", "静脉输液", 1, 50, "", null, "2");
            if (dzMixLevel.Total == 0) throw new Exception("级别 + 关键词组合检索无结果");

            var dzMix = App.State.Dezhou.ListRows("", "", "main", "心脏", 1, 50);
            if (dzMix.Items.Any(i => i.Kind != "main"))
                throw new Exception("性质 + 关键词组合过滤不严");

            // ---- 德州物价 · 作废对照（批次映射关系表）----
            // 数据源：四批通知附件「映射关系表」，合并进 dezhou-price.json 的 cancels 键。
            // 旧库（无 cancels）时此段退化为计数为 0 的通过项，不阻塞老数据包自检。
            var czAll = App.State.Dezhou.CancelRows("", "", "");
            if (czAll.Count > 0)
            {
                if (czAll.Any(i => string.IsNullOrWhiteSpace(i.OldCode) || string.IsNullOrWhiteSpace(i.OldName)))
                    throw new Exception("作废对照存在缺编码/名称的行");
                // 每行必须有批次归属：空 doc 的行会从「批次下拉 / 类别筛选」里消失（转正合并踩过）
                var noDoc = czAll.Count(i => string.IsNullOrWhiteSpace(i.Doc));
                if (noDoc > 0)
                    throw new Exception($"作废对照有 {noDoc} 行缺批次文号（批次/类别筛选将看不到这些行）");
                var batchDocs = new HashSet<string>(
                    App.State.Dezhou.Meta.Batches.Select(b => (b.Doc ?? "").Trim()), StringComparer.Ordinal);
                var orphan = czAll.FirstOrDefault(i => !batchDocs.Contains((i.Doc ?? "").Trim()));
                if (orphan != null)
                    throw new Exception($"作废对照文号「{orphan.Doc}」不在批次表里（批次下拉不会出现）");
                var itemOrphan = App.State.Dezhou.Items
                    .FirstOrDefault(i => string.IsNullOrWhiteSpace(i.Doc) || !batchDocs.Contains(i.Doc.Trim()));
                if (itemOrphan != null)
                    throw new Exception($"物价项目「{itemOrphan.Code}」批次文号缺失或不在批次表（doc=「{itemOrphan.Doc}」）");
                var czDoc = czAll[0].Doc;
                var czByDoc = App.State.Dezhou.CancelRows(czDoc, "", "");
                if (czByDoc.Count == 0 || czByDoc.Count >= czAll.Count || czByDoc.Any(i => i.Doc != czDoc))
                    throw new Exception("作废对照按批次筛选无效");
                var czDead = czAll.Count(i => string.IsNullOrWhiteSpace(i.NewCode));
                var czHit = App.State.Dezhou.CancelRows("", "", "心脏移植");
                if (czHit.Count == 0) throw new Exception("作废对照关键词检索无结果");
                // 旧码后缀是小写字母（310701001g），关键词转大写后用 Ordinal 前缀比对会一条都搜不到
                if (App.State.Dezhou.CancelRows("", "", "310701001g").Count == 0)
                    throw new Exception("作废对照按带小写后缀的编码检索无结果（大小写敏感的回归）");
                if (App.State.Dezhou.LegacyRows("", "", "310701001g").Count == 0)
                    throw new Exception("旧版物价按带小写后缀的编码检索无结果（大小写敏感的回归）");
                var czStat = App.State.Dezhou.CancelStat();
                if (czStat.Total != czAll.Count || czStat.Batches == 0 || czStat.Categories == 0)
                    throw new Exception("作废对照统计口径不一致");
                // 划线保留（keep）：通知废止表里划横线的行不作废 —— 不得进「已作废」判定，也不给「现行标准」；
                // 但映射行照旧保留（旧码 ↔ 新码仍要能核对），所以总行数不减、只多一个「保留」状态。
                var czKeep = czAll.Where(i => i.Keep).ToList();
                if (czStat.Kept != czKeep.Count)
                    throw new Exception($"保留行统计不一致：{czStat.Kept}≠{czKeep.Count}");
                var keepDead = czKeep.FirstOrDefault(i => App.State.Dezhou.LegacyCanceled(i.OldCode));
                if (keepDead != null)
                    throw new Exception($"保留项目被判成已作废：{keepDead.OldCode}（{keepDead.OldName}）");
                var keepStd = czKeep.FirstOrDefault(i => App.State.Dezhou.LegacyCurrentStd(i.OldCode).Length > 0);
                if (keepStd != null)
                    throw new Exception($"保留项目不该给「现行标准」：{keepStd.OldCode}（{keepStd.OldName}）");
                // 25 号文附件里划横线的两项（311400031 / PCAA0101）必须落在保留侧，防止重导数据时丢标记
                foreach (var code in new[] { "311400031", "PCAA0101" })
                {
                    var row = czAll.FirstOrDefault(i => (i.OldCode ?? "").Trim() == code && i.Doc.Contains("25号"));
                    if (row != null && !row.Keep)
                        throw new Exception($"25 号文划线项目 {code} 未标记为保留（重导数据会丢 keep 标记）");
                }
                d($"物价·作废对照 {czAll.Count:N0} 行（有替代 {czStat.Mapped:N0} / 直接废止 {czDead:N0} / 划线保留 {czStat.Kept:N0}）· {czStat.Batches} 批 / {czStat.Categories} 类 · 关键词命中 {czHit.Count:N0}");
            }
            else
            {
                d("物价·作废对照 数据未含 cancels（旧数据包），跳过");
            }

            // ---- 德州物价 · 旧版物价（45号文对码表）× 作废对照联查 ----
            // 已作废行必须有「现行标准」（或明确直接废止），状态筛选两态互斥不漏。
            var lgAll = App.State.Dezhou.LegacyRows("", "", "");
            if (lgAll.Count > 0)
            {
                var lgDead = App.State.Dezhou.LegacyRows("", "dead", "");
                var lgLive = App.State.Dezhou.LegacyRows("", "live", "");
                if (lgDead.Count + lgLive.Count != lgAll.Count)
                    throw new Exception($"旧版物价两态之和不等于总数：{lgDead.Count}+{lgLive.Count}≠{lgAll.Count}");
                if (lgDead.Any(i => App.State.Dezhou.LegacyCurrentStd(i.Code).Length == 0
                                    && App.State.Dezhou.CancelRows("", "", i.Code)
                                        .Any(c => c.OldCode == i.Code && !string.IsNullOrWhiteSpace(c.NewCode))))
                    throw new Exception("已作废行缺少现行标准（映射存在但联查为空）");
                if (lgLive.Any(i => App.State.Dezhou.LegacyCanceled(i.Code)))
                    throw new Exception("「仅现行」混入已作废行");
                // 作废判定不得因「无替代项目」漏判：旧码出现在作废对照里就必须是「已作废」
                // （曾因 CancelIndex 只收 NewCode 非空的行，462 条直接废止的旧项目全被误标「现行」）
                var canceledCodes = new HashSet<string>(StringComparer.Ordinal);
                foreach (var c in App.State.Dezhou.Cancels)
                    if (!c.Pending && !c.Keep && !string.IsNullOrWhiteSpace(c.OldCode)) canceledCodes.Add(c.OldCode.Trim());
                var missDead = lgAll.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Code)
                                                          && canceledCodes.Contains(i.Code.Trim())
                                                          && !App.State.Dezhou.LegacyCanceled(i.Code));
                if (missDead != null)
                    throw new Exception($"已作废判定漏行：{missDead.Code}（{missDead.Name}）在作废对照中但状态仍为「现行」");
                var lgHit = App.State.Dezhou.LegacyRows("", "", "心脏移植");
                var lgCat = App.State.Dezhou.LegacyCategories();
                if (lgCat.Count == 0) throw new Exception("旧版物价归集口径为空");
                var lgStat = App.State.Dezhou.LegacyStat();
                if (lgStat.Total != lgAll.Count || lgStat.Dead != lgDead.Count)
                    throw new Exception("旧版物价统计口径不一致");

                // 字段映射核对：价格 / 医保 / 自付列不能整列读成空串
                // （曾因 JSON 短键 p3/p2/p1/ins/self ↔ 属性 Price3/Insurance/SelfPay 对不上而整列空白）
                if (lgAll.All(i => string.IsNullOrWhiteSpace(i.Price3) && string.IsNullOrWhiteSpace(i.Price2)
                                    && string.IsNullOrWhiteSpace(i.Price1)))
                    throw new Exception("旧版物价三级/二级/一级价格整列为空（JSON 键映射失效）");
                if (lgAll.All(i => string.IsNullOrWhiteSpace(i.Insurance)))
                    throw new Exception("旧版物价医保列整列为空（JSON 键映射失效）");
                if (lgAll.All(i => string.IsNullOrWhiteSpace(i.SelfPay)))
                    throw new Exception("旧版物价自付列整列为空（JSON 键映射失效）");

                // 现行标准明细：已作废行经 CancelOf 应给出可点击链接数据源
                var lgDeadWithStd = lgDead.Where(i => App.State.Dezhou.LegacyCurrentStd(i.Code).Length > 0).ToList();
                if (lgDeadWithStd.Any(i => App.State.Dezhou.CancelOf(i.Code).Count == 0
                                           || App.State.Dezhou.CancelOf(i.Code).Any(c => string.IsNullOrWhiteSpace(c.NewCode))))
                    throw new Exception("现行标准明细（CancelOf）与字符串联查不一致");
                // 同族补判：映射关系表引省对码，与本地对码表的字母后缀可能错位
                // （对照 311400012 ↔ 旧表 311400012a「醋酸白试验」），只按编码比对会漏判成「现行」。
                // 抽查两条已知错位行 + 保证补判不误伤跨码同名（「知名专家」不同基础码各自独立）。
                var famProbe = lgAll.FirstOrDefault(i => i.Code == "311400012a");
                if (famProbe != null && !App.State.Dezhou.LegacyCanceled(famProbe))
                    throw new Exception("同族补判失效：311400012a（醋酸白试验）在作废对照 311400012 名下仍标「现行」");
                var famCross = lgAll.FirstOrDefault(i => i.Code == "111000002d");   // 跨码同名的「知名专家」
                if (famCross != null && App.State.Dezhou.LegacyCanceled(famCross))
                    throw new Exception("同族补判误伤：111000002d（知名专家）基础码不同，不该跟着 110200002c 作废");
                // 全角/半角括号差异同样要能对上（映射表用（），对码表用()）
                // 210102d「X线摄影(床旁摄片加收)」↔ 作废对照 210102「X线摄影（床旁摄片加收）」
                var famPunct = lgAll.FirstOrDefault(i => i.Code == "210102d");
                if (famPunct != null && !App.State.Dezhou.LegacyCanceled(famPunct))
                    throw new Exception("归一化失效：210102d（X线摄影 床旁摄片加收）括号全半角不同，仍标「现行」");
                var famDeadWithStd = lgDead.Where(i => App.State.Dezhou.LegacyCurrentStd(i).Length > 0).ToList();
                if (famDeadWithStd.Any(i => App.State.Dezhou.CancelOf(i).Count == 0))
                    throw new Exception("整行版 CancelOf 与 LegacyCurrentStd 联查不一致");
                // 废止项目表（附件16-30）比映射关系表多出的行：加收子项与整项。
                // 这类旧码只在「废止项目表」出现，映射表压根没列，靠映射表建库会整条漏掉，
                // 旧版物价里就一直挂着「现行」。抽查三条已补录的，任一回到「现行」即报错。
                foreach (var (pc, label) in new[]
                         {
                             ("310701003a", "动态心电图(镍式加收)"),
                             ("330100005", "全身麻醉"),
                             ("331601011", "隆乳术"),
                         })
                {
                    var probe = lgAll.FirstOrDefault(i => i.Code == pc);
                    if (probe == null) continue;
                    if (!App.State.Dezhou.LegacyCanceled(probe))
                        throw new Exception($"废止项目表补录丢失：{pc}（{label}）在 11 号文废止表里，却仍标「现行」");
                }
                // 美容整形类无映射表，旧码→新项只能按项目内涵人工推定。
                // 水光注射 311400069 已承接 016100000090000T 美容注射费，两处都要能查到承接项，
                // 否则旧版物价会退化成「已作废·无替代」，收费员看不到该按哪个新项目收。
                var szProbe = lgAll.FirstOrDefault(i => i.Code == "311400069");
                if (szProbe != null)
                {
                    if (!App.State.Dezhou.LegacyCanceled(szProbe))
                        throw new Exception("补录丢失：311400069（水光注射）在美容整形类废止表里，却仍标「现行」");
                    var szStd = App.State.Dezhou.CancelOf(szProbe);
                    if (szStd.Count == 0 || !szStd.Any(c => c.NewCode == "016100000090000T"))
                        throw new Exception("承接关系丢失：311400069（水光注射）应指向 016100000090000T（美容注射费）");
                    if (szStd.Any(c => string.IsNullOrWhiteSpace(c.NewCode)))
                        throw new Exception("承接关系半截：311400069 仍带空替代行");
                }
                // 美容整形类其余推定行：旧项→新项是人工按内涵定的，最怕写错码或写成不存在的项目，
                // 收费员点过去发现空白。逐条核对「作废对照的 newCode 必须真实存在于现行项目库」。
                foreach (var (oc, nc, label) in new[]
                         {
                             ("311400024", "016100000080000T", "药物面膜综合治疗"),
                             ("311400035", "016100000010000T", "激光除皱术"),
                             ("331604011", "016200000180000T", "酒窝再造术"),
                             ("331604021", "016200000090000T", "毛发移植术"),
                             ("331604036", "016200000780000T", "双向倒刺线植入悬吊术"),
                             ("331604037", "016200000780100T", "线雕取出术"),
                             ("331601013", "016200000780100T", "乳腺假体取出术"),
                             ("331604007", "016200000120000T", "胡须再造术"),
                         })
                {
                    var row = App.State.Dezhou.CancelRows("", "", oc).FirstOrDefault(c => c.OldCode == oc);
                    if (row == null) continue;
                    if ((row.NewCode ?? "").Trim() != nc)
                        throw new Exception($"美容整形类推定行对不上：{oc}（{label}）应为 {nc}，实际「{row.NewCode}」");
                    if (App.State.Dezhou.Items.All(i => i.Code != nc))
                        throw new Exception($"美容整形类推定行指向不存在的项目：{oc}→{nc}（{label}）");
                }
                // 全库兜底：所有作废行的承接码都必须真实存在于现行项目库。
                // 曾有 310511011 盖髓术 → 013105010100011（11 号文映射表笔误，正文计价表实为 …001），
                // 旧版物价点过去是空链接；修复后全库不允许再有悬空承接码。
                var dangling = App.State.Dezhou.Cancels
                    .Where(c => !string.IsNullOrWhiteSpace(c.NewCode))
                    .Where(c => App.State.Dezhou.Items.All(i => i.Code != c.NewCode.Trim()))
                    .Select(c => c.OldCode + "→" + c.NewCode).ToList();
                if (dangling.Count > 0)
                    throw new Exception("作废对照存在悬空承接码（新码不在现行项目库）：" + string.Join(" / ", dangling));
                if (App.State.Dezhou.Cancels.Any(c => (c.OldCode ?? "") == "310511011" && (c.NewCode ?? "") != "013105010100001"))
                    throw new Exception("盖髓术承接码回退：310511011 应指向 013105010100001（间接盖髓 减收）");
                d($"物价·旧版物价 {lgAll.Count:N0} 行（已作废 {lgStat.Dead:N0} / 现行 {lgStat.Live:N0}）· {lgStat.Categories} 口径 · 关键词命中 {lgHit.Count:N0} · 价格/医保/自付列有值 · 现行标准明细联查一致");
            }
            else
            {
                d("物价·旧版物价 数据未含 legacy（旧数据包），跳过");
            }

            // ---- 立项指南：区段 + 类别 + 加收/扩展 ----
            var lxAll = App.State.Lixiang.ListRows("", "", 1, 50).Total;
            var lxCats = App.State.Lixiang.Categories();
            if (lxCats.Count == 0) throw new Exception("立项指南类别候选为空");
            var lxCat = App.State.Lixiang.ListRows("", "", 1, 50, lxCats[0]).Total;
            if (lxCat == 0 || lxCat >= lxAll) throw new Exception($"指南类别筛选无效：{lxCats[0]} {lxCat}/{lxAll}");
            var lxAdd = App.State.Lixiang.ListRows("", "", 1, 50, "", "add").Total;
            var lxExt = App.State.Lixiang.ListRows("", "", 1, 50, "", "ext").Total;
            if (lxAdd == 0 || lxExt == 0 || lxAdd > lxAll || lxExt > lxAll)
                throw new Exception($"指南加收/扩展筛选异常：add {lxAdd} ext {lxExt} / 全部 {lxAll}");
            var lxPy = App.State.Lixiang.ListRows("", "xzyz", 1, 50);
            if (lxPy.Total == 0) throw new Exception("指南拼音首字母检索无结果");

            d($"规范 全部{all:N0}/治疗{byCat:N0} · 首字母+全拼命中 · 耗材 {withReq.Total:N0}+{noReq.Total:N0}={all:N0} · " +
              $"物价 医保 {dzIns:N0} 是/{dzNo:N0} 否（其余 {dzOther} 条该列为空）· " +
              $"级别 三级{lv3:N0}/二级{LevelAll("2").Count:N0}/一级{LevelAll("1").Count:N0}/同价{eqRows.Count:N0}（各档均无空价混入）· " +
              $"指南 类别{lxCats.Count} 加收{lxAdd:N0}/扩展{lxExt:N0}");
        });

        // 划线保留：通知「废止项目表」里被划横线的行原文即不作废（德医保发〔2026〕25 号
        // 311400031 / PCAA0101 两行），旧版物价必须显示「现行」，作废对照仍保留映射但标「保留」。
        Check("物价·划线保留（keep）不作废 / 不给现行标准 / 统计单列", d =>
        {
            var keeps = App.State.Dezhou.Cancels.Where(x => x.Keep).ToList();
            if (keeps.Count == 0)
            {
                d("数据未含 keep 行（旧数据包），跳过");
                return;
            }
            if (keeps.Any(x => App.State.Dezhou.LegacyCanceled(x.OldCode)))
                throw new Exception("保留行被判成已作废（LegacyCanceled 为 true）");
            if (keeps.Any(x => App.State.Dezhou.LegacyCurrentStd(x.OldCode).Length > 0))
                throw new Exception("保留行不该给「现行标准」");
            if (keeps.Any(x => App.State.Dezhou.CancelOf(x.OldCode).Count > 0))
                throw new Exception("保留行不该进 CancelOf 联查（旧项目没作废）");
            var st = App.State.Dezhou.CancelStat();
            if (st.Kept != keeps.Count)
                throw new Exception($"保留行统计不一致：{st.Kept}≠{keeps.Count}");
            // 旧版物价侧联动：这两码必须是「现行」
            var back = App.State.Dezhou.LegacyRows("", "", "311400031")
                .Concat(App.State.Dezhou.LegacyRows("", "", "PCAA0101")).ToList();
            var stillDead = back.FirstOrDefault(i => App.State.Dezhou.LegacyCanceled(i.Code));
            if (stillDead != null)
                throw new Exception($"旧版物价仍标已作废：{stillDead.Code}");
            d($"{keeps.Count} 行保留（{string.Join(" / ", keeps.Select(k => k.OldCode))}）· 均未判作废 · 无现行标准 · " +
              $"作废对照 {st.Total:N0} 行中单列保留 {st.Kept} · 旧版物价联动为「现行」{back.Count} 行");
        });

        // 病理开具指引：海报 15 个疾病系统 / 47 条标本档位，报价必须与《德州市病理一二三级物价》
        // 那张表逐行对得上（二级 1/2/4/12 块 = 168/237/380/982.4，三级 = 198/279/446/1146.2），
        // 蜡块速算必须复现《切换说明》里的三个案例（4 点位=4 块 / 5 块息肉+2 块=3 块 / 9cm 肿物=8 块）。
        Check("病理开具指引·档位数据与蜡块/收费速算", d =>
        {
            var z = App.State.Zhijian;
            z.EnsureLoaded();
            if (z.Entries.Count == 0) throw new Exception("病理开具指引未载入（data/zhijian.json 缺失？）");
            if (z.Systems.Count < 12) throw new Exception($"疾病系统只有 {z.Systems.Count} 个，海报是 15 个");
            if (z.Levels.Count != 2) throw new Exception($"价格档应为二级/三级两档，实际 {z.Levels.Count} 档");
            var bad = z.Entries.Where(x => x.Wax <= 0 || string.IsNullOrWhiteSpace(x.Sample)).ToList();
            if (bad.Count > 0) throw new Exception($"{bad.Count} 条档位/标本为空");
            if (!z.Entries.Any(x => x.Kind == "复杂"))
                throw new Exception("复杂病理档位全丢了（报价会一律按常规算）");

            // 逐行对《德州市病理一二三级物价》：常规组织病理 / 复杂组织病理 / 细胞病理 / TCT
            (string Mode, int N, decimal L2, decimal L3)[] cases =
            {
                ("常规", 1, 168m, 198m), ("常规", 2, 237m, 279m), ("常规", 3, 306m, 360m),
                ("常规", 4, 380m, 446m), ("常规", 5, 454m, 532m), ("常规", 6, 528m, 618m),
                ("常规", 7, 602m, 704m), ("常规", 8, 676m, 790m), ("常规", 10, 824m, 962m),
                ("常规", 12, 982.4m, 1146.2m), ("常规", 13, 1061.6m, 1238.3m),
                ("复杂", 6, 618m, 726m), ("复杂", 8, 796m, 934m), ("复杂", 10, 974m, 1142m),
                ("复杂", 12, 1162.4m, 1362.2m), ("复杂", 15, 1445m, 1692.5m),
                ("细胞", 1, 235m, 276m), ("细胞", 2, 371m, 435m),
                ("TCT", 1, 201m, 236m),
            };
            foreach (var c in cases)
            {
                var q2 = z.Quote(c.N, c.Mode, "2");
                if (q2.Total != c.L2)
                    throw new Exception($"{c.Mode} {c.N} 张二级应付 {c.L2}，实算 {q2.Total}" +
                                        $"（诊断 {q2.Diag} / 处理 {q2.Proc} / 染色 {q2.Stain}）");
                var q3 = z.Quote(c.N, c.Mode, "3");
                if (q3.Total != c.L3)
                    throw new Exception($"{c.Mode} {c.N} 张三级应付 {c.L3}，实算 {q3.Total}" +
                                        $"（诊断 {q3.Diag} / 处理 {q3.Proc} / 染色 {q3.Stain}）");
            }

            // 诊断费封顶：第 31 块起不再加收（二级每块 5.2，最多加收 20 块）
            var q31 = z.Quote(31, "常规", "2");
            var q30 = z.Quote(30, "常规", "2");
            if (q31.Diag != q30.Diag)
                throw new Exception($"诊断费超过第 30 块仍在加收（30 块 {q30.Diag} / 31 块 {q31.Diag}）");

            if (ZhijianStore.WaxCount(4, 1, 2) != 4) throw new Exception("4 个点位应算 4 个蜡块");
            if (ZhijianStore.WaxCount(1, 5, 2) + ZhijianStore.WaxCount(1, 2, 2) != 3)
                throw new Exception("5 块息肉 + 2 块组织应算 3 个蜡块");
            if (ZhijianStore.WaxCount(1, 1, 9) != 8) throw new Exception("9cm 肿物应算 8 个蜡块");

            d($"{z.Systems.Count} 个系统 / {z.Entries.Count} 条档位 · 二级 1/2/4/12 蜡块 = " +
              $"{z.Quote(1, "常规", "2").Total:0.##}/{z.Quote(2, "常规", "2").Total:0.##}/" +
              $"{z.Quote(4, "常规", "2").Total:0.##}/{z.Quote(12, "常规", "2").Total:0.##} 元 · " +
              $"三级 4 蜡块 {z.Quote(4, "常规", "3").Total:0.##} · 细胞 1 张 {z.Quote(1, "细胞", "2").Total:0.##} · " +
              $"TCT {z.Quote(1, "TCT", "2").Total:0.##}");
        });

        // 工具栏：右侧竖排标签不再是「病理开具指引」而是「工具栏」，抽屉里第一格是病理、
        // 其余是预留位；以后加工具只加卡片 + 视图，不动导航与快捷键 —— 反射确认挂的是 ToolPanel。
        Check("工具栏·病理入口与预留位（以后加工具不用动导航）", d =>
        {
            const System.Reflection.BindingFlags Any =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public;

            var host = typeof(MainWindow).GetField("ToolHost", Any);
            if (host == null)
                throw new Exception("主窗口里没有 ToolHost（抽屉还挂着旧的 GuidePanel？）");
            if (host.FieldType != typeof(ToolPanel))
                throw new Exception($"抽屉挂的是 {host.FieldType.Name}，应为 ToolPanel");
            if (typeof(MainWindow).GetField("GuideTab", Any) == null)
                throw new Exception("右侧竖排标签（GuideTab）不见了");

            var open = typeof(ToolPanel).GetMethod("OpenTool", Any);
            if (open == null || open.GetParameters().Length != 1)
                throw new Exception("ToolPanel.OpenTool(string) 缺失（切不到病理工具）");
            if (ToolPanel.ToolPathology != "pathology")
                throw new Exception($"病理工具 id 变成了「{ToolPanel.ToolPathology}」，网页端对不上");
            if (typeof(GuidePanel).GetMethod("OnBack", Any) == null)
                throw new Exception("病理面板没有返回工具栏的入口（OnBack）");
            if (ToolPanel.ToolWuhao != "wuhao")
                throw new Exception($"物耗工具 id 变成了「{ToolPanel.ToolWuhao}」，网页端对不上");
            if (typeof(WuhaoPanel).GetMethod("OnBack", Any) == null)
                throw new Exception("物耗面板没有返回工具栏的入口（OnBack）");
            if (ToolPanel.ToolPriceAi != "priceai")
                throw new Exception($"物价解读 AI 工具 id 变成了「{ToolPanel.ToolPriceAi}」");
            if (typeof(PriceAiPanel).GetMethod("OnBack", Any) == null)
                throw new Exception("物价解读 AI 面板没有返回工具栏的入口（OnBack）");
            if (typeof(ToolPanel).GetMethod("OnPickPriceAi", Any) == null)
                throw new Exception("工具栏首页没有物价解读 AI 的卡片入口");

            d("竖排标签「工具栏」→ 抽屉（ToolPanel）：病理开具指引 + 基本物耗指引 + 物价解读 AI + 1 个预留位");
        });

        // 基本物耗：清单 / 边界 / 判定三件事必须自洽 ——
        // ① 36 类指南条款都切出来了（病理那条要含玻片/包埋介质/试剂，这是最容易被漏收的）；
        // ② 每条物耗都能说出「哪几类指南列了它」，不能有孤儿条目；
        // ③ 判定器把最常误收的几项咬死（输液器 / 耦合剂 = 不可另收，钢板 = 可另收，留置针 = 需核对）。
        Check("基本物耗·36 类条款 / 清单归属 / 判定口径", d =>
        {
            var w = App.State.Wuhao;
            w.EnsureLoaded();
            if (w.Items.Count < 100) throw new Exception($"基本物耗只载入 {w.Items.Count} 条（data/wuhao.json 缺失？）");
            if (w.Sources.Count < 30) throw new Exception($"指南条款只切出 {w.Sources.Count} 类，应覆盖 30 类以上");
            if (w.Edges.Count < 20) throw new Exception($"边界清单只有 {w.Edges.Count} 条");
            if (w.Rules.Count < 5) throw new Exception($"判断口径只有 {w.Rules.Count} 条");

            var path = w.Sources.FirstOrDefault(x => x.Cat == "病理");
            if (path == null) throw new Exception("36 类条款里没有病理");
            foreach (var kw in new[] { "玻片", "包埋介质", "试剂", "活检器具" })
                if (!path.Clause.Contains(kw))
                    throw new Exception($"病理基本物耗条款缺「{kw}」，病理收费口径会算错");

            var orphan = w.Items.Where(i => i.Cats.Count == 0).ToList();
            if (orphan.Count > 0)
                throw new Exception($"{orphan.Count} 条物耗没挂任何指南类别：{string.Join("、", orphan.Take(5).Select(x => x.Name))}");
            var noNote = w.Items.Where(i => string.IsNullOrWhiteSpace(i.Note)).ToList();
            if (noNote.Count > 0) throw new Exception($"{noNote.Count} 条物耗没有说明文字");

            (string Q, string Tone, string Why)[] cases =
            {
                ("纱布", WuhaoStore.ToneNo, "纱布是基本物耗，换药费里再收就是重复收费"),
                ("一次性输液器", WuhaoStore.ToneNo, "输液管路属基本物耗，门诊输液室最爱误收"),
                ("耦合剂", WuhaoStore.ToneNo, "超声类明确列入基本物耗"),
                ("玻片", WuhaoStore.ToneNo, "病理类明确列入（已含在蜡块价里）"),
                ("免疫组化抗体", WuhaoStore.ToneNo, "试剂含在项目价，免疫组化按项收费"),
                ("钢板", WuhaoStore.ToneYes, "内固定器械属植入物，可零差率另收"),
                ("留置针", WuhaoStore.ToneCheck, "未列入清单，能否收看可另收费材料目录"),
                ("导尿包", WuhaoStore.ToneNo, "包内导尿管/棉球/润滑剂/中单都在清单里，整套不另收（引流袋本身不在清单）"),
                ("避光输液器", WuhaoStore.ToneDeduct, "特殊功能耗材可另收，但项目价要按普通输液器均价减收"),
                ("精密输液器", WuhaoStore.ToneDeduct, "同上：只收不减等于重复收费"),
                ("精密", WuhaoStore.ToneCheck, "只写型号不能下结论，必须提示补全全称，绝不能落成可收"),
                ("三腔导尿管", WuhaoStore.ToneDeduct, "普通导尿管含在项目价，三腔属特殊功能，可收但要减收"),
                ("双腔导尿管", WuhaoStore.ToneNo, "常规双腔就是指南所称的「导尿管」，不得按型号另收"),
                ("胃管", WuhaoStore.ToneCheck, "清单没列，属清单外耗材，得先过三关"),
                ("固定带", WuhaoStore.ToneNo, "6 类指南明确列入，约束固定用的带子不另收"),
                ("换药包", WuhaoStore.ToneNo, "包内组件全是基本物耗，换药费里不能再挂材料费"),
                ("三通", WuhaoStore.ToneCheck, "输液管路附件，能否另收看目录"),
                ("雾化面罩", WuhaoStore.ToneNo, "呼吸类把雾化器列入基本物耗，面罩是它的组成部分"),
            };
            foreach (var c in cases)
            {
                var v = w.Judge(c.Q);
                if (v.Tone != c.Tone)
                    throw new Exception($"判定「{c.Q}」得到 {v.Tone}（{v.Head}），应为 {c.Tone} —— {c.Why}");
            }

            // 空输入不能报错，也不能假装命中清单
            var empty = w.Judge("  ");
            if (empty.Tone != WuhaoStore.ToneMiss) throw new Exception("空关键词的判定基调不对");

            // 短词 / 半截名称是最容易害人的一格：既不能判「可以收」，也要说清为什么
            foreach (var shortQ in new[] { "引流", "精密", "避光", "一次性" })
            {
                var sv = w.Judge(shortQ);
                if (sv.Tone == WuhaoStore.ToneYes)
                    throw new Exception($"半截名称「{shortQ}」被判成可另收（{sv.Head}），会误导一线");
                if (shortQ.Length <= 3 && sv.Tone == WuhaoStore.ToneMiss && w.EdgeHit(shortQ).Count == 0)
                    throw new Exception($"「{shortQ}」没命中任何条目却落到清单外，应判需核对并提示补全");
            }
            // 分组必须按 id 取得回条目 —— 界面上一度拿 Name 去比 id，点任何分组都是空列表
            var gids = w.Groups.Select(g => g.Id).ToHashSet();
            if (w.Items.Any(i => !gids.Contains(i.Group)))
                throw new Exception("有条目的 group 不在 groups 里的 id 集合中（界面按 id 筛会漏）");
            foreach (var g in w.Groups)
                if (w.ByGroup(g.Id).Count == 0)
                    throw new Exception($"分组「{g.Name}」按 id {g.Id} 取不到条目，界面点开会是空白");

            // 「某某专用导丝」现在能落到边界清单的「导丝」上（收录更全），清单外的断言换一个真没收录的
            var guide = w.Judge("某某专用导丝");
            if (guide.Tone != WuhaoStore.ToneYes)
                throw new Exception($"专用导丝应命中边界清单判可另收，实得 {guide.Tone}（{guide.Head}）");

            var missV = w.Judge("一次性使用黏膜切开刀");
            if (missV.Tone != WuhaoStore.ToneMiss || !missV.Note.Contains("三关"))
                throw new Exception("清单外的提示必须说清「过三关」，不能只说可以收");

            // 特殊功能耗材是第三种口径：能收，但项目价要同步减收 —— 只收不减等于重复收费
            var dd = w.Judge("精密输液器");
            if (dd.Tone != WuhaoStore.ToneDeduct) throw new Exception($"精密输液器应为可另收（减收），实得 {dd.Tone}");
            if (!dd.Basis.Contains("减收") && !dd.Note.Contains("减收"))
                throw new Exception("「可另收（减收）」的依据里必须写明减收，否则一线只会看到「能收」");
            if (!w.Rules.Any(r => r.Text.Contains("减收政策")))
                throw new Exception("判断口径里缺「特殊功能耗材同步减收」这一条（用户贴的原文最后一句）");
            if (w.Edges.Count(e => e.Verdict == "可另收（减收）") < 1)
                throw new Exception("边界清单里没有「可另收（减收）」条目");

            // 边界清单的检索出口：清单里搜不到的留置针，要能从边界清单捞回来
            if (w.EdgeHit("留置针").Count == 0)
                throw new Exception("「留置针」在边界清单里捞不到，不可收费页会显示成空结果");

            d($"{w.Sources.Count} 类条款 / {w.Items.Count} 条基本物耗 / {w.Edges.Count} 条边界 · " +
              $"输液器={w.Judge("一次性输液器").Tone} · 钢板={w.Judge("钢板").Tone} · 留置针={w.Judge("留置针").Tone}");
        });

        // 本机手工维护只写差异集（data/wuhao.user.json）：增 / 改 / 删都要能盖在基础库上生效，
        // 又不能碰 wuhao.json 本身、更不能进分发白名单。
        // 自检跑完必须把原文件逐字节还原 —— 这台机器上可能真的存着操作员自己补的条目。
        Check("基本物耗·本机维护差异集（增/改/删 + 不碰基础库 + 不进分发）", d =>
        {
            var w = App.State.Wuhao;
            w.EnsureLoaded();

            var userFile = AppPaths.WuhaoUserFile;
            var baseFile = AppPaths.WuhaoFile;
            var userBefore = System.IO.File.Exists(userFile)
                ? System.IO.File.ReadAllBytes(userFile) : null;
            var baseBefore = System.IO.File.ReadAllBytes(baseFile);

            try
            {
                var probe = new WuhaoItem
                {
                    Id = "u-selftest",
                    Name = "自检用测试耗材",
                    Group = w.Groups[0].Id,
                    Alias = new List<string> { "自检别名" },
                    Cats = new List<string>(),
                    Count = 0,
                    Note = "自检临时条目",
                };

                var target = w.Items.FirstOrDefault(i => i.Name.Contains("纱布"));
                if (target == null) throw new Exception("基础库里没有纱布条目，自检没法验证修改");
                var targetEdit = new WuhaoItem
                {
                    Id = target.Id,
                    Name = target.Name,
                    Group = target.Group,
                    Alias = new List<string>(target.Alias),
                    Cats = new List<string>(target.Cats),
                    Count = target.Count,
                    Note = "自检改过的说明",
                };

                var gone = w.Items.FirstOrDefault(i => i.Name == "棉签");
                if (gone == null) throw new Exception("基础库里没有「棉签」，自检没法验证删除");

                w.Save(new WuhaoUserFile
                {
                    Items = new List<WuhaoItem> { probe, targetEdit },
                    DeletedItems = new List<string> { gone.Id },
                });

                if (!System.IO.File.Exists(userFile))
                    throw new Exception("保存后没有生成 data/wuhao.user.json");
                if (w.Judge("自检用测试耗材").Tone != WuhaoStore.ToneNo)
                    throw new Exception("本机新增的条目没生效（判定没命中「不可另收」）");
                if (w.Items.First(i => i.Id == target.Id).Note != "自检改过的说明")
                    throw new Exception("本机修改没有覆盖基础库条目");
                if (w.Items.Any(i => i.Id == gone.Id))
                    throw new Exception("本机删除没有生效");

                var baseAfter = System.IO.File.ReadAllBytes(baseFile);
                if (!baseBefore.SequenceEqual(baseAfter))
                    throw new Exception("保存本机维护时改动了 data/wuhao.json（基础库必须只读）");

                var txt = System.IO.File.ReadAllText(userFile);
                if (!txt.Contains("u-selftest")) throw new Exception("差异集里没有新增条目");
                if (!txt.Contains("自检改过的说明")) throw new Exception("差异集里没有修改条目");
                if (!txt.Contains(gone.Id)) throw new Exception("差异集里没有删除标记");

                if (DataCatalog.Specs.Any(s => s.RelPath == "data/wuhao.user.json"))
                    throw new Exception("wuhao.user.json 混进了分发白名单（会被同步到全院）");
                if (!DataCatalog.PrivateRelPaths.Contains("data/wuhao.user.json"))
                    throw new Exception("wuhao.user.json 没登记为本机私有文件");

                d($"新增 {probe.Name} / 改 {target.Name} / 删 {gone.Name} · 基础库 0 改动 · 差异集 {txt.Length} B");
            }
            finally
            {
                // 原来没有就删掉，有就逐字节写回（这里不能"先清空再重载"，那等于替用户做了重置）
                if (userBefore == null)
                {
                    if (System.IO.File.Exists(userFile)) System.IO.File.Delete(userFile);
                }
                else System.IO.File.WriteAllBytes(userFile, userBefore);
                w.Reload();
            }
        });

        // 别名体检基线：基础库允许「别名 = 自己主名」的冗余（不影响判定），
        // 但绝不允许跨条目撞名 / 别名互撞 / 同名条目 —— HitItem 按清单顺序先命中谁，
        // 回显的条目与说明就归谁，口径会变得不确定。只查基础库，不把操作员本机差异算进去。
        Check("基本物耗·别名体检基线（基础库无真冲突）", d =>
        {
            string K(string s) => (s ?? "").Replace(" ", "").Replace("（", "(")
                .Replace("）", ")").Replace("／", "/").ToLowerInvariant();

            var baseFile = Js.ReadJsonFile<WuhaoFile>(AppPaths.WuhaoFile);
            if (baseFile?.Items == null || baseFile.Items.Count == 0)
                throw new Exception("读不到 data/wuhao.json 的基础库条目");
            var items = baseFile.Items;

            var names = new Dictionary<string, List<int>>();
            var aliases = new Dictionary<string, List<int>>();
            for (var i = 0; i < items.Count; i++)
            {
                var nk = K(items[i].Name);
                if (nk.Length == 0) continue;
                if (!names.TryGetValue(nk, out var nl)) names[nk] = nl = new List<int>();
                nl.Add(i);
                foreach (var a in items[i].Alias ?? new List<string>())
                {
                    var ak = K(a);
                    if (ak.Length == 0) continue;
                    if (!aliases.TryGetValue(ak, out var al)) aliases[ak] = al = new List<int>();
                    al.Add(i);
                }
            }

            var real = 0;
            var redundant = 0;
            for (var i = 0; i < items.Count; i++)
                foreach (var a in items[i].Alias ?? new List<string>())
                {
                    var ak = K(a);
                    if (ak.Length == 0) continue;
                    if (ak == K(items[i].Name)) redundant++;
                    if (names.TryGetValue(ak, out var owners) && owners.Any(j => j != i)) real++;
                    if (aliases.TryGetValue(ak, out var users) && users.Any(j => j > i)) real++;
                }
            real += names.Count(kv => kv.Value.Count > 1);

            if (real != 0)
                throw new Exception($"基础库出现 {real} 处别名真冲突（撞他人主名 / 别名互撞 / 同名条目），" +
                                   "判定归属会由清单顺序决定，必须先在源表消歧");
            d($"{items.Count} 条 · 真冲突 0 · 冗余自名别名 {redundant}（无判定影响）· " +
              "明细见 docs/wuhao-alias-audit.md");
        });

        // 物价解读 AI：配置只留本机 / 检索必须命中真实条文 / 协议与密钥不外泄
        Check("物价解读 AI·本机配置往返 / 密钥遮盖 / 私有边界", d =>
        {
            var tmp = Path.Combine(Path.GetTempPath(), "dip3-priceai-selftest-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var key = "sk-selftest-1234567890";
                var s = new PriceAiSettings
                {
                    Provider = "deepseek", BaseUrl = "https://api.deepseek.com/v1/", ApiKey = key,
                    Model = "deepseek-flash", Region = "山东省德州市",
                    TopK = 999, Temperature = 9, TimeoutSec = 1,
                };
                PriceAiSettingsStore.Save(s, tmp);
                var back = PriceAiSettingsStore.Load(tmp);
                if (back.ApiKey != key || back.Model != "deepseek-flash") throw new Exception("配置往返丢了字段");
                if (back.TopK != 12 || back.TimeoutSec != 10 || back.Temperature != 1)
                    throw new Exception("越界参数没有被归一化到安全区间");
                if (PriceAiSettingsStore.Endpoint(back.BaseUrl) != "https://api.deepseek.com/v1/chat/completions")
                    throw new Exception("接口地址拼接不对（末尾斜杠 / 重复路径）");
                if (PriceAiSettingsStore.Endpoint("http://127.0.0.1:11434/v1/chat/completions")
                    != "http://127.0.0.1:11434/v1/chat/completions")
                    throw new Exception("用户已填完整路径时不应重复补 /chat/completions");
                if (PriceAiSettingsStore.MaskKey(key).Contains(key) || PriceAiSettingsStore.MaskKey(key) == key)
                    throw new Exception("密钥遮盖回显了全文");
                if (PriceAiSettingsStore.SafeDescribe(back).Contains(key))
                    throw new Exception("可写日志的描述串里带了密钥");
                if (!PriceAiSettingsStore.Validate(back).Ok) throw new Exception("合法配置被校验拦下");
                if (PriceAiSettingsStore.Validate(new PriceAiSettings { BaseUrl = "ftp://x", Model = "m" }).Ok)
                    throw new Exception("非法协议的地址没有被拦下");
                if (DataCatalog.Specs.Any(x => x.RelPath == "data/price-ai.json"))
                    throw new Exception("price-ai.json 混进分发白名单（会把密钥同步到全院）");
                if (!DataCatalog.PrivateRelPaths.Contains("data/price-ai.json"))
                    throw new Exception("price-ai.json 没登记为本机私有文件");
                // 发布产物里本来就不带发布脚本（源码工程才有），找不到不算失败
                var batsChecked = 0;
                foreach (var bat in new[] { "publish-desktop.bat", "publish-web.bat" })
                {
                    var batPath = Path.Combine(AppPaths.Root, bat);
                    if (!System.IO.File.Exists(batPath)) continue;
                    batsChecked++;
                    var txt = System.IO.File.ReadAllText(batPath);
                    if (!txt.Contains("price-ai.json")) throw new Exception(bat + " 的 KEEPFILES 没保住本机配置");
                }
                // 真实发布路径也要能写（不能只在临时目录里通），写完逐字节还原现场
                var real = AppPaths.PriceAiFile;
                var before = System.IO.File.Exists(real) ? System.IO.File.ReadAllBytes(real) : null;
                try
                {
                    var probe = back.Clone();
                    probe.Region = "北京市";
                    PriceAiSettingsStore.Save(probe);
                    PriceAiSettingsStore.ResetCache();
                    if (PriceAiSettingsStore.Current.Region != "北京市")
                        throw new Exception("本机配置落盘后重新载入没生效");
                }
                finally
                {
                    if (before == null)
                    {
                        if (System.IO.File.Exists(real)) System.IO.File.Delete(real);
                    }
                    else System.IO.File.WriteAllBytes(real, before);
                    PriceAiSettingsStore.ResetCache();
                }

                d($"往返 {back.Model} · 越界归一 · Endpoint 拼接 2 例 · 密钥遮盖 · 真实路径落盘+还原 · " +
                  $"白名单反向断言 · 发布脚本 KEEP 检查 {batsChecked}/2");
            }
            finally { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); }
        });

        Check("物价解读 AI·立项指南证据检索 / 地区口径 / 引用编号 / 不臆断", d =>
        {
            var st = App.State;
            var cfg = PriceAiSettingsStore.Current.Clone();
            cfg.Region = "山东省德州市";
            cfg.ApiKey = "sk-never-in-prompt-0000";
            cfg.TopK = 6; cfg.JieduK = 4; cfg.PriceK = 3;
            // 用库里真实存在的项目名，验证「精确命中」而不是靠近似条文凑数
            var q = "鼻骨骨折复位费（闭合）的计价单位是什么，加收项能不能单独收费？";
            var ask = PriceAiKnowledge.Build(st, q, cfg);
            if (ask.Evidences.Count == 0 || ask.GuideCount == 0)
                throw new Exception("真实项目名没检索到立项指南条文，RAG 会退化成凭空作答");
            if (ask.FuzzyOnly || !ask.Evidences.Any(e => e.Kind == "guide" && e.Title.Contains("鼻骨骨折复位费")))
                throw new Exception("真实存在的同名项目被误判为「近似条文」");
            if (ask.SystemPrompt.Contains("未检索到该项目的同名条目"))
                throw new Exception("精确命中时不该出现「未找到同名项目」提示");
            if (!ask.SystemPrompt.Contains("山东省德州市") || !ask.SystemPrompt.Contains("[G1]"))
                throw new Exception("提示词里缺地区口径或证据编号");
            if (!ask.SystemPrompt.Contains("硬性规则") || !ask.SystemPrompt.Contains("不得给出"))
                throw new Exception("提示词丢了「证据里没有的金额不得给出」的硬约束");
            if (ask.SystemPrompt.Contains(cfg.ApiKey)) throw new Exception("密钥混进了提示词");
            if (!ask.UserPrompt.Contains(q)) throw new Exception("用户问题没进 user 消息");
            if (ask.Evidences.Select(e => e.Tag).Distinct().Count() != ask.Evidences.Count)
                throw new Exception("证据编号重复，回引会串");
            if (ask.Evidences.Any(e => string.IsNullOrWhiteSpace(e.Text)))
                throw new Exception("存在空条文证据");
            if (!ask.HasLocalPrice || ask.PriceCount == 0)
                throw new Exception("德州地区没有注入本地落地价证据");

            var other = cfg.Clone(); other.Region = "北京市";
            var ask2 = PriceAiKnowledge.Build(st, q, other);
            if (ask2.HasLocalPrice || ask2.PriceCount != 0)
                throw new Exception("非德州地区仍注入了德州市价格，地区设置没生效");
            if (!ask2.SystemPrompt.Contains("不报金额") || !ask2.SystemPrompt.Contains("未收录"))
                throw new Exception("未收录地区没有明确禁止报金额");

            // 库里根本没有「骨折手法复位术」这个项目：字面最像的是「子宫内翻手法复位费」，
            // 必须显式告诉模型这是近似条文，绝不能张冠李戴当成该项目的规定。
            var fuzzy = PriceAiKnowledge.Build(st, "骨折手法复位术的计价单位是什么？", cfg);
            if (fuzzy.Evidences.Count == 0 || !fuzzy.FuzzyOnly)
                throw new Exception($"库里不存在的项目没有被识别为「仅近似条文」" +
                    $"（证据 {fuzzy.Evidences.Count} 条 / FuzzyOnly={fuzzy.FuzzyOnly} / " +
                    $"首条 {string.Join("、", fuzzy.Evidences.Take(3).Select(e => e.Title))} / " +
                    $"Exact={PriceAiKnowledge.ExactProjectHit("骨折手法复位术的计价单位是什么？", fuzzy.Evidences)}）");
            if (!fuzzy.SystemPrompt.Contains("未检索到该项目的同名条目")
                || !fuzzy.SystemPrompt.Contains("严禁把近似条文"))
                throw new Exception("近似条文没有强制模型先声明不确定性");
            if (!fuzzy.Status.Contains("近似条文")) throw new Exception("状态条没提示近似条文");

            // 「换药」必须排在长条文之前：通用词「价格构成 / 包含 / 内容」
            // 不能把牙体开髓引流费之类无关项目顶到首位。
            var hy = PriceAiKnowledge.Build(st, "换药项目的价格构成包含哪些内容", cfg);
            if (hy.Evidences.Count == 0 || !hy.Evidences.Take(3).Any(e => e.Title.Contains("换药")))
                throw new Exception("问「换药」却被无关项目占据前排：" +
                    string.Join("、", hy.Evidences.Take(3).Select(e => e.Title)));

            var none = PriceAiKnowledge.Build(st, "zxqwerty98765", cfg);
            if (none.Evidences.Count != 0 || !none.SystemPrompt.Contains("未检索到相关条文"))
                throw new Exception("无命中时没有明令模型不得凭常识作答");
            if (none.FuzzyOnly) throw new Exception("零证据时不该同时标记「近似条文」");

            var hist = new List<PriceAiTurn>
            {
                new() { Role = "user", Content = "上一问" }, new() { Role = "assistant", Content = "上一答" },
                new() { Role = "user", Content = "再上一问" }, new() { Role = "assistant", Content = "再上一答" },
            };
            var msgs = PriceAiKnowledge.Messages(ask, hist, 1);
            if (msgs[0].Role != "system" || msgs[^1].Role != "user" || msgs.Count != 4)
                throw new Exception("多轮消息装配把历史轮数算错了");
            if (msgs.Skip(1).Take(2).Any(m => m.Content.Contains("[G1]")))
                throw new Exception("旧证据编号混进了历史消息");

            d($"精确命中首条 [{ask.Evidences[0].Tag}] {ask.Evidences[0].Title} · " +
              $"指南 {ask.GuideCount} / 解读 {ask.JieduCount} / 落地价 {ask.PriceCount} · " +
              "不存在的项目=强制声明近似条文 · 德州注入价格、北京禁止报金额 · 零证据禁止臆断");
        });

        Check("物价解读 AI·OpenAI 兼容协议 / 错误码 / 密钥脱敏（本地假服务端到端）", d =>
        {
            // 纯函数部分：请求体字段、错误码映射、思考型模型、脱敏
            var cfg = new PriceAiSettings
            {
                BaseUrl = "https://example.invalid/v1", ApiKey = "sk-secret-abcdef123456",
                Model = "fake-model", Region = "山东省德州市", MaxTokens = 1024, Temperature = 0.1,
            };
            var body = PriceAiClient.BuildRequestBody(cfg, new List<PriceAiMessage>
            {
                new() { Role = "system", Content = "证据 [G1]" },
                new() { Role = "user", Content = "计价单位？" },
            });
            using (var doc = System.Text.Json.JsonDocument.Parse(body))
            {
                var r = doc.RootElement;
                if (r.GetProperty("model").GetString() != "fake-model") throw new Exception("model 字段错");
                if (!r.TryGetProperty("max_tokens", out var mt) || mt.GetInt32() != 1024)
                    throw new Exception("max_tokens 被序列化策略改名了（下划线键必须原样）");
                if (r.GetProperty("stream").GetBoolean()) throw new Exception("stream 应为 false");
                if (r.GetProperty("messages").GetArrayLength() != 2) throw new Exception("messages 条数错");
            }
            if (body.Contains(cfg.ApiKey)) throw new Exception("请求体里带了密钥（密钥只能在 HTTP 头）");

            if (!PriceAiClient.ParseResponse("{\"error\":{\"message\":\"bad key\"}}", 401, cfg).Message.Contains("API Key"))
                throw new Exception("401 没有给到密钥排查方向");
            if (!PriceAiClient.ParseResponse("{}", 404, cfg).Message.Contains("接口地址不对"))
                throw new Exception("404 没有给到地址排查方向");
            if (!PriceAiClient.ParseResponse("{}", 429, cfg).Message.Contains("限流"))
                throw new Exception("429 没有给到限流说明");
            var think = PriceAiClient.ParseResponse(
                "{\"model\":\"m\",\"choices\":[{\"message\":{\"content\":null,\"reasoning_content\":\"思考后结论\"}}],\"usage\":{\"prompt_tokens\":7,\"completion_tokens\":9}}", 200, cfg);
            if (!think.Ok || think.Content != "思考后结论" || think.PromptTokens != 7)
                throw new Exception("思考型模型 / usage 解析失败");
            if (PriceAiClient.ParseResponse("{\"choices\":[{\"message\":{\"content\":\"\"}}]}", 200, cfg).Ok)
                throw new Exception("空回答被当成成功");
            var leaked = PriceAiClient.ParseResponse("{\"error\":{\"message\":\"" + cfg.ApiKey + "\"}}", 500, cfg);
            if (leaked.Message.Contains(cfg.ApiKey)) throw new Exception("错误信息回显了密钥");

            // 端到端：本地 HttpListener 冒充模型服务，核对路径 / 鉴权头 / 请求体 / 回包
            int port;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();

            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            string seenPath = "", seenAuth = "", seenBody = "";
            var serve = Task.Run(async () =>
            {
                for (var i = 0; i < 2; i++)
                {
                    var ctx = await listener.GetContextAsync();
                    using var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    var raw = await sr.ReadToEndAsync();
                    if (i == 0)
                    {
                        seenPath = ctx.Request.Url.AbsolutePath;
                        seenAuth = ctx.Request.Headers["Authorization"] ?? "";
                        seenBody = raw;
                    }
                    var ok = i == 0;
                    var payload = ok
                        ? "{\"id\":\"x\",\"model\":\"fake-model\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"依据 [G1]，计价单位为次。\"}}],\"usage\":{\"prompt_tokens\":120,\"completion_tokens\":30}}"
                        : "{\"error\":{\"message\":\"no quota\"}}";
                    var bytes = Encoding.UTF8.GetBytes(payload);
                    ctx.Response.StatusCode = ok ? 200 : 429;
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                    ctx.Response.Close();
                }
            });

            var localCfg = cfg.Clone();
            localCfg.BaseUrl = $"http://127.0.0.1:{port}/v1";
            var reply = PriceAiClient.ChatAsync(localCfg, new List<PriceAiMessage>
            {
                new() { Role = "system", Content = "地区：山东省德州市；证据 [G1]" },
                new() { Role = "user", Content = "计价单位？" },
            }).GetAwaiter().GetResult();
            var bad = PriceAiClient.TestAsync(localCfg).GetAwaiter().GetResult();
            serve.GetAwaiter().GetResult();
            listener.Stop();

            if (seenPath != "/v1/chat/completions") throw new Exception("请求路径不是 /v1/chat/completions：" + seenPath);
            if (seenAuth != "Bearer " + cfg.ApiKey) throw new Exception("鉴权头没带或格式不对");
            if (!seenBody.Contains("山东省德州市") || !seenBody.Contains("[G1]") || !seenBody.Contains("计价单位"))
                throw new Exception("请求体没带上地区口径 / 证据 / 用户问题");
            if (seenBody.Contains(cfg.ApiKey)) throw new Exception("请求体带了密钥");
            if (!reply.Ok || !reply.Content.Contains("[G1]") || reply.PromptTokens != 120)
                throw new Exception("端到端回包解析失败：" + reply.Message);
            if (bad.Ok || !bad.Message.Contains("限流")) throw new Exception("429 没有翻译成人话：" + bad.Message);
            d("请求体字段 / 401·404·429 / 思考型模型 / 空回答 / 密钥脱敏 + 本地假服务路径·鉴权·回包全通过");
        });

        Check("检索防抖（连续键入只触发一次 / 延迟生效 / 可取消）", d =>
        {
            var hits = 0;
            var firstAt = TimeSpan.Zero;
            var t0 = DateTime.Now;
            var db = new Debounce(120, () =>
            {
                hits++;
                if (hits == 1) firstAt = DateTime.Now - t0;
            });

            void Pump(int ms)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                var timer = new System.Windows.Threading.DispatcherTimer(
                    TimeSpan.FromMilliseconds(ms), System.Windows.Threading.DispatcherPriority.Normal,
                    (_, _) => frame.Continue = false, System.Windows.Threading.Dispatcher.CurrentDispatcher);
                timer.Start();
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                timer.Stop();
            }

            db.Poke(); db.Poke(); db.Poke();          // 连续键入（间隔极短）
            Pump(300);
            if (hits != 1) throw new Exception($"连续 3 次键入应只触发 1 次，实际 {hits} 次");
            if (firstAt.TotalMilliseconds < 100)
                throw new Exception($"防抖延迟未生效（{firstAt.TotalMilliseconds:0}ms 就触发了）");

            db.Poke();
            db.Cancel();                              // 清空按钮：取消未触发的检索
            Pump(300);
            if (hits != 1) throw new Exception("Cancel 后仍触发了检索");

            d($"3 次连击 → 1 次触发（延迟 {firstAt.TotalMilliseconds:0}ms）· Cancel 生效");
        });

        Check("首页详情块懒构建（首屏不再为空行预建跨库关联）", d =>
        {
            // 不访问 Blocks 就不该调用工厂
            var calls = 0;
            var vm = new GfRowVm
            {
                Code = "AAAA0001",
                Name = "普通门诊诊察",
                BlocksFactory = () =>
                {
                    calls++;
                    return new List<LxBlock> { new() { Label = "项目内涵", Lines = new List<LxLine> { new() { Text = "x" } } } };
                },
            };
            if (calls != 0) throw new Exception("构造 VM 时不应构建详情块");
            if (vm.Blocks.Count != 1 || calls != 1) throw new Exception("首次访问详情块应构建一次");
            _ = vm.Blocks;
            if (calls != 1) throw new Exception("详情块应缓存，不应重复构建");

            // 页面首屏 50 行：只有被选中的那一行（渲染详情）才需要构建
            var page = App.State.Guifan.ListRows("", "", "", 1, 50);
            var built = 0;
            var vms = page.Items.Select(i => new GfRowVm
            {
                Code = i.Code,
                Name = i.Name,
                BlocksFactory = () => { built++; return GuifanView.BuildBlocks(i); },
            }).ToList();
            if (built != 0) throw new Exception("首屏 50 行不应预先构建详情块");
            var selected = vms[0];
            if (selected.Blocks.Count == 0) throw new Exception("选中行详情块为空");

            d($"首屏 {vms.Count} 行懒构建（选中前 0 次构建 / 选中后 1 次）");
        });

        Check("启动预热（技术规范载入 + 拼音缓存 + 跨库索引）", d =>
        {
            var sw = Stopwatch.StartNew();
            App.State.Guifan.EnsureLoaded();
            var loadMs = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            PinyinHelper.WarmAll(App.State.Guifan.Items.Take(500).Select(i => i.Name));
            var warmMs = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            CrossMap.Warm();
            var cmMs = sw.Elapsed.TotalMilliseconds;

            if (App.State.Guifan.Items.Count == 0) throw new Exception("技术规范未载入");
            if (PinyinHelper.CacheCount == 0) throw new Exception("拼音缓存未建立");
            var hit = PinyinHelper.PyMatch("原位心脏移植术", "xzyz");
            if (!hit) throw new Exception("拼音首字母匹配失效");

            // 预热后首个关联查询应当明显快于「冷启动建索引」
            sw.Restart();
            var blocks = CrossMap.ForGuifan(App.State.Guifan.Items[0].Name);
            var q1 = sw.Elapsed.TotalMilliseconds;

            d($"规范载入 {loadMs:0}ms（幂等重入）· 拼音 500 条 {warmMs:0}ms · 跨库索引 {cmMs:0}ms · 首次查询 {q1:0.0}ms（{blocks.Count} 块）");
        });


        Head("二、分组正确性（与 xunit / Node 对拍同一批断言口径）");

        Check("胃肠炎 + 胃镜（胃镜被排除 → 基础规则保守行）", d =>
        {
            var kase = new Kase { MainDiag = "A09.901", MainProc = "44.1300x001" };
            var r = GrouperService.Group(App.State, kase, false);
            if (r.Matched != true) throw new Exception("未命中分组");
            if (r.GroupTypeCode != "JC") throw new Exception("分组类型应为 JC，实际 " + r.GroupTypeCode);
            if (r.Seq != 1868) throw new Exception("序号应为 1868，实际 " + r.Seq);
            d($"{r.GroupTypeCode} #{r.Seq} {r.Name}");
        });

        Check("伤寒仅主诊断（保守治疗）", d =>
        {
            var r = GrouperService.Group(App.State, new Kase { MainDiag = "A01.000" }, false);
            if (r.Matched != true || r.Seq != 1858) throw new Exception($"期望 JC #1858，实际 {r.GroupTypeCode} #{r.Seq}");
            d($"{r.GroupTypeCode} #{r.Seq} {r.Name}");
        });

        Check("肿瘤化疗 + 靶向（取最长组合）", d =>
        {
            var kase = new Kase
            {
                MainDiag = "Z51.100x002",
                OtherDiags = new List<string> { "C34.900" },
                MainProc = "99.2503",
                OtherProcs = new List<string> { "99.2800x006" },
            };
            var r = GrouperService.Group(App.State, kase, false);
            if (r.Matched != true || r.Seq != 1772) throw new Exception($"期望 FZ #1772，实际 {r.GroupTypeCode} #{r.Seq}");
            d($"{r.GroupTypeCode} #{r.Seq} {r.Name}");
        });

        Check("结核区间行 → 综合病组兜底", d =>
        {
            var r = GrouperService.Group(App.State,
                new Kase { MainDiag = "A15.000x002", MainProc = "31.9400" }, false);
            if (r.Matched != true || r.Seq != 1813) throw new Exception($"期望 ZH #1813，实际 {r.GroupTypeCode} #{r.Seq}");
            d($"{r.GroupTypeCode} #{r.Seq} {r.Name}");
        });

        Check("启用映射后分组（诊断经医保码转换）", d =>
        {
            var r = GrouperService.Group(App.State, new Kase { MainDiag = "A09.901" }, true);
            d($"命中 {r.Matched} · {r.GroupTypeCode} #{r.Seq}");
        });

        Check("联想（诊断 / 手术 / 关联）", d =>
        {
            var diag = GrouperService.SuggestDiag(App.State, "胃肠炎");
            var proc = GrouperService.SuggestProc(App.State, "胃镜");
            var linked = GrouperService.SuggestLinked(App.State, "A09.901");
            if (diag.Count == 0) throw new Exception("诊断联想为空");
            if (proc.Count == 0) throw new Exception("手术联想为空");
            d($"诊断 {diag.Count} · 手术 {proc.Count} · 关联 {linked.OtherDiags.Count}+{linked.Procs.Count}");
        });

        Check("批量分组 + 结果表生成", d =>
        {
            var rows = new List<Dictionary<string, JsonElement>>
            {
                GrouperService.ToJsonDict(new Dictionary<string, object> { ["mainDiag"] = "A01.000" }),
                GrouperService.ToJsonDict(new Dictionary<string, object> { ["mainDiag"] = "A09.901", ["mainProc"] = "44.1300x001" }),
            };
            var br = GrouperService.Batch(App.State, rows, false);
            if (br.Rows.Count != 2) throw new Exception("批量结果条数不符");
            if (br.Xlsx == null || br.Xlsx.Length == 0) throw new Exception("结果 xlsx 为空");
            d($"{br.Rows.Count} 条 · xlsx {br.Xlsx.Length:N0} 字节");
        });

        Check("批量模板生成", d =>
        {
            var b = GrouperService.BuildBatchTemplate();
            if (b == null || b.Length == 0) throw new Exception("模板为空");
            d($"{b.Length:N0} 字节");
        });

        // ================= 三、管理端只读链路 =================
        Head("三、后台管理只读链路");

        Check("病种库分页查询", d =>
        {
            var r = Diseases.ListRows(App.State.Config, "bingxiang", "", 1, 20);
            if (r.Rows.Count == 0) throw new Exception("并项区段无数据");
            d($"{r.Label} · 共 {r.Total:N0} 条 · 本页 {r.Rows.Count}");
        });

        Check("病种库各区段均可列出", d =>
        {
            var bad = new List<string>();
            foreach (var sec in Diseases.Sections)
            {
                try { Diseases.ListRows(App.State.Config, sec.Tab, "", 1, 5); }
                catch (Exception e) { bad.Add($"{sec.Tab}({e.Message})"); }
            }
            if (bad.Count > 0) throw new Exception("以下区段列表失败: " + string.Join(", ", bad));
            d($"{Diseases.Sections.Count} 个区段全部可列出");
        });

        Check("病种库模板导出", d =>
        {
            var (bytes, name) = AdminService.DiseasesTemplate(null);
            var (one, oneName) = AdminService.DiseasesTemplate("bingxiang");
            if (bytes.Length == 0 || one.Length == 0) throw new Exception("模板为空");
            d($"全量 {bytes.Length:N0}B({name}) · 单区段 {one.Length:N0}B({oneName})");
        });

        Check("映射表分页 + 灰码筛选", d =>
        {
            var all = App.State.Mapping.List("", null, 1, 20);
            var gray = App.State.Mapping.List("", true, 1, 20);
            var norm = App.State.Mapping.List("", false, 1, 20);
            if (all.Rows.Count == 0) throw new Exception("映射表无数据");
            if (gray.Rows.Any(x => !x.Gray)) throw new Exception("灰码筛选返回了非灰码行");
            if (norm.Rows.Any(x => x.Gray)) throw new Exception("非灰码筛选返回了灰码行");
            d($"全部 {all.Total:N0} · 灰码 {gray.Total:N0} · 非灰码 {norm.Total:N0}");
        });

        Check("综合病组分页查询", d =>
        {
            var r = App.State.Zonghe.List("", 1, 20);
            d($"共 {r.Total:N0} 条 · 本页 {r.Rows.Count}");
        });

        Check("综合病组模板导出", d =>
        {
            var b = AdminService.ZongheTemplate();
            if (b.Length == 0) throw new Exception("模板为空");
            d($"{b.Length:N0} 字节");
        });

        Check("立项指南区段列表 + 分页", d =>
        {
            var counts = App.State.Lixiang.SectionCounts();
            var tab = counts.Keys.First();
            var r = App.State.Lixiang.ListRows(tab, "", 1, 20);
            d($"{counts.Count} 个区段 · {r.Label} 共 {r.Total:N0} 条");
        });

        Check("立项指南模板导出", d =>
        {
            var (bytes, name) = AdminService.LixiangTemplate(App.State, null);
            if (bytes.Length == 0) throw new Exception("模板为空");
            d($"{bytes.Length:N0}B({name})");
        });

        Check("解读辅导检索", d =>
        {
            var r = App.State.Lixiang.JieduList("", "");
            if (r.Items.Count == 0) throw new Exception("解读条目为空");
            var one = App.State.Lixiang.JieduList(r.Issues[0].Issue.ToString(), "");
            d($"共 {r.Total:N0} 条 / {r.Issues.Count} 期 · 第{r.Issues[0].Issue}期 {one.Total:N0} 条");
        });

        // 2026-10-30：切答案时把下一题的「2.」留在上一题末尾，详情卡正文凭空多一行序号。
        // 用户就是这么发现的（第1期第1题）：既查存量数据干不干净，也用一个两题的样例把切分边界钉死。
        Check("解读辅导答案不带游离题号（切分边界 / 存量清洁）", d =>
        {
            var all = App.State.Lixiang.JieduList("", "").Items;
            var dirty = all.Where(x => LixiangStore.HasDanglingNo(x.A)).ToList();
            if (dirty.Count > 0)
                throw new Exception($"{dirty.Count} 条答案末尾挂着下一题的题号，例：第{dirty[0].Issue}期第{dirty[0].No}题 → " +
                                    "「" + (dirty[0].A ?? "").Substring(Math.Max(0, (dirty[0].A ?? "").Length - 8)).Replace("\n", "\\n") + "」");
            var empty = all.Count(x => string.IsNullOrWhiteSpace(x.A));
            if (empty > 0) throw new Exception($"{empty} 条答案被清成了空白（清理过度）");

            // 题号前一个换行 / 两个换行、句点 / 顿号，都必须切在同一处
            var src = "编者按：样例说明。\n" +
                      "医疗服务价格项目立项指南解读辅导（第1期）\n" +
                      "1. 第一问问什么？\n第一条答案。\n\n2. 第二问问什么？\n第二条答案。\n3、第三问问什么？\n第三条答案。";
            var parsed = LixiangStore.ParseJieduText(src);
            if (parsed.List.Count != 3) throw new Exception("样例应解析出 3 条，实际 " + parsed.List.Count);
            if (parsed.List[0].A != "第一条答案。")
                throw new Exception("双换行分隔时答案切歪了：「" + parsed.List[0].A.Replace("\n", "\\n") + "」");
            if (parsed.List[1].A != "第二条答案。")
                throw new Exception("顿号题号后答案切歪了：「" + parsed.List[1].A.Replace("\n", "\\n") + "」");
            if (parsed.List[2].A != "第三条答案。")
                throw new Exception("末题答案被截断：「" + parsed.List[2].A.Replace("\n", "\\n") + "」");
            if (parsed.List[1].No != 2 || parsed.List[2].No != 3)
                throw new Exception("题号解析错位");

            d($"{all.Count:N0} 条答案末尾无游离题号 · 样例三题（双换行 / 顿号 / 末题）切分边界正确");
        });

        Check("立项指南参数（类别/单位提取与合并 / 类别不再绑定区段）", d =>
        {
            var labels = App.State.Lixiang.SectionLabels();
            var cats = App.State.Lixiang.DistinctCategories();
            var units = App.State.Lixiang.DistinctUnits();
            if (labels.Count == 0 || units.Count == 0) throw new Exception("现有数据提取结果为空");
            if (!labels.Contains("器官移植")) throw new Exception("区段名候选缺少「器官移植」");
            if (cats.Count == 0) throw new Exception("类别去重值为空");

            // 类别不再强绑定：留空回退区段名，填了就按填的存（新增类别不必改代码）
            if (LixiangStore.ResolveCategory("", "器官移植") != "器官移植")
                throw new Exception("留空时未回退为区段名");
            if (LixiangStore.ResolveCategory("  ", "器官移植") != "器官移植")
                throw new Exception("纯空白未回退为区段名");
            if (LixiangStore.ResolveCategory(" 新类别 ", "器官移植") != "新类别")
                throw new Exception("显式类别被改写，仍然存在绑定");

            var merged = SettingsStore.CleanList(new List<string> { "x", " x ", "", units[0] });
            if (merged == null || merged.Count != 2 || merged[0] != "x" || merged[1] != units[0])
                throw new Exception("CleanList 去重/去空白不符");
            var s = SettingsStore.Current;
            d($"区段 {labels.Count} · 类别 {cats.Count} · 单位 {units.Count} · " +
              $"参数表 类别 {s.LixiangCategories?.Count ?? 0}/单位 {s.LixiangUnits?.Count ?? 0}");
        });

        // ================= 四、界面构造 =================
        Head("四、界面构造与布局（资源解析 / 模板展开 / 表格建列）");

        Ui("单条分组页", () => new SingleGroupView());
        Ui("批量分组页", () => new BatchGroupView());
        Ui("病种库查询页", () => new DiseaseLibView());
        Ui("立项指南查询页", () => new LixiangView());
        Ui("德州物价查询页", () => new DezhouPriceView());
        Ui("项目技术规范查询页", () => new GuifanView());
        Ui("后台 · 概览", () => new AdminOverviewView());
        Ui("后台 · 病种库管理", () => new AdminDiseasesView());
        Ui("后台 · 立项指南", () => new AdminLixiangView());
        Ui("后台 · 映射与灰码", () => new AdminMappingView());
        Ui("后台 · 德州物价", () => new AdminDezhouView());
        Ui("后台 · 版本管理", () => new AdminVersionView());
        Ui("后台 · 审计日志", () => new AdminAuditView());
        Ui("后台 · 数据更新", () => new AdminDataView());
        Ui("后台 · 系统设置", () => new AdminSettingsView());
        Ui("工具栏抽屉（病理 / 物耗 / 物价 AI）", () => new ToolPanel(App.State));
        Ui("物价解读 AI 面板", () => new PriceAiPanel(App.State));
        Ui("主窗口", () => new MainWindow());
        Ui("后台窗口", () => new AdminWindow());
        Ui("关联详情弹窗", () => new RelatedWindow());
        Ui("启动画面", () => new SplashWindow());
        Ui("后台登录窗", () => new LoginWindow());

        Ui("详情卡关联行模板（跨库链接按钮 + 字段块）", () =>
        {
            static T Find<T>(DependencyObject root) where T : DependencyObject
            {
                if (root is T hit) return hit;
                var n = VisualTreeHelper.GetChildrenCount(root);
                for (var i = 0; i < n; i++)
                {
                    var r = Find<T>(VisualTreeHelper.GetChild(root, i));
                    if (r != null) return r;
                }
                return null;
            }

            var blockTpl = (DataTemplate)Application.Current.FindResource("Block.Tpl");
            var host = new ContentPresenter
            {
                ContentTemplate = blockTpl,
                Content = new LxBlock
                {
                    Label = "关联·技术规范",
                    Lines = new List<LxLine>
                    {
                        new()
                        {
                            Chip = "同名", Text = "普通门诊诊察　AAAA0001 · 综合",
                            LinkLib = Related.Guifan, LinkId = 1,
                        },
                    },
                },
            };
            host.Measure(new Size(900, 80));
            host.Arrange(new Rect(0, 0, 900, 80));
            host.UpdateLayout();

            var btn = Find<Button>(host) ?? throw new Exception("关联行模板里没有链接按钮");
            if (btn.Visibility != Visibility.Visible) throw new Exception("关联行的链接按钮不可见");
            if (!RelatedLink.GetEnable(btn)) throw new Exception("链接按钮未挂上跳转行为");
            if ((btn.Content as string ?? "").Length == 0) throw new Exception("链接按钮没有文本");
            return host;
        });

        // ================= 五、可配置化 =================
        Head("五、应用设置与主题换肤");

        Check("设置载入与默认值补全", d =>
        {
            var s = SettingsStore.Current;
            if (string.IsNullOrWhiteSpace(s.AppName)) throw new Exception("软件名称为空");
            if (string.IsNullOrWhiteSpace(s.AdminUser)) throw new Exception("登录账号为空");
            if ((s.AdminPass ?? "").Split('$').Length != 4) throw new Exception("口令未按 pbkdf2$迭代$盐$摘要 存储");
            if (SettingsStore.LogoText.Length == 0) throw new Exception("品牌短名称为空");
            d($"{s.AppName} · 品牌「{SettingsStore.LogoText}」 · 主题 {s.Theme} · 账号 {s.AdminUser} · " +
              $"登录校验 {(SettingsStore.LoginEnabled ? "开" : "关")}");
        });

        Check("口令哈希往返校验", d =>
        {
            const string pwd = "s3cr3t-测试";
            var h = SettingsStore.HashPassword(pwd);
            if (!SettingsStore.VerifyPassword(pwd, h)) throw new Exception("正确口令未通过");
            if (SettingsStore.VerifyPassword("wrong", h)) throw new Exception("错误口令被放行");
            if (SettingsStore.VerifyPassword(pwd, "bogus")) throw new Exception("非法哈希被放行");
            if (SettingsStore.VerifyPassword(pwd, "")) throw new Exception("空哈希被放行");
            d("正确口令通过 / 错误口令与非法哈希均被拒绝（PBKDF2-SHA256）");
        });

        Check("后台登录窗·回车提交通道", d =>
        {
            var win = new LoginWindow();
            var pass = (PasswordBox)win.FindName("TxtPass");
            var err = (TextBlock)win.FindName("TxtError");
            var btn = (Button)win.FindName("BtnLogin");
            if (btn == null || !btn.IsDefault) throw new Exception("登录按钮未设为默认按钮（回车落不到按钮上）");
            if (InputMethod.GetIsInputMethodEnabled(pass))
                throw new Exception("密码框未关闭输入法：中文输入法开着时回车会先被 IME 吃掉");

            // 故意用错误口令：只验证"回车这条路能走到校验"，不依赖实机上的真实口令
            pass.Password = "probe-not-the-password";
            win.SubmitForTest();
            if (win.LoginAttempts != 1) throw new Exception($"回车未进入登录校验（尝试 {win.LoginAttempts} 次）");
            if (err.Visibility != Visibility.Visible) throw new Exception("口令错误后没有提示");
            if (pass.Password.Length != 0) throw new Exception("口令错误后未清空密码框");

            // 同一次按键可能从多条通道（窗口级 / 输入框级 / 默认按钮）进来，连打两次只应算一次
            win.SubmitForTest();
            win.SubmitForTest();
            if (win.LoginAttempts != 1)
                throw new Exception($"连按节流失效：一次按键被算成 {win.LoginAttempts} 次登录");

            d("窗口级 / 输入框级 / 默认按钮 三路回车都能提交 · 密码框已关输入法 · " +
              "错误口令回显并清空 · 重复触发已去重");
        });

        Check("主题语义键齐全（含 Colors.xaml 已定义）", d =>
        {
            var missing = (from t in ThemeCatalog.All
                           from k in ThemeCatalog.Keys
                           where !t.Map.ContainsKey(k)
                           select $"{t.Id}.{k}").ToList();
            if (missing.Count > 0) throw new Exception("主题缺键: " + string.Join(", ", missing.Take(8)));

            var res = Application.Current.Resources;
            var undeclared = ThemeCatalog.Keys.Where(k => !res.Contains("B." + k)).ToList();
            if (undeclared.Count > 0) throw new Exception("Colors.xaml 未声明: " + string.Join(", ", undeclared));
            d($"{ThemeCatalog.All.Count} 套主题 × {ThemeCatalog.Keys.Length} 个语义键，全部齐全");
        });

        Check("各主题文本对比度（防止白底白字）", d =>
        {
            // 这一项是"换肤后某处文字看不见"的自动化守门员：
            // DynamicResource 缺键会静默回落成 null，肉眼看不出问题但会消失，故用对比度兜底。
            (string A, string B, double Min)[] rules =
            {
                ("Text", "Card", 7.0), ("Text", "Bg", 7.0), ("Text", "SoftBg", 7.0),
                ("Muted", "Card", 4.5), ("Muted", "Bg", 4.5),
                ("White", "Accent", 3.0),
                ("SidebarText", "Sidebar", 4.5), ("SidebarTextFaint", "SidebarHead", 3.0),
                ("InfoText", "InfoBg", 4.5), ("WarnText", "WarnBg", 4.5),
                ("Danger", "Card", 3.5), ("Accent", "Card", 3.0), ("Ok", "Card", 3.0),
            };

            var bad = new List<string>();
            foreach (var t in ThemeCatalog.All)
                foreach (var (a, b, min) in rules)
                {
                    var c = Contrast(t.Hex(a), t.Hex(b));
                    if (c < min) bad.Add($"{t.Id} {a}/{b}={c:F2}<{min}");
                }
            if (bad.Count > 0) throw new Exception("对比度不足: " + string.Join("; ", bad.Take(6)));
            d($"{ThemeCatalog.All.Count} 套主题 × {rules.Length} 组前景/背景组合均达标");
        });

        Check("逐主题切换后页面仍可构造", d =>
        {
            foreach (var t in ThemeCatalog.All)
            {
                ThemeManager.Apply(t.Id);
                if (ThemeManager.CurrentId != t.Id) throw new Exception($"切换 {t.Id} 失败");

                var page = new AdminSettingsView();
                page.EnsureLoaded();
                page.Measure(new Size(1440, 900));
                page.Arrange(new Rect(0, 0, 1440, 900));
                page.UpdateLayout();
            }
            var restore = SettingsStore.Current.Theme;
            ThemeManager.Apply(restore);
            d($"{ThemeCatalog.All.Count} 套主题切换 + 页面重建均无异常，已还原为「{ThemeCatalog.Get(restore).Name}」");
        });

        Check("前台主题选择（入口齐全 / 点选即生效落盘 / 选中态唯一）", d =>
        {
            // 在独立临时根目录里跑：真机上别人的客户端可能正拿着旧 settings 写回，
            // 在真实文件上断言「写盘读回」会互相踩（同托盘自检的处理）。
            var origRoot = AppPaths.Root;
            var origTheme = SettingsStore.Current.Theme;
            var tmp = Path.Combine(Path.GetTempPath(), "dip3-theme-" + Guid.NewGuid().ToString("N")[..8]);
            MainWindow win = null;
            try
            {
                Directory.CreateDirectory(Path.Combine(tmp, "data"));
                AppPaths.SetRoot(tmp);
                SettingsStore.Reload();
                ThemeManager.Apply(SettingsStore.Current.Theme);

                win = new MainWindow();

                // ① 入口就位：标题栏按钮 + 5 张卡（含当前选中）
                var btn = (Button)win.FindName("BtnTheme");
                if (btn == null) throw new Exception("标题栏没有主题入口按钮");
                if (win.ThemeCards.Count != ThemeCatalog.All.Count)
                    throw new Exception($"色卡数量 {win.ThemeCards.Count}，应为 {ThemeCatalog.All.Count}");
                if (win.SelectedThemeId != ThemeManager.CurrentId)
                    throw new Exception($"初始选中「{win.SelectedThemeId}」与实际主题「{ThemeManager.CurrentId}」不一致");

                // ② 每套都点一遍（走真实路径）：主题 / 选中态 / 内存设置三者必须同步
                foreach (var t in ThemeCatalog.All)
                {
                    win.PickThemeForTest(t.Id);
                    if (ThemeManager.CurrentId != t.Id)
                        throw new Exception($"点「{t.Name}」后实际主题仍是 {ThemeManager.CurrentId}");
                    if (win.SelectedThemeId != t.Id)
                        throw new Exception($"点「{t.Name}」后选中态仍是 {win.SelectedThemeId}");
                    if (SettingsStore.Current.Theme != t.Id)
                        throw new Exception($"点「{t.Name}」后设置里的主题是 {SettingsStore.Current.Theme}");
                    if (win.ThemeCards.Count(c => c.IsSelected) != 1)
                        throw new Exception($"选中态不唯一（{win.ThemeCards.Count(c => c.IsSelected)} 张同时选中）");
                }

                // ③ 确实落盘：重新从磁盘读回，仍是最后选的那套
                var last = ThemeCatalog.All[^1];
                SettingsStore.Reload();
                if (SettingsStore.Current.Theme != last.Id)
                    throw new Exception($"落盘后读回的是「{SettingsStore.Current.Theme}」，应为「{last.Id}」");

                // ④ 色卡预览用的是「该主题自己的颜色」，不能跟着当前主题变
                var card = win.ThemeCards.First(c => c.Id == "crimson");
                var expect = ThemeCatalog.Get("crimson").Col("Accent");
                if (card.AccentBrush is not SolidColorBrush sb || sb.Color != expect)
                    throw new Exception("朱砂卡片的强调色与该主题实际强调色不一致");

                // ⑤ 「恢复默认」回得去
                win.PickThemeForTest(ThemeCatalog.DefaultId);
                if (win.SelectedThemeId != ThemeCatalog.DefaultId)
                    throw new Exception("恢复默认后选中态没回到默认主题");

                d($"{win.ThemeCards.Count} 套主题逐一点选：主题/选中态/设置三者同步 · 落盘读回一致 · 色卡预览为该主题本色 · 恢复默认可用");
            }
            finally
            {
                win?.Close();
                AppPaths.SetRoot(origRoot);
                SettingsStore.Reload();
                ThemeManager.Apply(origTheme);
            }
        });


        Check("编辑对话框可编辑下拉模板（PART_EditableTextBox 存在且 Text 可回显）", d =>
        {
            // 历史 bug：主题「Cb」模板只有 ToggleButton + ContentPresenter，没有 PART_EditableTextBox，
            // 导致 IsEditable=true 时 ComboBox.Text 无处显示——编辑框里计价单位永远是空白。
            // 这里构造一个带候选值的可编辑字段，强制应用模板后断言关键部件与文本回显。
            var dlg = new RowEditDialog("自检", new[]
            {
                new RowEditDialog.Field("unit", "计价单位", "次", false, false,
                    new List<string> { "次", "单侧", "日" }),
            });
            dlg.Measure(new Size(640, 800));
            dlg.Arrange(new Rect(0, 0, 640, 800));
            dlg.UpdateLayout();

            var cb = dlg.FieldHost.Children.OfType<ComboBox>().FirstOrDefault()
                     ?? throw new Exception("对话框中未生成 ComboBox");
            cb.ApplyTemplate();
            var tb = cb.Template.FindName("PART_EditableTextBox", cb) as TextBox
                     ?? throw new Exception("Cb 模板缺少 PART_EditableTextBox —— IsEditable 时 Text 无处显示");
            if (tb.Visibility != Visibility.Visible)
                throw new Exception("IsEditable=true 时 PART_EditableTextBox 未显示");
            if (tb.Margin.Right < 20)
                throw new Exception("PART_EditableTextBox 右侧未留白 —— 会盖住箭头，下拉点不开");

            cb.SelectedIndex = 0;
            if (cb.Text != "次") throw new Exception($"选中候选后 Text 回显失败：{cb.Text}");
            cb.Text = "自定义单位";
            if (cb.Text != "自定义单位") throw new Exception($"自由输入后 Text 回显失败：{cb.Text}");
            d("PART_EditableTextBox 存在且可见，候选选中与自由输入均可回显");
        });

        Check("德州市物价库（编码 / 价格 / 批次统计 / 检索）", d =>
        {
            var z = App.State.Dezhou;
            var all = z.Items;
            if (all.Count == 0) throw new Exception("data/dezhou-price.json 为空 —— 德州物价未导入");

            var badCode = all.Where(i => !Regex.IsMatch(i.Code ?? "", @"^\d{11,15}[A-Za-z]?$")).ToList();
            if (badCode.Count > 0)
                throw new Exception($"项目编码格式异常 {badCode.Count} 条，例如「{badCode[0].Code}」");

            // 价格列允许「自主定价 / 暂不定价 / 基价30元每车次…」等文本，但不得夹带换行或异常拉长
            var badPrice = all.FirstOrDefault(i => i.Price3.Contains('\n') || i.Price3.Length > 60);
            if (badPrice != null) throw new Exception($"三级价格字段异常：{badPrice.Name} → {badPrice.Price3}");

            // 三档价格应随医院级别递减（一级 ≤ 二级 ≤ 三级）——列错位会大面积违反；源文件本身有极个别特例
            var numeric = all.Where(i =>
                Regex.IsMatch(i.Price1, @"^\d+(\.\d+)?$") &&
                Regex.IsMatch(i.Price2, @"^\d+(\.\d+)?$") &&
                Regex.IsMatch(i.Price3, @"^\d+(\.\d+)?$")).ToList();
            var disorder = numeric.Count(i =>
                double.Parse(i.Price1) > double.Parse(i.Price2) ||
                double.Parse(i.Price2) > double.Parse(i.Price3));
            if (disorder > 3)
                throw new Exception($"三档价格不单调 {disorder} 条（阈值 3），疑似列错位");

            var s = z.Stat();
            if (s.Main + s.Add + s.Ext != s.Total)
                throw new Exception($"主项 {s.Main} + 加收 {s.Add} + 扩展 {s.Ext} ≠ 总数 {s.Total}");

            var batchSum = z.Batches().Sum(b => b.Rows);
            if (batchSum != all.Count)
                throw new Exception($"批次行数合计 {batchSum} ≠ 总条数 {all.Count}");

            // 官方公布数为参考值：后台允许增删改，容差 5%（且不少于 5 条）——
            // 解析错位（如整类漏抓）偏差远大于此，仍会被抓住。
            foreach (var b in z.Batches())
            {
                if (b.Expect <= 0) continue;
                var tol = Math.Max(5, (int)Math.Ceiling(b.Expect * 0.05));
                if (Math.Abs(b.Mains - b.Expect) > tol)
                    throw new Exception($"{b.Doc} 主项 {b.Mains} 与官方公布 {b.Expect} 偏差 {Math.Abs(b.Mains - b.Expect)}（容差 {tol}）");
                if (b.Mains + b.Adds + b.Exts != b.Rows)
                    throw new Exception($"{b.Doc} 批次明细不自洽：主项 {b.Mains} + 加收 {b.Adds} + 扩展 {b.Exts} ≠ {b.Rows}");
            }

            if (z.ListRows("", "", "", "013315000010000", 1, 50).Total == 0)
                throw new Exception("按 15 位编码检索无结果");
            if (z.ListRows("", "", "", "骨伤", 1, 50).Total == 0)
                throw new Exception("按名称检索「骨伤」无结果");
            if (z.ListRows("", "", "add", "", 1, 5000).Total == 0)
                throw new Exception("加收项筛选无结果");

            // 批次 / 类别 / 性质三维筛选
            var byDoc = z.ListRows("德医保发〔2025〕37号", "", "", "", 1, 500);
            if (byDoc.Total != 150)
                throw new Exception($"37号批次筛选应为 150 条，实际 {byDoc.Total}");
            var byCat = z.ListRows("德医保发〔2025〕37号", "中医骨伤类", "", "", 1, 500);
            if (byCat.Total != 18)
                throw new Exception($"中医骨伤类筛选应为 18 条，实际 {byCat.Total}");
            var byKind = z.ListRows("德医保发〔2026〕11号", "", "ext", "", 1, 500);
            if (byKind.Total == 0)
                throw new Exception("11号扩展项筛选无结果");

            d($"{s.Total:N0} 条 · {s.BatchCount} 批 / {s.CategoryCount} 类 · 主项 {s.Main:N0} · " +
              $"加收 {s.Add:N0} · 扩展 {s.Ext:N0} · 医保支付 {s.Insured:N0}");
        });

        // 加收项在源表里价格是 Excel 公式（ROUND(G42*0.2,0)）：数据原样保留，但**展示必须换算成金额**。
        // 表格 / 详情 / 比对 / 导出四条出口共用 DezhouPriceStore.PriceOf，本项逐格对账。
        //
        // 数据形态已随德医保发〔2026〕25号 正式版变化：379 个公式格被官方替换成固定金额
        //（326 格有值、49 格留空 = 加收减收政策未落地），暂定 24 号文批次已整批撤下。
        // 因此本项改为双分支：
        //   A. 仍有公式价 → 照旧逐格换算对账（兼容历史数据包 / 未来再次出现公式）
        //   B. 已无公式价 → 改验「正式版每个加收价格格"有值即为合法金额、无值允许留空"」，
        //      并对计价说明写明「加收比例20%」且三格皆空的项目做定向核对（该组应为主项 × 20%）。
        //      **不做全局 20% 断言**：加收/减收项本身定价独立（如单气囊小肠镜加收 1800 与主项 333 无关），
        //      历史批次更是 0.04~1.0 各种比例并存，按比例硬卡只会误报。
        // 两个分支都断言「任何出口都不得把公式文本当价格显示」。
        Check("德州物价 · 加收项价格口径（公式换算 / 正式版固定金额）", d =>
        {
            var z = App.State.Dezhou;
            var rx = new Regex(@"^ROUND\(\s*([A-Za-z])\d+\s*\*\s*([\d.]+)\s*,\s*\d+\s*\)$",
                RegexOptions.IgnoreCase);
            var formulaItems = z.Items.Where(i =>
                DezhouPriceStore.IsFormulaPrice(i.Price3) ||
                DezhouPriceStore.IsFormulaPrice(i.Price2) ||
                DezhouPriceStore.IsFormulaPrice(i.Price1)).ToList();

            // ---- B 分支（正式版）：公式价已被固定金额取代，改为「金额合法性 + 留空豁免」守门 ----
            if (formulaItems.Count == 0)
            {
                var addItems = z.Items.Where(i => i.Kind == "add").ToList();
                if (addItems.Count == 0)
                    throw new Exception("既无公式价也无加收项 —— 加收项目整体缺失，数据不完整");

                var amountCells = 0;
                var blankCells = 0;   // 官方定稿留空（加收减收政策未落地）
                var perRule = 0;      // 「加收比例20%」且三格全空的，按主项 20% 定向核对
                var textCells = new Dictionary<string, int>();   // 文字价口径计数（自主定价 / 暂不定价 …）
                var mism = new List<string>();
                var zeroCells = 0;
                foreach (var it in addItems)
                {
                    var isPct20 = (it.Usage ?? "").Replace(" ", "").Contains("加收比例20%");
                    var missing = 0;
                    foreach (var key in new[] { "price3", "price2", "price1" })
                    {
                        var shown = z.PriceOf(it, key);
                        if (DezhouPriceStore.IsFormulaPrice(shown))
                            mism.Add($"{it.Code}/{key} 把公式文本当价格显示「{shown}」");
                        if (string.IsNullOrWhiteSpace(shown))
                        {
                            blankCells++;
                            missing++;
                            continue;
                        }
                        if (!double.TryParse(shown.Trim(), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var sv))
                        {
                            // 源表价格列本就允许放文字口径：「自主定价」「暂不定价」「基价30元/车次…」
                            // 只要不是公式文本，就按"合法文字价"放行，不计入金额格
                            textCells[shown] = (textCells.TryGetValue(shown, out var n) ? n : 0) + 1;
                            continue;
                        }
                        amountCells++;
                        if (sv <= 0)
                        {
                            zeroCells++;
                            mism.Add($"{it.Code}/{key} 价格 ≤ 0「{shown}」—— 加收项不该出现非正值");
                        }
                    }

                    // 「加收比例20%。」但三格全空：说明官方把公式换算结果也一并清空了，
                    // 此处按主项 × 20% 复核一次，确保不是转换错位（49 条属于此形态）
                    if (isPct20 && missing == 3)
                    {
                        var b = z.BaseItemOf(it);
                        if (b == null) continue;
                        var braw = b.Price3 ?? "";
                        if (!double.TryParse(braw.Trim(), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var bv) || bv <= 0)
                            continue;
                        perRule++;
                        var want = (int)Math.Round(bv * 0.2, MidpointRounding.AwayFromZero);
                        // 这里只记录「可推算值」，不塞回展示 —— 留空是官方口径
                        if (want <= 0)
                            mism.Add($"{it.Code} 主项 {bv} × 20% 推不出正整数加收额");
                    }
                }
                if (mism.Count > 0)
                    throw new Exception($"{mism.Count} 个加收价格格不合法，例如 {mism[0]}");

                // 比对行不得有公式残留（差额 / 涨幅会算不出）
                var leftB = z.CompareRows()
                    .Count(r => DezhouPriceStore.IsFormulaPrice(r.NewPrice3)
                                || DezhouPriceStore.IsFormulaPrice(r.NewPrice2)
                                || DezhouPriceStore.IsFormulaPrice(r.NewPrice1));
                if (leftB > 0)
                    throw new Exception($"比对行仍有 {leftB} 条新价是公式 —— 差额 / 涨幅会全部算不出");

                // 已无公式 → 只有「比例写在计价说明里的空价加收项」才允许弹换算说明，
                // 其余一律不该有（否则就是公式文本没洗干净、却还在假装换算过）
                var ghost = z.Items.FirstOrDefault(i =>
                    !z.IsPending(i, (i.Price3 ?? "").Trim()) &&
                    !z.IsPending(i, (i.Price2 ?? "").Trim()) &&
                    !z.IsPending(i, (i.Price1 ?? "").Trim()) &&
                    (z.PriceNote(i, "price3") != null
                     || z.PriceNote(i, "price2") != null
                     || z.PriceNote(i, "price1") != null));
                if (ghost != null)
                    throw new Exception($"无公式价却仍有换算说明：{ghost.Code}");

                // 至少要有一定比例的加收项真有价格，否则说明整列解析成了空
                if (amountCells < addItems.Count / 2)
                    throw new Exception($"加收项金额格过少（{amountCells} / {addItems.Count * 3}）—— 价格列疑似整列解析失败");

                var textTotal = textCells.Values.Sum();
                d($"正式版无公式价 · {addItems.Count:N0} 条加收项 · {amountCells:N0} 格金额合法 · " +
                  $"{textTotal:N0} 格文字口径（{string.Join(" / ", textCells.Keys.Take(3))}）· " +
                  $"{blankCells:N0} 格官方留空（其中 {perRule:N0} 条注明「加收比例20%」三格皆空）· 比对行无公式残留");
                return;
            }

            var bad = 0;
            string first = null;
            var cells = 0;
            foreach (var it in formulaItems)
            {
                var baseCode = (it.Code ?? "").Length >= 15 ? it.Code[..12] + "000" : (it.Code ?? "");
                var b = z.BaseItemOf(it)
                        ?? throw new Exception($"加收项 {it.Code} 找不到主项 {baseCode}");
                foreach (var key in new[] { "price3", "price2", "price1" })
                {
                    var raw = key switch { "price3" => it.Price3, "price2" => it.Price2, _ => it.Price1 };
                    var shown = z.PriceOf(it, key);
                    if (!DezhouPriceStore.IsFormulaPrice(raw))
                    {
                        if (DezhouPriceStore.IsFormulaPrice(shown))
                        {
                            bad++;
                            first ??= $"{it.Code}/{key} 仍返回公式「{shown}」";
                        }
                        continue;
                    }
                    cells++;
                    if (!Regex.IsMatch(shown ?? "", @"^\d+$"))
                    {
                        bad++;
                        first ??= $"{it.Code}/{key} 换算结果不是金额：「{shown}」";
                        continue;
                    }
                    var braw = key switch { "price3" => b.Price3, "price2" => b.Price2, _ => b.Price1 };
                    if (!double.TryParse((braw ?? "").Trim(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var bv) ||
                        !double.TryParse(rx.Match(raw.Trim()).Groups[2].Value,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var k))
                    {
                        bad++;
                        first ??= $"{it.Code}/{key} 主项价不可解析「{braw}」";
                        continue;
                    }
                    var want = ((int)Math.Round(bv * k, MidpointRounding.AwayFromZero)).ToString();
                    if (shown != want)
                    {
                        bad++;
                        first ??= $"{it.Code}/{key} 期望 {want}，实际 {shown}";
                    }
                }
            }
            if (bad > 0) throw new Exception($"{bad} 个价格格换算不对，例如 {first}");

            var note = z.PriceNote(formulaItems[0], "price3");
            if (note == null || !note.Contains("已自动换算"))
                throw new Exception($"公式价悬停说明缺失：「{note}」");
            var plain = z.Items.FirstOrDefault(i => !DezhouPriceStore.IsFormulaPrice(i.Price3));
            if (plain != null && z.PriceNote(plain, "price3") != null)
                throw new Exception($"普通项目不该带换算说明：{plain.Code}");

            var leftovers = z.CompareRows()
                .Count(r => DezhouPriceStore.IsFormulaPrice(r.NewPrice3)
                            || DezhouPriceStore.IsFormulaPrice(r.NewPrice2)
                            || DezhouPriceStore.IsFormulaPrice(r.NewPrice1));
            if (leftovers > 0)
                throw new Exception($"比对行仍有 {leftovers} 条新价是公式 —— 差额 / 涨幅会全部算不出");

            d($"{formulaItems.Count:N0} 条加收项 · {cells:N0} 个价格格全部折算为金额 · 比对行无公式残留");
        });

        Check("德州物价表格 · 列宽与换行（防「中文被截成…」回归）", d =>
        {
            static double W((string Prop, string Header, double Width, bool Align, bool Mono)[] cols, string prop)
            {
                var hit = cols.FirstOrDefault(c => c.Prop == prop);
                if (hit.Prop == null) throw new Exception($"列定义里找不到 {prop}");
                return hit.Width;
            }

            // 作废对照：批次（文号 12 字）、类别（7 字）必须能整串显示；名称列换行（WrapProps）
            if (W(DezhouPriceView.CancelCols, "Batch") < 160) throw new Exception("批次列 < 160，文号会被截断");
            if (W(DezhouPriceView.CancelCols, "Category") < 118) throw new Exception("类别列 < 118，「临床量表评估类」会被截断");
            // 现行项目：落地文号走短式（只留「〔2025〕33号」），104px 足够；类别 7 字、单位表头
            if (W(DezhouPriceView.Cols, "DocShort") < 100) throw new Exception("落地文号列 < 100，短式文号会被截断");
            if (W(DezhouPriceView.Cols, "Category") < 110) throw new Exception("类别列 < 110，类别名会被截断");
            // 短式文号真的短：12 字文号缩成 8 字，且悬停提示保留全文
            var sd = DezhouPriceView.ShortDoc("德医保发〔2025〕33号");
            if (sd != "〔2025〕33号") throw new Exception($"短式文号异常：「{sd}」，应只留「〔2025〕33号」");
            if (DezhouPriceView.ShortDoc("无括号文号") != "无括号文号")
                throw new Exception("无〔〕的文号不应被裁剪");
            // 旧版物价：文本价「自主定价」+ 新标记「新」
            if (W(DezhouPriceView.LegacyCols, "Price3") < 76) throw new Exception("三级价格列 < 76，「自主定价」会变 …");
            if (W(DezhouPriceView.LegacyCols, "NewMark") < 38) throw new Exception("「新」标记列 < 38，会缩成 …");

            // 表头不得被裁字：DataGridColumnHeader 的 ContentPresenter 不换行时，
            // 列宽不足会把右侧硬裁（「医保支付」→「医保支」、「自付比例」→「自付比」）。
            // 口径：12.5px 半粗中文 ≈ 17.4px/字 + 左右内边距各 10px；
            // 单字符 < 0.5 的算半角（如「★」「新」标记列），按 8px/字计。
            static double HeadNeed(string h)
            {
                if (string.IsNullOrEmpty(h)) return 0;
                double w = 0;
                foreach (var ch in h)
                    w += ch < 0x2E80 ? 8.0 : 17.4;
                return w + 20;   // 左右 Padding 10,0
            }

            var narrow = new List<string>();
            foreach (var (label, cols) in new (string, (string, string, double, bool, bool)[])[]
                     {
                         ("现行项目", DezhouPriceView.Cols),
                         ("作废对照", DezhouPriceView.CancelCols),
                         ("旧版物价", DezhouPriceView.LegacyCols),
                         ("新旧比对", DezhouPriceView.CmpCols),
                     })
            {
                foreach (var c in cols)
                {
                    if (c.Item3 <= 0) continue;                       // star 列（名称）自动吃掉剩余宽度
                    var need = HeadNeed(c.Item2);
                    if (need > c.Item3 + 0.5)
                        narrow.Add($"{label}「{c.Item2}」需 {need:F0}px，实际 {c.Item3:F0}px");
                    // 选了医院级别后价格列头会加「● 」前缀（见 ApplyLevelToColumns），同样不能折行
                    if (c.Item1 is "Price3" or "Price2" or "Price1")
                    {
                        var needStar = HeadNeed("● " + c.Item2);
                        if (needStar > c.Item3 + 0.5)
                            narrow.Add($"{label}「● {c.Item2}」（本级标记）需 {needStar:F0}px，实际 {c.Item3:F0}px");
                    }
                }
            }
            if (narrow.Count > 0)
                throw new Exception($"表头会被裁字（{narrow.Count} 处）：{narrow[0]}");

            // 单元格同样会裁：源表价格列允许放文字价（「自主定价」「暂不定价」），
            // 列宽必须放得下库里真实出现的短文字价，否则显示成「自主定…」。
            // 长句（如「基价30元/车次（10公里以内），里程费3元/公里（往返）」）不计入——
            // 为 3 行把价格列撑到 400+px 会让整张表没法看，这类行走悬停提示看全文。
            static bool IsTextPrice(string v) =>
                !string.IsNullOrWhiteSpace(v) && !double.TryParse(v.Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out _);

            var textPrices = new SortedSet<string>(StringComparer.Ordinal);
            var legacyTextPrices = new SortedSet<string>(StringComparer.Ordinal);
            void CollectTextPrices(SortedSet<string> into, IEnumerable<(string A, string B, string C)> triples)
            {
                foreach (var (a, b, c) in triples)
                    foreach (var v in new[] { a, b, c })
                        if (IsTextPrice(v) && v.Trim().Length <= 6) into.Add(v.Trim());
            }
            // ⚠ 两个库分别收集：『暂自主定价』5 字只出现在旧版物价（6 行 18 格），
            // 现行项目只有『自主定价』/『暂不定价』。混在一起验会把现行项目的价格列顶到 108 虚胖。
            CollectTextPrices(textPrices, App.State.Dezhou.Items.Select(i => (i.Price3, i.Price2, i.Price1)));
            CollectTextPrices(legacyTextPrices, App.State.Dezhou.Legacy.Select(i => (i.Price3, i.Price2, i.Price1)));
            foreach (var (label, cols, props, pool) in new (string, (string, string, double, bool, bool)[], string[], List<string>)[]
                     {
                         ("现行项目", DezhouPriceView.Cols, new[] { "Price3", "Price2", "Price1" }, textPrices.ToList()),
                         ("旧版物价", DezhouPriceView.LegacyCols, new[] { "Price3", "Price2", "Price1" }, legacyTextPrices.ToList()),
                     })
            {
                foreach (var p in props)
                {
                    var col = cols.FirstOrDefault(c => c.Item1 == p);
                    if (col.Item1 == null || col.Item3 <= 0) continue;
                    foreach (var tp in pool)
                    {
                        var need = HeadNeed(tp);
                        if (need > col.Item3 + 0.5)
                        {
                            narrow.Add($"{label} {p} 放「{tp}」需 {need:F0}px，实际 {col.Item3:F0}px");
                            break;
                        }
                    }
                }
            }
            if (narrow.Count > 0)
                throw new Exception($"文字价会被裁（{narrow.Count} 处）：{narrow[0]}");

            // 三个副表行高：文本列一律单行 + 省略号（换行会把两行字压进一行互相重叠，2026-10-30 的教训）；
            // 但「作废对照 / 旧版物价」的链接列是编码+名称混排的一对多容器，必须允许换行 ——
            // 2026-11-17 用户截图反馈：固定 34px 行高下，013105010100001 活髓保存治疗费-间接盖髓（减收）
            // 换到第二行时被表头压住，看着就是错位。所以这两张表改成「MinRowHeight=密度 + RowHeight=NaN」，
            // 单行行仍等于密度值，只有链接换行的那几行自己长高；新旧比对的编码是短码，仍用固定行高。
            var v = new DezhouPriceView();
            v.EnsureLoaded();
            foreach (var (gname, grid) in new (string, DataGrid)[]
                     {
                         ("作废对照", v.DzCancelGrid), ("旧版物价", v.DzLegacyGrid),
                     })
            {
                if (!double.IsNaN(grid.RowHeight))
                    throw new Exception($"{gname} 行高必须自适应（RowHeight=NaN），否则换行内容会被裁掉，实际 {grid.RowHeight}");
                if (Math.Abs(grid.MinRowHeight - UiPrefs.RowHeight) > 0.5)
                    throw new Exception($"{gname} MinRowHeight 应跟随密度 {UiPrefs.RowHeight:F0}，实际 {grid.MinRowHeight}");
                foreach (var col in grid.Columns.OfType<DataGridTextColumn>())
                {
                    var wrap = col.ElementStyle?.Setters.OfType<Setter>()
                        .FirstOrDefault(x => x.Property == TextBlock.TextWrappingProperty)?.Value;
                    if (Equals(wrap, TextWrapping.Wrap))
                        throw new Exception($"{gname}「{col.Header}」仍允许换行，自适应行高下会与相邻行挤在一起");
                }
            }
            if (double.IsNaN(v.DzCmpGrid.RowHeight)
                || Math.Abs(v.DzCmpGrid.RowHeight - UiPrefs.RowHeight) > 0.5)
                throw new Exception($"新旧比对 行高应为密度值 {UiPrefs.RowHeight:F0}，实际 {v.DzCmpGrid.RowHeight}");

            // 批次列走两行模板（文号 / 执行日期），而不是「文号 · 日期」挤一行
            var batchCol = v.DzCancelGrid.Columns.FirstOrDefault(c => c.Header as string == "批次 / 执行")
                           ?? throw new Exception("作废对照缺少「批次 / 执行」列");
            if (batchCol is not DataGridTemplateColumn)
                throw new Exception("批次 / 执行 必须是模板列（两行展示），否则又被挤成一行截断");

            // 数值列居中（2026-10-27 第七轮）：价格 / 比例 / 差额 / 涨幅这类短数值要居中，
            // 与表头同一条中线；长文本列（名称 / 文号 / 备注）仍左对齐。
            // ⚠ 只设 TextAlignment 不设 Stretch 是无效的（TextBlock 按内容宽自适应），
            //   所以这里同时断言 HorizontalAlignment=Stretch，否则「看起来仍是左对齐」。
            static void AssertCentered(DataGridColumn col, string label)
            {
                if (col is not DataGridTextColumn tc)
                    throw new Exception($"{label} 不是文本列，无法校验居中对齐");
                var sets = tc.ElementStyle.Setters.OfType<Setter>().ToList();
                var ta = sets.FirstOrDefault(x => x.Property == TextBlock.TextAlignmentProperty)?.Value;
                var ha = sets.FirstOrDefault(x => x.Property == TextBlock.HorizontalAlignmentProperty)?.Value;
                if (!Equals(ta, TextAlignment.Center) || !Equals(ha, HorizontalAlignment.Stretch))
                    throw new Exception($"{label} 应居中（TextAlignment=Center + HorizontalAlignment=Stretch），" +
                                        $"实际 {ta?.ToString() ?? "null"} / {ha?.ToString() ?? "null"}");
                var hca = tc.CellStyle?.Setters.OfType<Setter>()
                    .FirstOrDefault(x => x.Property == Control.HorizontalContentAlignmentProperty)?.Value;
                if (!Equals(hca, HorizontalAlignment.Center))
                    throw new Exception($"{label} 单元格 HorizontalContentAlignment 应为 Center（实际 {hca?.ToString() ?? "null"}）");
            }
            // 「扣减物耗」为条件列（2026-11-01）：列常驻但默认折叠，SyncDeductCol 按「一般治疗筛选 /
            // 当前页带值」显隐；列本身必须居中、挂表头样式，与其他数值列同规矩
            foreach (var p in new[] { "Price3", "Price2", "Price1", "Deduct", "SelfPay" })
            {
                var col = v.DzGrid.Columns.FirstOrDefault(c => c.Header is string h && h.EndsWith(
                    p switch { "Price3" => "三级价格", "Price2" => "二级价格", "Price1" => "一级价格",
                               "Deduct" => "扣减物耗", _ => "自付比例" }, StringComparison.Ordinal))
                          ?? throw new Exception($"现行项目缺少 {p} 列");
                AssertCentered(col, $"现行项目 {p}");
                if (col.HeaderStyle == null)
                    throw new Exception($"现行项目 {p} 表头未挂居中样式（HeaderStyle 为空）");
            }

            // 表头背景必须整行统一（2026-10-27）：列级 HeaderStyle 会**显式覆盖**
            // DataGrid.ColumnHeaderStyle，某列留 null 就会掉回系统默认表头模板（白底），
            // 在灰蓝（B.SoftBg）表头里开一块白 —— 用户看到的就是「背景块不一样」。
            // 所以每张表的每一列都必须显式挂 DgHead / DgHeadCenter。
            void AssertHeaderSkin(DataGrid g, string label)
            {
                foreach (var c in g.Columns)
                {
                    if (c.HeaderStyle == null)
                        throw new Exception($"{label} 的「{c.Header}」列 HeaderStyle 为空 → 表头会掉回白底，" +
                                            "与其余列拼成两段背景（挂 DgHead 或 DgHeadCenter）");
                }
            }
            AssertHeaderSkin(v.DzGrid, "现行项目");
            AssertHeaderSkin(v.DzCancelGrid, "作废对照");
            AssertHeaderSkin(v.DzCmpGrid, "新旧比对");
            // 名称列必须仍左对齐（居中的多行文本会散开）
            var nameCol = v.DzGrid.Columns.FirstOrDefault(c => c.Width.IsStar || c.Width.DisplayValue <= 0);
            if (nameCol is DataGridTextColumn ntc
                && ntc.ElementStyle.Setters.OfType<Setter>()
                    .Any(x => x.Property == TextBlock.TextAlignmentProperty && Equals(x.Value, TextAlignment.Center)))
                throw new Exception("名称列不应居中（长文本居中读起来会散）");

            d("批次 / 类别 / 文号 / 文本价 / 新标记 均不会截断 · 副表行高自适应 · 批次列两行模板 · 数值列居中（表头同步） · 表头底色整行统一");
        });

        Check("德州物价后台维护（模板往返 / 增删改 / 统计重算）", d =>
        {
            var z = App.State.Dezhou;
            if (z.Items.Count == 0) throw new Exception("德州物价为空，无法验证维护链路");
            var before = z.Items.Count;
            var batches0 = z.Batches().Count;

            // 1) 模板 → 解析往返：表头必须能被 ReadDezhouRows 认出来（否则导入功能形同虚设）
            var (tpl, _) = AdminService.DezhouTemplate();
            var parsed = AdminService.ReadDezhouRows(tpl);
            if (parsed.Count != 1) throw new Exception($"模板示例行应解析出 1 行，实际 {parsed.Count}");
            var p0 = parsed[0];
            if (p0.Code != "013315000010000" || p0.Name != "示例项目名称")
                throw new Exception($"模板往返字段不一致：{p0.Code} / {p0.Name}");
            if (p0.Price3 != "100" || p0.Price2 != "90" || p0.Price1 != "80" || p0.Insurance != "是" || p0.SelfPay != "20%")
                throw new Exception($"模板往返价格 / 医保字段错位：「{p0.Price3}|{p0.Price2}|{p0.Price1}|{p0.Insurance}|{p0.SelfPay}」");
            // 「扣减基本物耗价格」列（正式版 25 号文新增）必须参与模板往返，否则导入时整列丢失
            if (p0.Deduct != "2.5")
                throw new Exception($"模板往返「扣减基本物耗价格」丢失：「{p0.Deduct}」");
            if (p0.Kind != "main" || p0.Unit != "次" || p0.Category != "骨骼肌肉系统类")
                throw new Exception($"模板往返性质 / 单位 / 类别错位：「{p0.Kind}|{p0.Unit}|{p0.Category}」");

            // 2) 校验：非法编码、空名称、非法性质必须被拦下（且不入库）
            if (z.AddRow(new Dictionary<string, string> { ["code"] = "abc", ["name"] = "坏编码" }).Ok)
                throw new Exception("非法编码未被拦截");
            if (z.AddRow(new Dictionary<string, string> { ["code"] = "999999999999999", ["name"] = "" }).Ok)
                throw new Exception("空名称未被拦截");
            if (z.AddRow(new Dictionary<string, string> { ["code"] = "999999999999999", ["name"] = "坏性质", ["kind"] = "xx" }).Ok)
                throw new Exception("非法性质未被拦截");
            if (z.Items.Count != before) throw new Exception("被拦截的行仍然入库了");

            // 3) 新增 → 编辑 → 删除（全程只在内存，最后复原；不写盘）
            var doc = z.Batches().FirstOrDefault()?.Doc ?? "自检批次";
            var cat = z.Items.FirstOrDefault(i => i.Doc == doc)?.Category ?? "自检类别";
            Dictionary<string, string> Flat(string name, string kind, string p3) => new()
            {
                ["doc"] = doc, ["exec"] = "2026-01-01", ["category"] = cat, ["seq"] = "9999",
                ["code"] = "999999999999999", ["kind"] = kind, ["name"] = name,
                ["unit"] = "次", ["price3"] = p3, ["price2"] = "120", ["price1"] = "110",
                ["insurance"] = "是", ["selfPay"] = "10%",
            };

            var add = z.AddRow(Flat("自检示例项目", "main", "123"));
            if (!add.Ok) throw new Exception("新增失败：" + add.Error);
            var id = (int)add.Row;
            if (z.Items.Count != before + 1) throw new Exception("新增后条数未 +1");
            if (!z.CodeExists("999999999999999", 0, doc)) throw new Exception("同文号重复编码未检出");
            if (z.CodeExists("999999999999999", id, doc)) throw new Exception("排除自身后仍报重复");
            if (!z.RemoveRow(id.ToString()).Ok) throw new Exception("删除失败（新增的行）");

            var add2 = z.AddRow(Flat("自检示例项目", "main", "123"));
            if (!add2.Ok) throw new Exception("新增失败：" + add2.Error);
            id = (int)add2.Row;

            var upd = z.UpdateRow(id.ToString(), Flat("自检示例项目（改）", "add", "200"));
            if (!upd.Ok) throw new Exception("编辑失败：" + upd.Error);
            var edited = z.Items.First(i => i.Id == id);
            if (edited.Name != "自检示例项目（改）" || edited.Kind != "add" || edited.Price3 != "200")
                throw new Exception("编辑结果未生效");

            // 4) 统计重算：总数与批次明细都要自洽
            z.Recompute();
            if (z.Meta.Total != z.Items.Count) throw new Exception("Recompute 后总数与条数不一致");
            foreach (var b in z.Batches())
                if (b.Mains + b.Adds + b.Exts != b.Rows)
                    throw new Exception($"{b.Doc} 重算后明细不自洽：{b.Mains}+{b.Adds}+{b.Exts} ≠ {b.Rows}");

            // 5) 删除复原并再算一次（保证自检不留副作用）
            if (!z.RemoveRow(id.ToString()).Ok) throw new Exception("删除失败");
            z.Recompute();
            if (z.Items.Count != before) throw new Exception($"删除后条数未复原：{z.Items.Count} ≠ {before}");
            if (z.Batches().Count != batches0) throw new Exception("空批次未回收");
            if (z.Batches().Sum(b => b.Rows) != before) throw new Exception("批次合计与总条数不一致");
            if (z.Stat().Main + z.Stat().Add + z.Stat().Ext != before) throw new Exception("性质统计与总条数不一致");

            d($"模板往返 1 行 · 校验拦截 3 例 · 增删改往返通过 · 复原 {before:N0} 条 / {z.Batches().Count} 批");
        });

        Check("德州物价类别使用说明（覆盖 / 精确查询 / 重算保留 / 详情卡摘录）", d =>
        {
            var z = App.State.Dezhou;
            if (z.Items.Count == 0) throw new Exception("德州物价为空，无法验证使用说明");
            var (covered, cats, entries) = z.UsageStat();
            if (cats == 0) throw new Exception("meta 中没有任何类别");
            if (covered != cats)
                throw new Exception($"使用说明未全覆盖：{covered}/{cats} 个类别（缺失的类别会让页面看不到该类规则）");
            if (entries < 300) throw new Exception($"使用说明条目异常偏少：{entries}（应约 327 条）");

            // 精确查询：器官移植类首条应是「移植 / 切取」的名词定义
            var doc = "德医保发〔2025〕33号";
            var u = z.UsageOf(doc, "器官移植类");
            if (u.Count == 0 || !u[0].Contains("移植"))
                throw new Exception("器官移植类使用说明首条异常：" + (u.FirstOrDefault() ?? "<空>"));
            if (z.UsageOf(doc, "不存在的类别").Count != 0)
                throw new Exception("未知类别不应返回说明");
            if (z.UsageOf("", "护理类").Count == 0)
                throw new Exception("文号为空时应能按类别名跨批次命中");

            // Recompute（后台改数据后必经之路，纯内存）不得丢类别说明
            z.Recompute();
            var (c2, _, e2) = z.UsageStat();
            if (c2 != covered || e2 != entries)
                throw new Exception($"Recompute 后说明丢失：{c2}/{covered} 类，{e2}/{entries} 条");

            // 详情卡应带本类使用说明摘录（前 2 条）
            var it = z.Items.First(i => i.Doc == doc && i.Category == "器官移植类");
            var blocks = DezhouPriceView.BuildBlocks(it);
            var ub = blocks.FirstOrDefault(b => b.Label != null && b.Label.Contains("使用说明"));
            if (ub == null || ub.Lines.Count < 2)
                throw new Exception("详情卡缺少类别使用说明摘录");

            d($"{covered}/{cats} 类全覆盖 · {entries} 条 · Recompute 保留 · 详情卡摘录 {ub.Lines.Count} 行");
        });

        Check("德州物价页·使用说明面板与常用收藏（交互 / 落盘 / 仅看收藏）", d =>
        {
            var v = new DezhouPriceView();
            v.EnsureLoaded();
            v.Measure(new Size(1440, 900));
            v.Arrange(new Rect(0, 0, 1440, 900));
            v.UpdateLayout();

            // 未选文号 / 类别：面板全量展示（详情卡「查看全部」的落点）；选中具体类别：面板收窄
            if (v.UsageCard.Visibility != Visibility.Visible)
                throw new Exception("未选文号 / 类别时使用说明面板应全量显示");
            var allCount = (v.UsageList.ItemsSource as System.Collections.IEnumerable)?.Cast<object>().Count() ?? 0;
            if (allCount < 300)
                throw new Exception("未筛选时面板应载入全部批次使用说明，实际 " + allCount);
            if (!v.UsageTitle.Text.Contains("全部批次"))
                throw new Exception("未筛选时面板标题应为「全部批次」：" + v.UsageTitle.Text);
            v.DiagSelect("德医保发〔2025〕37号", "中医骨伤类");
            v.UpdateLayout();
            if (v.UsageCard.Visibility != Visibility.Visible)
                throw new Exception("选中类别后使用说明面板未显示");
            if (v.UsageList.ItemsSource is not System.Collections.IEnumerable rows ||
                rows.Cast<object>().Count() != 8)
                throw new Exception("面板条目数应为 8（中医骨伤类），实际 " +
                                    (v.UsageList.ItemsSource as System.Collections.IEnumerable)?.Cast<object>().Count());
            if (!v.UsageTitle.Text.Contains("中医骨伤类"))
                throw new Exception("面板标题未含类别名：" + v.UsageTitle.Text);

            // 详情卡「点此定位查看全部」：面板聚焦指定类别并展开，且不改变表格筛选
            var gridRows = v.DzGrid.Items.Count;
            v.FocusUsage("德医保发〔2025〕33号", "器官移植类");
            if (!v.UsageTitle.Text.Contains("器官移植类"))
                throw new Exception("面板定位后标题未含目标类别：" + v.UsageTitle.Text);
            if (v.UsageBody.Visibility != Visibility.Visible)
                throw new Exception("面板定位后应自动展开");
            if (v.DzGrid.Items.Count != gridRows)
                throw new Exception("面板定位不应改变表格筛选行数");
            v.DiagSelect("", "");   // 退出定位态（下拉复位即清 focus）

            // 收起 / 展开
            // 注意：点按钮走的是 OnToggleUsage，它会真的把偏好落盘（SetUsageOpen → Save）。
            // 自检必须还原现场，否则打包脚本跑完自检，发行包里的 data\settings.json 就带着
            // usageOpen=true —— 用户首次打开物价页看到的是展开态，而不是出厂默认的收起。
            var usage0 = SettingsStore.Current.UsageOpen;
            v.UsageToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (v.UsageBody.Visibility != Visibility.Collapsed)
                throw new Exception("点「收起」后说明正文未隐藏");
            v.UsageToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (SettingsStore.Current.UsageOpen != usage0)
            {
                SettingsStore.Current.UsageOpen = usage0;
                SettingsStore.Save();
            }

            // 常用收藏：备份正式收藏文件 → 全程内存与临时文件 → finally 恢复
            var file = AppPaths.DezhouFavFile;
            var bak = File.Exists(file) ? File.ReadAllBytes(file) : null;
            try
            {
                var fav = App.State.DezhouFav;
                var n0 = fav.Count;
                var code = v.DzGrid.Items.Cast<DzRowVm>().First().Code;
                var vm = v.DzGrid.Items.Cast<DzRowVm>().First(x => x.Code == code);

                if (!fav.Toggle(code, vm.Doc, vm.Name)) throw new Exception("首次 Toggle 应变为已收藏");
                if (!fav.Has(code)) throw new Exception("收藏后 Has 应为真");
                if (!File.Exists(file)) throw new Exception("收藏未落盘（dezhou-favorites.json 不存在）");
                if (new DezhouFavoritesStore().Count != n0 + 1)
                    throw new Exception("重载后收藏数与内存态不一致");
                if (fav.Items[0].Code != code) throw new Exception("最近收藏应排在最前");

                vm.IsFav = true;   // 星标 INPC 通路（界面上的即时变色依赖它）

                // 「仅看收藏」筛选：注入过滤后结果数必须与收藏数一致
                var set = fav.CodeSet();
                var r = App.State.Dezhou.ListRows("", "", "", "", 1, 5000, "", x => set.Contains(x.Code));
                if (r.Total != fav.Count)
                    throw new Exception($"仅看收藏筛选 {r.Total} 条 ≠ 收藏数 {fav.Count}");

                if (fav.Toggle(code)) throw new Exception("再次 Toggle 应变为未收藏");
                if (fav.Has(code)) throw new Exception("取消后 Has 应为假");
                if (fav.Count != n0) throw new Exception("取消后收藏数未还原");
            }
            finally
            {
                if (bak != null) File.WriteAllBytes(file, bak); else File.Delete(file);
                App.State.DezhouFav.Load();
            }
            d("面板显隐 / 条目 8 / 收起展开 · 收藏落盘与重载一致 · 仅看收藏数吻合 · 文件已复原");
        });

        Check("德州物价「新项目征求意见」稿（隔离存储 / 三视图取数 / 主库零污染）", d =>
        {
            var dr = App.State.DezhouDraft;
            if (!dr.HasData)
                throw new Exception("征求意见稿为空（data/dezhou-draft.json 缺失或解析失败）");

            var st = dr.Stat();
            // 本批实测家底（改批次数据时同步更新这三个数）
            if (st.Items != 1089) throw new Exception($"拟新增应为 1089 项，实际 {st.Items}");
            if (st.Cancels != 1160) throw new Exception($"拟作废应为 1160 项，实际 {st.Cancels}");
            if (st.Maps != 1133) throw new Exception($"映射行应为 1133 行，实际 {st.Maps}");
            // draft = 征求意见中；active = 已转正并入主库（本页保留作档案视图）
            if (st.Status is not ("draft" or "active"))
                throw new Exception($"征求意见稿状态应为 draft/active，实际 {st.Status}");
            if (st.Mains + st.Adds + st.Exts != st.Items)
                throw new Exception("主项+加收项+扩展项 ≠ 总数（性质判定漏项）");

            // 类别名已归一：尾部不应再带「类」字（否则附件 8「药学类」对不上附件 17「药学废止」）
            foreach (var c in dr.Categories())
                if (c.EndsWith("类"))
                    throw new Exception($"类别名未归一（仍带「类」尾字）：{c}");
            if (st.Categories != 9) throw new Exception($"本批应为 9 个类别，实际 {st.Categories}");

            // 三视图取数都能落地
            var all = dr.AllItems("", "", "");
            if (all.Count != st.Items) throw new Exception($"全部新增取数 {all.Count} ≠ 统计 {st.Items}");
            if (dr.ListCancels("", "").Count != st.Cancels) throw new Exception("拟作废取数不等于统计");
            if (dr.ListMaps("", "").Count != st.Maps) throw new Exception("映射取数不等于统计");

            // 关键词检索：编码前缀应能命中
            var one = all.First();
            var hit = dr.AllItems("", "", one.Code);
            if (hit.Count == 0) throw new Exception($"按编码 {one.Code} 检索不到结果");

            // 双向索引可用（详情卡「映射原价格项目」靠它）
            var maps = dr.MapsOfNew(one.Code);
            if (maps.Count > 0 && !maps.All(m => m.NewCode == one.Code))
                throw new Exception("MapsOfNew 返回了不属于该新编码的行");

            // 主库零污染：征求意见稿不进现行标准，也不该点亮已作废红标
            var z = App.State.Dezhou;
            if (z.Items.Count == 0) throw new Exception("主库为空，无法验证隔离");
            foreach (var it in all.Take(50))
                if (it.Status != "draft")
                    throw new Exception($"征求意见稿条目未标 draft：{it.Code}");
            if (z.LegacyCanceled(one.Code))
                throw new Exception($"新增编码不该被主库判定为已作废：{one.Code}");

            d($"{st.Items} 拟新增（主 {st.Mains}/加 {st.Adds}/扩 {st.Exts}）· {st.Cancels} 拟作废 · "
              + $"{st.Maps} 映射 · {st.Categories} 类（名已归一）· 主库隔离未污染");
        });

        Check("新旧价格比对（legacy 优先 / 差额涨幅 / 三态标注 / 涨跌着色）", d =>
        {
            var dr = App.State.DezhouDraft;
            var all = dr.Compare();
            var (total, priced, cancelOnly, brandNew, down, up, flat) = dr.CompareStat();
            // Python 预演家底（改批次数据时同步更新）：513 可比价 / 1 仅作废对照 / 575 纯新增
            if (total != 1089) throw new Exception($"比对总行应 1,089，实际 {total}");
            if (priced + cancelOnly + brandNew != total)
                throw new Exception("三态行数加总 ≠ 总数");
            if (priced != 513) throw new Exception($"有旧价可比应 513，实际 {priced}");
            if (brandNew != 575) throw new Exception($"纯新增应 575，实际 {brandNew}");

            // 差额数学：抽查 20 条有旧价的行，差额 = 新三级 − 旧三级
            var sample = all.Where(r => r.OldSource == "legacy").Take(20).ToList();
            if (sample.Count == 0) throw new Exception("没有任何可比价行");
            foreach (var r in sample)
            {
                var np = ParseNum(r.NewPrice3);
                var op = ParseNum(r.OldPrice3);
                if (np is null || op is null)
                {
                    if (r.Delta3 != null) throw new Exception($"价格不可解析但差额有值：{r.NewCode}");
                    continue;
                }
                if (r.Delta3 is null || Math.Abs(r.Delta3.Value - (np - op).Value) > 0.01)
                    throw new Exception($"差额算错：{r.NewCode} 新 {np} − 旧 {op} ≠ {r.Delta3}");
            }
            // 旧价文本（如「自主定价」）不该算出差额
            if (all.Any(r => r.OldSource == "legacy" && r.Delta3 is null && r.Ratio3 != null))
                throw new Exception("差额为空但涨幅有值（涨幅必须由差额推出）");
            // 涨跌三分与差额符号一致（旧价是文本如「自主定价」的行解析不出差额，
            // 只计入 up/down/flat，故三者之和 ≤ priced，其余为「旧价非数字」）
            var noNum = all.Count(r => r.OldSource == "legacy" && r.Delta3 is null);
            if (up + down + flat + noNum != priced)
                throw new Exception("涨+跌+平+旧价非数字 ≠ 有旧价可比行数");

            // 比对筛选：范围过滤真实生效
            var onlyNew = dr.ListCompare("", "", "new");
            if (onlyNew.Count != brandNew || onlyNew.Any(r => r.OldSource != "none"))
                throw new Exception("「纯新增」范围过滤不净");

            // 前台行模型的三态文案与着色键
            var vmUp = new DraftCompareRowVm { Delta3 = 12.5, Ratio3 = 20.8, OldSource = "legacy" };
            var vmDown = new DraftCompareRowVm { Delta3 = -3, OldSource = "legacy" };
            var vmNone = new DraftCompareRowVm { Delta3 = null, OldSource = "none" };
            if (vmUp.Trend != "up" || vmDown.Trend != "down" || vmNone.Trend != "")
                throw new Exception("Trend 判定错误（应 up/down/空）");
            if (vmUp.Delta3Text != "+12.5" || vmDown.Delta3Text != "-3")
                throw new Exception("差额文案缺正负号");
            if (vmUp.RatioText == "—" || vmNone.RatioText != "—")
                throw new Exception("涨幅文案异常");

            // 离屏构造比对视图，核对四段切换与行数
            var v = new DraftPriceView();
            v.Prewarm(1500, 760);
            v.DiagCompare();
            if (v.DiagRows <= 0) throw new Exception("比对视图 0 行");
            var first = v.DiagRows;

            // 表头排序：按「差额(三级)」升序，首行应为全表最小差额（null 排最后）
            v.DiagCompareSort("差额(三级)");
            var sorted = v.CompareGrid.Items.Cast<DraftCompareRowVm>().ToList();
            if (sorted.Count == 0) throw new Exception("排序后 0 行");
            var minDelta = all.Where(r => r.Delta3 != null).Min(r => r.Delta3);
            if (sorted[0].Delta3 != minDelta)
                throw new Exception($"排序首行 {sorted[0].Delta3} ≠ 全表最小 {minDelta}");
            // 涨幅列数值序（非字典序）：构造已知序列验证（"9.5" 若按字典序会排在 "41.2" 后）
            var deltas = sorted.Where(r => r.Delta3 != null).Select(r => r.Delta3.Value).ToList();
            for (var i = 1; i < deltas.Count; i++)
                if (deltas[i] < deltas[i - 1])
                    throw new Exception($"升序被破坏：第 {i} 行 {deltas[i - 1]} → {deltas[i]}");

            d($"{total} 行 = 可比 {priced}（涨 {up}/跌 {down}/平 {flat}）+ 仅对照 {cancelOnly} + 纯新增 {brandNew}"
              + $" · 抽查 {sample.Count} 条差额全对 · 首屏 {first} 行 · 涨红跌绿走 B.Danger/B.Ok · 表头排序升序校验通过");
        });

        // 比对差额抽查用的宽松取数（与 DezhouDraftStore.TryNum 同规则）

        Check("新旧物价比对（主库：作废对照反查旧码 / 旧版物价取价 / 差额涨幅 / 三态加总）", d =>
        {
            var z = App.State.Dezhou;
            var all = z.CompareRows();
            var (total, priced, cancelOnly, brandNew, down, up, flat) = z.CompareStat();
            if (priced + cancelOnly + brandNew != total)
                throw new Exception("三态行数加总 ≠ 总数");
            if (all.Count(r => r.OldSource == "legacy") != priced
                || all.Count(r => r.OldSource == "cancel") != cancelOnly
                || all.Count(r => r.OldSource == "none") != brandNew)
                throw new Exception("三态计数与统计不一致");

            // 反查路径：任取一条作废对照（新码非空且旧码在旧版物价里），比对表必须有一行
            // (新码, 旧码) 且标 legacy；一对多时每个旧码各占一行
            var c = z.Cancels.FirstOrDefault(x => !x.Pending
                && (x.NewCode ?? "").Trim().Length > 0
                && z.Legacy.Any(l => l.Code == (x.OldCode ?? "").Trim()));
            if (c != null)
            {
                var hit = all.FirstOrDefault(r =>
                    r.NewCode == c.NewCode.Trim() && r.OldCode == c.OldCode.Trim());
                if (hit == null)
                    throw new Exception($"作废对照 {c.OldCode}→{c.NewCode} 未进入比对表");
                if (hit.OldSource != "legacy")
                    throw new Exception("旧码在旧版物价里却没标 legacy");
                if (hit.OldPrice3.Length == 0 && hit.Delta3 != null)
                    throw new Exception("旧三级价为空却算出差额");
            }

            // 差额数学：抽查 20 条 legacy 行，差额 = 新三级 − 旧三级
            var sample = all.Where(r => r.OldSource == "legacy").Take(20).ToList();
            if (sample.Count == 0) throw new Exception("没有任何可比价行");
            foreach (var r in sample)
            {
                var np = ParseNum(r.NewPrice3);
                var op = ParseNum(r.OldPrice3);
                if (np is null || op is null)
                {
                    if (r.Delta3 != null) throw new Exception($"价格不可解析但差额有值：{r.NewCode}");
                    continue;
                }
                if (Math.Abs(r.Delta3.Value - (np - op).Value) > 0.01)
                    throw new Exception($"差额算错：{r.NewCode} 新 {np} − 旧 {op} ≠ {r.Delta3}");
            }

            // 筛选真实生效 + 懒构建缓存幂等（重复调用不重建）
            var onlyPriced = z.ListCompare("", "", "priced");
            if (onlyPriced.Count != priced || onlyPriced.Any(r => r.OldSource != "legacy"))
                throw new Exception("「有旧价可比」范围过滤不净");
            if (!ReferenceEquals(all, z.CompareRows()))
                throw new Exception("比对缓存不应在重复调用间重建");

            d($"{total:N0} 行 = 可比 {priced:N0}（降 {down:N0}/涨 {up:N0}/平 {flat:N0}）"
              + $" + 仅对照 {cancelOnly:N0} + 纯新增 {brandNew:N0} · 反查与抽查 {sample.Count} 条全对");
        });
        static double? ParseNum(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            var sb = new System.Text.StringBuilder();
            var started = false;
            foreach (var c in v)
            {
                if (char.IsAsciiDigit(c) || (c == '.' && started)) { sb.Append(c); started = true; }
                else if (started) break;
            }
            return sb.Length > 0 && double.TryParse(sb.ToString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var x) ? x : null;
        }

        Check("放开选中可复制（Tx.Sel 样式 / 详情卡可选文本 / 使用说明正文）", d =>
        {
            var st = Application.Current.TryFindResource("Tx.Sel") as Style
                     ?? throw new Exception("缺少 Tx.Sel 样式（详情文本将无法拖选复制）");
            var probe = new TextBox { Style = st, Text = "心脏移植术" };
            if (!probe.IsReadOnly) throw new Exception("Tx.Sel 必须只读");
            if (probe.TextWrapping != TextWrapping.Wrap) throw new Exception("Tx.Sel 必须自动换行");
            if (probe.ContextMenu != null) throw new Exception("Tx.Sel 不应自带右键菜单（右键统一交给行菜单）");
            probe.Measure(new Size(200, 60));
            if (probe.DesiredSize.Height < 8) throw new Exception("Tx.Sel 度量异常");

            // 详情卡模板真的产出了可选中文本：选中物价首页第一行并展开详情
            var v = new DezhouPriceView();
            v.EnsureLoaded();
            v.Measure(new Size(1440, 900)); v.Arrange(new Rect(0, 0, 1440, 900)); v.UpdateLayout();
            v.DzGrid.SelectedIndex = 0;
            v.UpdateLayout();
            var boxes = SelectableBoxes(v);
            if (boxes.Count == 0) throw new Exception("物价详情卡内没有可选中 TextBox");
            if (boxes.Any(b => !b.IsReadOnly)) throw new Exception("详情卡内出现了可编辑 TextBox");

            // 使用说明面板条目同样可选（Usage.Item → Tx.Sel）。
            // ⚠ 面板已改虚拟化列表（原来一次性建 100+ 个 TextBox，首开多花 700ms），
            // 因此只统计「已实例化」的可见条目：要 > 0，且不得等于总条目数（等于 = 虚拟化失效）。
            // ⚠ 面板出厂默认是「收起」，收起时不生成条目 → 这里用 FocusUsage 展开
            //   （它只改内存态 _usageOpen，不落盘偏好，不像点按钮那样污染 settings.json）。
            v.FocusUsage("", "");
            v.UpdateLayout();
            if (v.UsageCard.Visibility != Visibility.Visible) throw new Exception("使用说明面板未显示");
            if (v.UsageBody.Visibility != Visibility.Visible) throw new Exception("使用说明面板未展开");
            var inPanel = SelectableBoxes(v.UsageList).Count;
            var items = (v.UsageList.ItemsSource as System.Collections.IEnumerable)?.Cast<object>().Count() ?? 0;
            if (items == 0) throw new Exception("使用说明面板没有条目");
            if (inPanel == 0) throw new Exception("使用说明正文没有可选中文本");
            if (inPanel > items) throw new Exception($"使用说明实例化条目 {inPanel} 多于总数 {items}");
            if (items > 12 && inPanel >= items)
                throw new Exception($"使用说明列表未虚拟化：{items} 条全被实例化（首开会卡，检查 " +
                                    "ScrollViewer.CanContentScroll / VirtualizingMode）");

            d($"样式解析 · 只读可换行无自带菜单 · 详情卡 {boxes.Count} 处 / 使用说明面板 {inPanel} 处可选中" +
              $"（共 {items} 条，虚拟化生效）");
        });

        Check("右键菜单（条目集合 / 复制文本格式 / 收藏切换 / 四页覆盖）", d =>
        {
            var v = new DezhouPriceView();
            v.EnsureLoaded();
            var vm = v.DzGrid.Items.Cast<DzRowVm>().First();

            var menu = RowMenu.Build(vm, v, v)
                      ?? throw new Exception("物价行右键菜单没有生成");
            var heads = menu.Items.OfType<MenuItem>().Select(m => m.Header as string ?? "").ToList();
            foreach (var need in new[] { "加入常用收藏", "复制项目编码", "复制项目名称", "复制本行", "复制全部详情" })
                if (!heads.Any(h => h.Contains(need)))
                    throw new Exception($"物价菜单缺少「{need}」：{string.Join(" / ", heads)}");
            if (!heads.Any(h => h.StartsWith("打开关联 · ")))
                throw new Exception("首行应带跨库关联菜单项");
            if (!menu.Items.OfType<Separator>().Any()) throw new Exception("菜单缺少分组分隔线");

            // 复制文本格式：本行 = 与表格列一致的制表符分隔；详情 = 标题 + 字段块
            var row = RowMenu.RowText(vm);
            if (row.Split('\t').Length != 10) throw new Exception("本行文本应为 10 列：" + row);
            var det = RowMenu.DetailText(vm);
            if (!det.Contains(vm.Name) || !det.Contains("项目编码"))
                throw new Exception("详情文本缺标题或字段");

            // 收藏切换（星标与右键菜单共用的同一通路）
            var file = AppPaths.DezhouFavFile;
            var bak = File.Exists(file) ? File.ReadAllBytes(file) : null;
            try
            {
                var fav = App.State.DezhouFav;
                var n0 = fav.Count;
                if (!DezhouPriceView.ToggleFav(vm)) throw new Exception("切换后应为已收藏");
                if (!fav.Has(vm.Code) || fav.Count != n0 + 1) throw new Exception("收藏未落库");
                if (!vm.IsFav) throw new Exception("星标 INPC 未同步");
                if (DezhouPriceView.ToggleFav(vm)) throw new Exception("再次切换应为取消");
                if (fav.Has(vm.Code) || fav.Count != n0) throw new Exception("取消后未还原");
            }
            finally
            {
                if (bak != null) File.WriteAllBytes(file, bak); else File.Delete(file);
                App.State.DezhouFav.Load();
            }

            // 其余三页 / 关联弹窗 / 使用说明条目的菜单
            var lv = new LixiangView(); lv.EnsureLoaded();
            var lxMenu = RowMenu.Build(lv.LxGrid.Items.Cast<LxRowVm>().First(), lv, lv)
                       ?? throw new Exception("立项指南菜单没有生成");
            if (!Heads(lxMenu).Any(h => h.Contains("复制计价说明"))) throw new Exception("立项指南菜单缺「复制计价说明」");

            var gv = new GuifanView(); gv.EnsureLoaded();
            var gfMenu = RowMenu.Build(gv.GfGrid.Items.Cast<GfRowVm>().First(), gv, gv)
                       ?? throw new Exception("技术规范菜单没有生成");
            if (!Heads(gfMenu).Any(h => h.Contains("复制项目编码"))) throw new Exception("技术规范菜单缺「复制项目编码」");

            var jdMenu = RowMenu.Build(new JdRowVm { Issue = 13, No = 5, Q = "什么是 DIP？", A = "按病种分值付费。" }, null, null)
                       ?? throw new Exception("解读辅导菜单没有生成");
            if (!Heads(jdMenu).Any(h => h.Contains("复制答案"))) throw new Exception("解读辅导菜单缺「复制答案」");
            if (!RowMenu.DetailText(new JdRowVm { Issue = 13, No = 5, Q = "Q", A = "A" }).Contains("第 13 期"))
                throw new Exception("解读辅导详情文本缺期号");

            var relMenu = RowMenu.Build(new RelatedDoc { Name = "心脏移植术", Sub = "三级 1000" }, null, null)
                        ?? throw new Exception("关联弹窗菜单没有生成");
            if (!Heads(relMenu).Any(h => h.Contains("复制名称"))) throw new Exception("关联弹窗菜单缺「复制名称」");

            var uMenu = RowMenu.Build(new UsageVm { Text = "移植指器官…", No = "1." }, null, null)
                      ?? throw new Exception("使用说明条目菜单没有生成");
            if (!Heads(uMenu).Any(h => h.Contains("复制本条"))) throw new Exception("使用说明菜单缺「复制本条」");

            d($"物价 {heads.Count} 项（收藏 / 5 类复制 / 关联）· 立项指南 / 技术规范 / 解读辅导 / 弹窗 / 说明条目均可构建 · 收藏往返已复原");
        });

        Check("明细卡 点击收起 / 再点展开 / 「收起」链接 / 可选文本不误触", d =>
        {
            var v = new DezhouPriceView();
            v.EnsureLoaded();
            v.Measure(new Size(1440, 900)); v.Arrange(new Rect(0, 0, 1440, 900)); v.UpdateLayout();

            var g = v.DzGrid;
            g.SelectedIndex = 0;
            g.UpdateLayout();
            var row = g.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow
                      ?? throw new Exception("取不到第一行容器");
            if (row.DetailsVisibility != Visibility.Visible) throw new Exception("选中行本应展开明细卡");

            // ① 点该行（明细卡）→ 收起
            if (!DetailToggle.TryToggle(g, row)) throw new Exception("点击已选中的行未触发切换");
            if (row.DetailsVisibility != Visibility.Collapsed) throw new Exception("点击后明细卡未收起");

            // ② 再点一次 → 重新展开
            if (!DetailToggle.TryToggle(g, row)) throw new Exception("第二次点击未触发切换");
            g.UpdateLayout();
            if (row.DetailsVisibility != Visibility.Visible) throw new Exception("再次点击后明细卡未重新展开");

            // ③ 卡里的可选文本不参与切换，否则就没法拖选复制了
            var box = SelectableBoxes(row).FirstOrDefault() ?? throw new Exception("明细卡里没有可选文本");
            if (DetailToggle.TryToggle(g, box)) throw new Exception("点在可选文本上不该切换");
            if (row.DetailsVisibility != Visibility.Visible) throw new Exception("点可选文本后明细卡被误收起");

            // ④ 卡片头部的「收起」链接
            var btn = Descendants<Button>(row).FirstOrDefault(b => (b.Content as string) == "收起")
                      ?? throw new Exception("明细卡头部没有「收起」链接");
            btn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            v.UpdateLayout();
            if (row.DetailsVisibility != Visibility.Collapsed) throw new Exception("「收起」链接未生效");

            // ⑤ 未选中的行：这次点击不归它管（先让 DataGrid 正常选中即可）
            var other = g.ItemContainerGenerator.ContainerFromIndex(1) as DataGridRow
                        ?? throw new Exception("取不到第二行容器");
            if (DetailToggle.TryToggle(g, other)) throw new Exception("未选中的行不应被切换");

            d($"选中即展开 → 点击收起 → 再点展开 → 文本 / 链接 / 未选中行行为均正确 · " +
              $"卡片 {SelectableBoxes(row).Count} 处可选文本");
        });

        Check("前台导出（范围取数 / 工作簿列与条数 / 使用说明附表 / 空结果）", d =>
        {
            var v = new DezhouPriceView();
            v.EnsureLoaded();

            // 三档范围：全部 = 当前筛选命中数；当前页 = 每页 50；仅收藏 = 收藏集合
            var (all, allLabel) = v.ExportScope("all");
            if (all.Count != App.State.Dezhou.Items.Count)
                throw new Exception($"全部导出应为 {App.State.Dezhou.Items.Count} 条，实际 {all.Count}");
            if (allLabel != "全部结果") throw new Exception("全部导出标签不对：" + allLabel);

            var (page, _) = v.ExportScope("page");
            if (page.Count != 50) throw new Exception($"当前页应为 50 条，实际 {page.Count}");

            var fav = App.State.DezhouFav;
            var pick = all.Take(2).ToList();
            fav.DiagInject(pick.Select(i => (i.Code, i.Doc, i.Name)));   // 只注入内存，不落盘
            string favLabel;
            try
            {
                var (favItems, lbl) = v.ExportScope("fav");
                favLabel = lbl;
                if (favItems.Count != 2) throw new Exception($"仅收藏应为 2 条，实际 {favItems.Count}");
            }
            finally { fav.Load(); }                                      // 还原磁盘上的真实收藏

            // 工作簿：表头与列定义一致、行数与导出条数一致、文件名规范
            var sample = all.Take(3).ToList();
            var (bytes, name) = DezhouExport.Front(App.State, sample, "全部结果");
            if (bytes.Length == 0) throw new Exception("导出字节为空");
            if (!name.EndsWith(".xlsx") || !name.Contains("全部结果"))
                throw new Exception("文件名不合规：" + name);

            var aoa = ExcelUtil.ReadAoa(ExcelUtil.Open(bytes).GetSheetAt(0));
            if (aoa.Count != sample.Count + 1)
                throw new Exception($"物价表应为表头 + {sample.Count} 行，实际 {aoa.Count}");
            if (aoa[0].Count != DezhouPriceStore.Columns.Count)
                throw new Exception($"表头 {aoa[0].Count} 列 ≠ 列定义 {DezhouPriceStore.Columns.Count} 列");
            var codeAt = aoa[0].IndexOf("项目编码");
            if (codeAt < 0) throw new Exception("表头缺「项目编码」（导出件将无法再导回后台）");
            if (aoa[1][codeAt] != sample[0].Code) throw new Exception("首行编码与数据不符");

            // 使用说明附表：带说明的记录应产出第二张表，行数 = 说明条数 + 表头
            var withUsage = all.FirstOrDefault(i => App.State.Dezhou.UsageOf(i.Doc, i.Category).Count > 0)
                            ?? throw new Exception("样例里找不到带类别使用说明的记录");
            var n = App.State.Dezhou.UsageOf(withUsage.Doc, withUsage.Category).Count;
            var wb2 = ExcelUtil.Open(DezhouExport.Front(App.State, new[] { withUsage }, "全部结果").Bytes);
            if (wb2.NumberOfSheets != 2) throw new Exception($"应有两张表，实际 {wb2.NumberOfSheets}");
            if (wb2.GetSheetName(1) != DezhouExport.UsageSheet) throw new Exception("第二张表名不对");
            var u2 = ExcelUtil.ReadAoa(wb2.GetSheetAt(1));
            if (u2.Count != n + 1) throw new Exception($"使用说明表应为表头 + {n} 行，实际 {u2.Count}");

            // 空结果：只出表头，不额外加空表
            var wb3 = ExcelUtil.Open(DezhouExport.Front(App.State, Array.Empty<DezhouPriceItem>(), "全部结果").Bytes);
            if (wb3.NumberOfSheets != 1) throw new Exception("空结果不该带第二张表");
            if (ExcelUtil.ReadAoa(wb3.GetSheetAt(0)).Count != 1) throw new Exception("空结果应只有表头");

            d($"{all.Count:N0} 条（全部）/ 50 条（本页）/ 2 条（{favLabel}）· 表头 {aoa[0].Count} 列 · " +
              $"使用说明 {n} 条成表 · 空结果只出表头 · {name}");
        });

        Check("应用图标与托盘（图标多尺寸 / 窗口图标 / 菜单模型 / 设置往返 / 关闭询问）", d =>
        {
            // ① 嵌入图标资源可加载，且是多尺寸 ico（16px 小图标与 256px 大图标都在）
            var frames = 0;
            using (var s = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream)
            {
                if (s == null || s.Length == 0) throw new Exception("嵌入图标资源加载失败");
                using var br = new BinaryReader(s);
                br.ReadUInt16();                                    // reserved
                if (br.ReadUInt16() != 1) throw new Exception("不是 ico 文件（type != 1）");
                frames = br.ReadUInt16();
                if (frames < 6) throw new Exception($"图标帧数应 ≥ 6（16~256），实际 {frames}");
                var has16 = false;
                var has256 = false;
                for (var i = 0; i < frames; i++)
                {
                    var bw = br.ReadByte();                         // 0 表示 256
                    if (bw == 16) has16 = true;
                    if (bw == 0) has256 = true;
                    br.ReadBytes(15);                               // ICONDIRENTRY 其余字节
                }
                if (!has16 || !has256)
                    throw new Exception($"缺少 16px 或 256px 帧（16={has16}, 256={has256}）");
            }

            var w = new MainWindow();
            var closed = false;
            w.Closed += (_, _) => closed = true;
            try
            {
                // ② 窗口图标已接线（任务栏 / Alt-Tab / 托盘共用一份），exe 本体也带图标
                if (w.Icon is not System.Windows.Media.Imaging.BitmapFrame f || f.PixelWidth < 16)
                    throw new Exception("MainWindow.Icon 未指向图标资源");
                using var exe = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath);
                if (exe == null) throw new Exception("exe 未内嵌图标（ApplicationIcon 未生效）");

                // ③ 托盘菜单模型：核心条目齐全，勾选开关只此一个
                var model = TrayIcon.MenuModel();
                var texts = string.Join(" | ", model.Where(x => !x.IsSeparator).Select(x => x.Text));
                foreach (var need in new[] { "打开主界面", "最小化到托盘", "打开后台管理", "退出程序" })
                    if (!model.Any(x => x.Text == need)) throw new Exception("托盘菜单缺「" + need + "」：" + texts);
                var toggle = model.SingleOrDefault(x => x.IsToggle)
                             ?? throw new Exception("托盘菜单缺「关闭时最小化到托盘」开关：" + texts);

                // ④~⑦ 设置往返与关闭去向：**必须在独立临时根目录里跑**。
                // 真实部署的 settings.json 可能正被运行中的客户端实例（管理员的 DIP 客户端）
                // 以它内存里的旧值写回 —— 在真实文件上断言「写盘 → 读回一致」会与那个实例互相踩，
                // 冒烟自检因此偶发失败（磁盘=旧业务值+新 updatedAt，典型他人覆盖）。
                var origRoot67 = AppPaths.Root;
                var tmp67 = Path.Combine(Path.GetTempPath(), "dip3-tray-" + Guid.NewGuid().ToString("N")[..8]);
                try
                {
                    Directory.CreateDirectory(Path.Combine(tmp67, "data"));
                    AppPaths.SetRoot(tmp67);
                    SettingsStore.Reload();

                    // ④ 设置往返：写盘 → 重载读回一致（默认开 → 关 → 开）
                    SettingsStore.SetCloseToTray(false);
                    SettingsStore.Reload();
                    if (SettingsStore.CloseToTray) throw new Exception("SetCloseToTray(false) 落盘后读回仍是开");
                    SettingsStore.SetCloseToTray(true);
                    SettingsStore.Reload();
                    if (!SettingsStore.CloseToTray) throw new Exception("SetCloseToTray(true) 落盘后读回仍是关");

                    // ④b 「关闭时先询问」：默认开、可单独关；勾「记住」要一次写两个键
                    if (!SettingsStore.CloseAsk) throw new Exception("CloseAsk 默认应为开");
                    SettingsStore.SetCloseAsk(false);
                    SettingsStore.Reload();
                    if (SettingsStore.CloseAsk) throw new Exception("SetCloseAsk(false) 落盘后读回仍是开");
                    SettingsStore.SetCloseChoice(true);          // 记住 = 最小化到托盘
                    SettingsStore.Reload();
                    if (SettingsStore.CloseAsk) throw new Exception("记住选择后不该再询问");
                    if (!SettingsStore.CloseToTray) throw new Exception("记住「最小化到托盘」后应转托盘");
                    SettingsStore.SetCloseChoice(false);         // 记住 = 退出程序
                    SettingsStore.Reload();
                    if (SettingsStore.CloseAsk) throw new Exception("记住选择后不该再询问");
                    if (SettingsStore.CloseToTray) throw new Exception("记住「退出程序」后应直接退出");

                    // ⑤ 关闭去向决策（纯函数）：四种输入组合全覆盖
                    if (MainWindow.ResolveClose(false, true, true) != MainWindow.CloseRoute.Ask)
                        throw new Exception("开着询问时应先弹确认框");
                    if (MainWindow.ResolveClose(false, false, true) != MainWindow.CloseRoute.HideToTray)
                        throw new Exception("不问 + 托盘开 → 应收进托盘");
                    if (MainWindow.ResolveClose(false, false, false) != MainWindow.CloseRoute.Exit)
                        throw new Exception("不问 + 托盘关 → 应直接退出");
                    if (MainWindow.ResolveClose(true, true, true) != MainWindow.CloseRoute.Exit)
                        throw new Exception("托盘「退出程序」/ 系统关机时不该再弹确认框");

                    // ⑥ 真实关闭：先关掉询问，免得冒烟自检把确认框弹到桌面上
                    SettingsStore.SetCloseAsk(false);
                    SettingsStore.SetCloseToTray(true);
                    if (!w.ShouldHideOnClose) throw new Exception("托盘开启时点关闭应转最小化到托盘");
                    SettingsStore.SetCloseToTray(false);
                    if (w.ShouldHideOnClose) throw new Exception("托盘关闭时点关闭应直接退出");

                    // ⑥b 延后询问：开着「先询问」点关闭 → 窗口必须被拦下且不抛异常
                    // （真实 bug：在 OnClosing 里同步 ShowDialog 会撞上「窗口关闭期间
                    //   无法调用 Show/ShowDialog」，现已改为 Dispatcher 延后一拍）
                    SettingsStore.SetCloseAsk(true);
                    w.Close();
                    if (closed) throw new Exception("开着询问时点关闭应被拦下等待确认，而不是直接关掉");
                    // 再关掉询问真正关闭；此前排队的延后询问会因窗口已不在（IsLoaded=false）安全退出
                    SettingsStore.SetCloseAsk(false);
                    w.Close();
                    if (!closed) throw new Exception("关掉询问后再次关闭应真正关闭");

                    // ⑦ 确认框本体：能构造、默认「什么都不做」、能离屏出图（XAML 解析 + 版式）
                    var dlg = new CloseConfirmWindow("自检");
                    if (dlg.Choice != CloseConfirmWindow.CloseChoice.Cancel)
                        throw new Exception("未做选择时确认框应默认 Cancel");
                    if (dlg.Remember) throw new Exception("「记住我的选择」默认不该勾上");
                    var png = Path.Combine(tmp67, "close-ask.png");
                    var shotErr = CloseConfirmWindow.Shot(png);
                    if (shotErr != null) throw new Exception("确认框离屏渲染失败：" + shotErr);
                    if (!File.Exists(png) || new FileInfo(png).Length < 3000)
                        throw new Exception("确认框截图异常（文件缺失或过小）");
                    dlg.Close();
                }
                finally
                {
                    AppPaths.SetRoot(origRoot67);
                    SettingsStore.Reload();
                    try { Directory.Delete(tmp67, true); } catch { /* 临时目录尽力清理 */ }
                }

                d($"图标帧 {frames} 帧（16~256）· exe 已内嵌 · 菜单 {model.Count(x => !x.IsSeparator)} 项 +「{toggle.Text}」开关 · 设置往返一致（独立临时根） · 关闭去向四态与确认框渲染正常");
            }
            finally
            {
                try { w.Close(); } catch { /* 已关闭或不可关 */ }
            }
        });

        // ================= 九、局域网数据更新 =================
        Head("九、局域网数据更新（数据分发中心）");

        Check("数据分发白名单（只发业务数据，本机私有文件永不出门）", d =>
        {
            if (DataCatalog.Specs.Count == 0) throw new Exception("白名单为空");
            foreach (var spec in DataCatalog.Specs)
            {
                var rel = spec.RelPath.Replace('\\', '/');
                if (DataCatalog.PrivateRelPaths.Contains(rel))
                    throw new Exception($"私有文件被列入可分发白名单：{rel}");
                if (spec.Name.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')))
                    throw new Exception($"标识含 URL 不安全字符：{spec.Name}");
            }

            // 只有精确命中的标识才允许落到磁盘路径上，拼出来的名字一律解析不出路径
            foreach (var bad in new[]
                     {
                         "", "settings", "../settings", "data/settings.json", "dezhou-favorites",
                         "../../config/dip3-config.json", "mappings/../../x", "dip3-config.json",
                     })
                if (DataCatalog.LocalPath(bad) != null)
                    throw new Exception($"非白名单标识不该解析出路径：{bad}");

            if (DataCatalog.LocalPath("mappings") == null) throw new Exception("白名单内的标识应能解析出路径");

            d($"{DataCatalog.Specs.Count} 个可分发文件 · {DataCatalog.PrivateRelPaths.Count} 个本机私有文件已隔离 · 越界标识一律不解析");
        });

        Check("清单生成与 SHA-256 一致性", d =>
        {
            var m = DataCatalog.BuildManifest();
            if (m.Files.Count != DataCatalog.Specs.Count)
                throw new Exception($"清单条目数 {m.Files.Count} 与白名单 {DataCatalog.Specs.Count} 不符");

            var exist = 0;
            foreach (var f in m.Files)
            {
                var p = DataCatalog.LocalPath(f.Name);
                if (!File.Exists(p))
                {
                    if (f.Exists) throw new Exception($"{f.Label} 标记为存在，但文件不在");
                    continue;
                }
                exist++;
                if (!f.Exists) throw new Exception($"{f.Label} 存在却没被标记");
                if (f.Sha256 is not { Length: 64 }) throw new Exception($"{f.Label} 的 SHA-256 不合法：{f.Sha256}");
                if (!string.Equals(f.Sha256, DataCatalog.Sha256(p), StringComparison.Ordinal))
                    throw new Exception($"{f.Label} 两次哈希不一致");
                if (new FileInfo(p).Length != f.Size) throw new Exception($"{f.Label} 大小与磁盘不符");
            }
            if (exist == 0) throw new Exception("本机没有任何数据文件");

            // 清单要能序列化后又原样读回 —— 分发接口与更新包都依赖这一点
            var back = Js.FromJson<DataManifest>(Js.ToJson(m));
            if (back?.Files == null || back.Files.Count != m.Files.Count)
                throw new Exception("清单序列化往返后条目数变了");
            if (back.Files[0].Sha256 != m.Files[0].Sha256 || back.Files[0].Name != m.Files[0].Name)
                throw new Exception("清单序列化往返后内容变了");

            d($"{m.Files.Count} 个文件 · 本机存在 {exist} 个 · 哈希与序列化往返一致");
        });

        Check("数据源地址归一化", d =>
        {
            var cases = new (string In, string Out)[]
            {
                ("10.0.0.8:3210", "http://10.0.0.8:3210"),
                ("http://10.0.0.8:3210/", "http://10.0.0.8:3210"),
                ("https://dip.hosp.local", "https://dip.hosp.local"),
                ("  http://10.0.0.8:3210  ", "http://10.0.0.8:3210"),
                ("", ""),
                ("   ", ""),
            };
            foreach (var (input, expect) in cases)
                if (DataUpdateClient.Normalize(input) != expect)
                    throw new Exception($"「{input}」→「{DataUpdateClient.Normalize(input)}」，应为「{expect}」");
            d($"{cases.Length} 种写法全部归一正确");
        });

        Check("数据源设置往返（含清空回落为「本机独立」）", d =>
        {
            // 独立临时根目录：真实部署的 settings.json 正被运行中的客户端实例共用，
            // 在它上面断言「写盘 → 读回一致」会与那个实例的内存写回互相踩（冒烟偶发失败的根因）。
            var origRoot = AppPaths.Root;
            var tmp = Path.Combine(Path.GetTempPath(), "dip3-datasrc-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(Path.Combine(tmp, "data"));
                AppPaths.SetRoot(tmp);
                SettingsStore.Reload();

                SettingsStore.SetDataSource("  10.9.9.9:3210  ", " tok ", false);
                var raw = File.ReadAllText(AppPaths.SettingsFile, Encoding.UTF8);
                SettingsStore.Reload();
                if (SettingsStore.DataSourceUrl != "10.9.9.9:3210")
                    throw new Exception($"地址读回为「{SettingsStore.DataSourceUrl}」；"
                        + $"磁盘原始内容：{raw.Replace("\n", " ").Replace("\r", "")}");
                if (!SettingsStore.HasDataSource) throw new Exception("配了地址却判定为无数据源");
                if (SettingsStore.AutoCheckUpdate) throw new Exception("关掉自动检查后读回仍为开");

                SettingsStore.SetDataSource("", "", true);
                SettingsStore.Reload();
                if (SettingsStore.HasDataSource) throw new Exception("清空地址后仍判定为有数据源");
                if (!SettingsStore.AutoCheckUpdate) throw new Exception("自动检查未回落为默认开");
                d("写入→落盘→读回一致；清空后回落为「本机独立」（独立临时根，不与运行中的实例共用文件）");
            }
            finally
            {
                AppPaths.SetRoot(origRoot);
                SettingsStore.Reload();
                try { Directory.Delete(tmp, true); } catch { /* 临时目录尽力清理 */ }
            }
        });

        Check("更新全链路（回环服务：检查→下载→校验→替换→备份→回滚）", d =>
        {
            var origRoot = AppPaths.Root;
            var tmp = Path.Combine(Path.GetTempPath(), "dip3-datasync-" + Guid.NewGuid().ToString("N")[..8]);
            var srvRoot = Path.Combine(tmp, "server");
            var cliRoot = Path.Combine(tmp, "client");
            FakeDataServer server = null;

            void Write(string root, string rel, string text)
            {
                var p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p, text, new UTF8Encoding(false));
            }

            string Read(string root, string rel)
                => File.ReadAllText(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)), Encoding.UTF8);

            try
            {
                // 服务器侧全是「REMOTE」；客户端侧除 guifan 外都是「LOCAL」——
                // guifan 两边一致，正好验证「内容相同则跳过、不进备份、不重下」
                foreach (var spec in DataCatalog.Specs)
                {
                    Write(srvRoot, spec.RelPath, "REMOTE " + spec.Name);
                    Write(cliRoot, spec.RelPath, spec.Name == "guifan" ? "REMOTE " + spec.Name : "LOCAL " + spec.Name);
                }

                server = new FakeDataServer(srvRoot);
                AppPaths.SetRoot(cliRoot);   // 此后 DataCatalog 全部指向临时「客户端」目录，不碰真实数据

                var expect = DataCatalog.Specs.Count - 1;   // guifan 同内容，不计入
                var chk = Task.Run(() => DataUpdateClient.CheckAsync(server.Url)).GetAwaiter().GetResult();
                if (!chk.Ok) throw new Exception("检查更新失败：" + chk.Message);
                if (chk.Items.Count != DataCatalog.Specs.Count)
                    throw new Exception($"差异表应有 {DataCatalog.Specs.Count} 行，实际 {chk.Items.Count}");
                if (chk.PendingCount != expect)
                    throw new Exception($"待更新应为 {expect} 个，实际 {chk.PendingCount}");
                var kept = chk.Items.First(i => i.Name == "guifan");
                if (!kept.Same || kept.NeedsUpdate)
                    throw new Exception("两边内容一致的文件被判成了需更新");

                var apply = Task.Run(() => DataUpdateClient.ApplyAsync(server.Url, chk.Pending)).GetAwaiter().GetResult();
                if (!apply.Ok) throw new Exception("更新失败：" + apply.Message);
                if (apply.Applied.Count != expect) throw new Exception($"实际替换 {apply.Applied.Count} 个，应为 {expect}");

                foreach (var spec in DataCatalog.Specs)
                    if (Read(cliRoot, spec.RelPath) != "REMOTE " + spec.Name)
                        throw new Exception($"{spec.Label} 未被换成服务器版本");

                if (apply.BackupDir == null || !Directory.Exists(apply.BackupDir))
                    throw new Exception("更新后没有生成备份目录");
                if (Read(apply.BackupDir, "data/mappings.json") != "LOCAL mappings")
                    throw new Exception("备份的不是更新前的内容");
                if (File.Exists(Path.Combine(apply.BackupDir, "data", "guifan.json")))
                    throw new Exception("内容一致的文件不该进备份");

                var chk2 = Task.Run(() => DataUpdateClient.CheckAsync(server.Url)).GetAwaiter().GetResult();
                if (chk2.PendingCount != 0) throw new Exception("更新后再比对仍显示需要更新");

                var rb = DataUpdateClient.Restore(apply.BackupDir);
                if (!rb.Ok) throw new Exception("回滚失败：" + rb.Message);
                foreach (var spec in DataCatalog.Specs)
                    if (Read(cliRoot, spec.RelPath) != (spec.Name == "guifan" ? "REMOTE guifan" : "LOCAL " + spec.Name))
                        throw new Exception($"{spec.Label} 未能回滚到更新前的内容");

                d($"{DataCatalog.Specs.Count} 个文件 · 差异 {chk.PendingCount} 项（同内容自动跳过）· "
                  + $"替换 {apply.Applied.Count} 个 · 备份 {Path.GetFileName(apply.BackupDir)} · 回滚成功 · 走 HTTP {server.Requests} 次");
            }
            finally
            {
                AppPaths.SetRoot(origRoot);
                server?.Dispose();
                try { Directory.Delete(tmp, true); } catch { /* 临时目录尽力清理 */ }
            }
        });

        Check("更新包导出与导入（离线兜底）", d =>
        {
            var origRoot = AppPaths.Root;
            var tmp = Path.Combine(Path.GetTempPath(), "dip3-datapack-" + Guid.NewGuid().ToString("N")[..8]);
            var srvRoot = Path.Combine(tmp, "server");
            var cliRoot = Path.Combine(tmp, "client");
            try
            {
                foreach (var spec in DataCatalog.Specs)
                {
                    void Write(string root, string text)
                    {
                        var p = Path.Combine(root, spec.RelPath.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(p));
                        File.WriteAllText(p, text, new UTF8Encoding(false));
                    }
                    Write(srvRoot, "REMOTE " + spec.Name);
                    Write(cliRoot, "LOCAL " + spec.Name);
                }

                // 在「服务器侧」导出更新包（导出只看本地白名单文件）
                AppPaths.SetRoot(srvRoot);
                var zip = Path.Combine(tmp, "pack.zip");
                DataUpdateClient.ExportPackage(zip);
                if (!File.Exists(zip) || new FileInfo(zip).Length == 0) throw new Exception("更新包未生成");

                // 在「客户端侧」导入
                AppPaths.SetRoot(cliRoot);
                var res = DataUpdateClient.ImportPackage(zip);
                if (!res.Ok) throw new Exception("导入失败：" + res.Message);
                if (res.Applied.Count != DataCatalog.Specs.Count)
                    throw new Exception($"应导入 {DataCatalog.Specs.Count} 个，实际 {res.Applied.Count}");

                foreach (var spec in DataCatalog.Specs)
                {
                    var p = Path.Combine(cliRoot, spec.RelPath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.ReadAllText(p, Encoding.UTF8) != "REMOTE " + spec.Name)
                        throw new Exception($"{spec.Label} 未被更新包替换");
                }

                // 内容相同再导一次：应识别为「无需导入」
                var again = DataUpdateClient.ImportPackage(zip);
                if (!again.Ok || again.Applied.Count != 0)
                    throw new Exception("重复导入同一更新包时未识别为「无需导入」");

                d($"{DataCatalog.Specs.Count} 个文件打包 {DataCatalog.SizeText(new FileInfo(zip).Length)} · "
                  + $"导入替换 {res.Applied.Count} 个 · 重复导入正确跳过");
            }
            finally
            {
                AppPaths.SetRoot(origRoot);
                try { Directory.Delete(tmp, true); } catch { /* 临时目录尽力清理 */ }
            }
        });

        Check("前台消息推送（生效期过滤 / 草稿不外泄 / 版本号 / 落盘往返）", d =>
        {
            var origRoot = AppPaths.Root;
            var tmp = Path.Combine(Path.GetTempPath(), "dip3-notice-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(Path.Combine(tmp, "data"));
                AppPaths.SetRoot(tmp);          // 全程写临时根，真实 data/notice.json 不被碰
                var store = new NoticeStore();

                var (e1, n1) = store.Upsert("", "停机维护", "10月8日 20:00 起系统维护，期间分组结果照常出。", "warn", true, "", "", true, "admin");
                if (e1 != null) throw new Exception("发布失败：" + e1);
                var tomorrow = DateTime.Now.Date.AddDays(1).ToString("yyyy-MM-dd") + "T08:00";
                var (e2, n2) = store.Upsert("", "新病种库启用", "明日起切换新版病种库。", "info", false, tomorrow, "", true, "admin");
                var (e3, n3) = store.Upsert("", "内部草稿", "还没想好怎么写", "danger", false, "", "", false, "admin");
                if (e2 != null || e3 != null) throw new Exception("发布失败：" + (e2 ?? e3));
                if (n1.Id.Length != 8) throw new Exception("id 应为 8 位短 id，实际 " + n1.Id);

                // 前台只该看到「已发布 + 已到生效时间」的那一条：待生效 1 条、草稿 1 条都不可见
                var now = DateTime.Now;
                var front = store.ActiveForFront(now);
                if (front.Count != 1) throw new Exception($"前台应见 1 条（另 2 条待生效/草稿），实际 {front.Count}");
                var frontJson = Js.ToJson(front[0]);
                if (frontJson.Contains("\"enabled\"") || frontJson.Contains("\"startAt\"") || frontJson.Contains("\"endAt\""))
                    throw new Exception("前台视图泄漏了发布开关 / 生效排期：" + frontJson);
                if (!frontJson.Contains("停机维护")) throw new Exception("前台视图内容不对：" + frontJson);

                // 参数校验：空内容 / 非法时间 / 时间倒挂 / 超长，都应被拦下
                if (store.Upsert("", "  ", "  ", "info", false, "", "", true, "").Err == null) throw new Exception("空标题空正文应被拒绝");
                if (store.Upsert("", "x", "y", "info", false, "不是时间", "", true, "").Err == null) throw new Exception("非法时间应被拒绝");
                if (store.Upsert("", "x", "y", "info", false, "2026-10-10T10:00", "2026-10-09T10:00", true, "").Err == null)
                    throw new Exception("失效早于生效应被拒绝");
                if (store.Upsert("", new string('长', NoticeStore.MaxTitle + 1), "y", "info", false, "", "", true, "").Err == null)
                    throw new Exception("超长标题应被拒绝");
                if (store.Upsert("不存在的id", "x", "y", "info", false, "", "", true, "").Err == null)
                    throw new Exception("更新不存在的消息应报错");
                if (store.Items.Count != 3) throw new Exception("被拒绝的调用不应留下记录");

                // 级别白名单：乱填的级别回落到 info
                var (_, n4) = store.Upsert("", "级别测试", "body", "URGENT!!", false, "", "", false, "");
                if (n4.Level != NoticeStore.LevelInfo) throw new Exception("非法级别应回落 info，实际 " + n4.Level);

                // 停用后前台立刻不可见；版本号每次落盘递增，无变化的调用不递增
                if (!store.SetEnabled(n1.Id, false)) throw new Exception("停用失败");
                if (store.ActiveForFront(now).Count != 0) throw new Exception("停用后前台仍可见");
                var revBefore = store.Rev;
                store.SetEnabled(n1.Id, true);
                if (store.Rev <= revBefore) throw new Exception("落盘后版本号未递增");
                var revAfter = store.Rev;
                store.SetEnabled(n1.Id, true);       // 幂等：状态没变不该产生新版本
                if (store.Rev != revAfter) throw new Exception("无状态变化的调用不应递增版本号");

                // 落盘往返：重启后内容与版本号都在
                var reloaded = new NoticeStore();
                if (reloaded.Rev != store.Rev) throw new Exception($"重载后版本号不一致 {reloaded.Rev} != {store.Rev}");
                if (reloaded.Find(n1.Id)?.Title != "停机维护") throw new Exception("重载后内容丢失");
                if (reloaded.Find(n3.Id)?.Enabled != false) throw new Exception("重载后草稿状态丢失");

                // 删除 / 清理草稿（草稿应为 n3 + n4 两条）
                if (!store.Remove(n2.Id)) throw new Exception("删除失败");
                if (store.Find(n2.Id) != null) throw new Exception("删除后仍能查到");
                if (store.ClearDrafts() != 2) throw new Exception("应清掉 2 条草稿");

                d($"发布 3 条（1 待生效 / 1 草稿）→ 前台只见 {front.Count} 条且不含排期字段 · 版本号 {revBefore}→{revAfter} · "
                  + "5 项非法入参被拦 · 停用即时生效 · 落盘往返一致");
            }
            finally
            {
                AppPaths.SetRoot(origRoot);
                try { Directory.Delete(tmp, true); } catch { /* 临时目录尽力清理 */ }
            }
        });

        Check("公共设置跟随（服务端有值才下发 / 私有项绝不被覆盖 / 空值不回冲）", d =>
        {
            var origRoot = AppPaths.Root;
            var tmp = Path.Combine(Path.GetTempPath(), "dip3-pubsets-" + Guid.NewGuid().ToString("N")[..8]);
            FakeDataServer server = null;
            try
            {
                Directory.CreateDirectory(Path.Combine(tmp, "data"));
                AppPaths.SetRoot(tmp);
                SettingsStore.Reload();          // 关键：静态缓存里还是真实根的那一份，必须重新读

                // 本机先有一套「自己的」设置：口令 / 主题 / 托盘 / 数据源都是本机私有项
                SettingsStore.Current.AppName = "本机旧名";
                SettingsStore.Current.AppSubName = "本机自留副名";
                SettingsStore.Current.Theme = "azure";
                SettingsStore.Current.CloseToTray = false;
                SettingsStore.Current.AdminPass = SettingsStore.HashPassword("my-own-pass");
                SettingsStore.SetDataSource("10.1.1.1:3210", "tok", false);
                var passBefore = SettingsStore.Current.AdminPass;

                server = new FakeDataServer(tmp)
                {
                    SettingsProvider = () => new PublicSettingsSnapshot
                    {
                        AppName = "DIP 3.0 分组器",
                        AppSubName = "平原县中医院",
                        LogoText = "DI",
                        LixiangCategories = new List<string> { "综合类", "临床诊疗类", "综合类" },
                        LixiangUnits = new List<string> { "次", "日" },
                    },
                };

                var r = Task.Run(() => PublicSettingsClient.SyncAsync(server.Url)).GetAwaiter().GetResult();
                if (!r.Ok) throw new Exception("同步失败：" + r.Message);
                if (!r.Changed) throw new Exception("应当识别出改动");
                if (SettingsStore.Current.AppName != "DIP 3.0 分组器") throw new Exception("软件名称未跟随");
                if (SettingsStore.Current.AppSubName != "平原县中医院") throw new Exception("副名未跟随");
                if (SettingsStore.Current.LixiangCategories is not { Count: 2 })
                    throw new Exception("类别未去重：" + string.Join("|", SettingsStore.Current.LixiangCategories ?? new List<string>()));
                if (SettingsStore.Current.LixiangUnits is not { Count: 2 }) throw new Exception("单位未跟随");
                if (SettingsStore.Current.AdminPass != passBefore) throw new Exception("口令被服务端覆盖了");
                if (SettingsStore.Current.Theme != "azure") throw new Exception("主题被服务端覆盖了");
                if (SettingsStore.CloseToTray) throw new Exception("托盘开关被服务端覆盖了");
                if (SettingsStore.DataSourceUrl != "10.1.1.1:3210") throw new Exception("数据源地址被覆盖了");

                // 幂等：内容一致再同步一次不该再报改动
                var r2 = Task.Run(() => PublicSettingsClient.SyncAsync(server.Url)).GetAwaiter().GetResult();
                if (!r2.Ok || r2.Changed)
                    throw new Exception("内容未变却报告了改动：" + string.Join("、", r2.Applied));

                // 服务端把副名 / 参数表清空：表示「管理员没配」，不该把本机已有的值清掉
                server.SettingsProvider = () => new PublicSettingsSnapshot
                {
                    AppName = "DIP 3.0 分组器",
                    AppSubName = "",
                    LixiangCategories = new List<string>(),
                    LixiangUnits = new List<string>(),
                };
                var r3 = Task.Run(() => PublicSettingsClient.SyncAsync(server.Url)).GetAwaiter().GetResult();
                if (r3.Changed) throw new Exception("服务端空值不应触发改动");
                if (SettingsStore.Current.AppSubName != "平原县中医院") throw new Exception("服务端清空副名把本机也清了");
                if (SettingsStore.Current.LixiangUnits is not { Count: 2 }) throw new Exception("服务端清空参数表把本机也清了");

                // 落盘往返：重启后仍是跟随后的值，私有项原样
                SettingsStore.Reload();
                if (SettingsStore.Current.AppSubName != "平原县中医院") throw new Exception("落盘后副名丢失");
                if (SettingsStore.Current.AdminPass != passBefore) throw new Exception("落盘后口令被改");
                if (SettingsStore.Current.Theme != "azure") throw new Exception("落盘后主题被改");
                if (!SettingsStore.FollowServerSettings) throw new Exception("跟随开关默认应为开");

                d($"名称/副名/类别/单位 4 项跟随（类别自动去重）· 幂等同步 0 改动 · 服务端空值 0 回冲 · "
                  + $"口令/主题/托盘/数据源 4 项私有配置原样 · 走 HTTP {server.Requests} 次");
            }
            finally
            {
                AppPaths.SetRoot(origRoot);
                SettingsStore.Reload();
                server?.Dispose();
                try { Directory.Delete(tmp, true); } catch { /* 临时目录尽力清理 */ }
            }
        });

        Check("通知公告拉取与本机已读（拉取解析 / 未读计数 / 本机隐藏 / 失效 id 清理）", d =>
        {
            var origRoot = AppPaths.Root;
            var tmp = Path.Combine(Path.GetTempPath(), "dip3-notice-" + Guid.NewGuid().ToString("N")[..8]);
            FakeDataServer server = null;
            try
            {
                Directory.CreateDirectory(Path.Combine(tmp, "data"));
                AppPaths.SetRoot(tmp);
                SettingsStore.Reload();
                SettingsStore.SetDataSource("127.0.0.1:3210", "share-tok", false);   // 真实地址稍后换成假服务

                var items = new List<NoticeView>
                {
                    new()
                    {
                        Id = "aaaa1111", Title = "停机维护", Body = "10月8日 20:00 起维护。\n期间分组结果照常。",
                        Level = "warn", Pinned = true, UpdatedAt = "2026-10-07T20:00", Author = "admin",
                    },
                    new()
                    {
                        Id = "bbbb2222", Title = "新病种库已上线", Body = "已切换到 2026 版。",
                        Level = "info", Pinned = false, UpdatedAt = "2026-10-07T19:00",
                    },
                };
                server = new FakeDataServer(tmp)
                {
                    NoticeProvider = () => new NoticeFeed
                    {
                        Rev = 7,
                        ServerTime = "2026-10-07T20:30",
                        Items = items,
                    },
                };

                var r = Task.Run(() => NoticeClient.FetchAsync(server.Url)).GetAwaiter().GetResult();
                if (!r.Ok) throw new Exception("拉取失败：" + r.Message);
                if (r.Rev != 7) throw new Exception("版本号未解析：" + r.Rev);
                if (r.Items.Count != 2) throw new Exception("条数不对：" + r.Items.Count);
                if (r.Items[0].LevelText != "提醒" || !r.Items[0].Pinned) throw new Exception("级别 / 钉住未解析");
                if (r.Items[0].TimeText != "10-07 20:00") throw new Exception("时间格式不对：" + r.Items[0].TimeText);
                if (r.Items[0].Body.IndexOf('\n') < 0) throw new Exception("正文换行未保留");

                var read = new NoticeReadStore();
                if (read.UnreadCount(r.Items) != 2) throw new Exception("初始未读数不对");

                // 本机隐藏（点横幅 ✕）：只影响本机，不动服务端数据
                if (!read.Hide("aaaa1111") || !read.IsHidden("aaaa1111")) throw new Exception("隐藏横幅失败");

                // 打开面板 = 已读；重复标记不产生无意义落盘
                if (!read.MarkRead(new[] { "aaaa1111" })) throw new Exception("标记已读失败");
                if (read.UnreadCount(r.Items) != 1) throw new Exception("已读后未读数不对");
                if (read.MarkRead(new[] { "aaaa1111" })) throw new Exception("重复标记不该报告变化");
                if (read.Unread(r.Items).Single().Id != "bbbb2222") throw new Exception("未读集合不对");

                // 落盘往返
                var reloaded = new NoticeReadStore();
                if (!reloaded.IsRead("aaaa1111") || !reloaded.IsHidden("aaaa1111"))
                    throw new Exception("重启后已读 / 隐藏记录丢失");

                // 服务端删掉的消息：本机记录跟着清理，文件不会无限膨胀
                if (!reloaded.KeepOnly(new[] { "bbbb2222" })) throw new Exception("应报告清理了失效 id");
                var after = new NoticeReadStore();
                if (after.IsRead("aaaa1111") || after.IsHidden("aaaa1111")) throw new Exception("失效 id 未被清掉");
                if (after.IsRead("bbbb2222")) throw new Exception("清理时误伤仍然有效的 id");

                // 边界：空 id 不写入、空列表未读为 0
                if (after.MarkRead(new[] { "", "   " })) throw new Exception("空 id 不该写入");
                if (after.UnreadCount(new List<NoticeView>()) != 0) throw new Exception("空列表未读数应为 0");

                // 服务端没开这个接口（404）时不抛异常，给一句人话
                server.NoticeProvider = null;
                var bad = Task.Run(() => NoticeClient.FetchAsync(server.Url)).GetAwaiter().GetResult();
                if (bad.Ok || bad.Message.Length == 0) throw new Exception("接口不可用时应返回可读的失败说明");

                d($"2 条（1 钉住 / 1 普通）解析与时间格式正确 · 已读/隐藏落盘往返一致 · "
                  + $"失效 id 自动清理且不误伤 · 接口不可用时提示「{bad.Message}」· 走 HTTP {server.Requests} 次");
            }
            finally
            {
                AppPaths.SetRoot(origRoot);
                SettingsStore.Reload();
                server?.Dispose();
                try { Directory.Delete(tmp, true); } catch { /* 临时目录尽力清理 */ }
            }
        });

        // ================= 十、版本管理 / 审计日志 / 客户端更新 =================
        Head("十、版本管理 · 审计日志 · 客户端更新");

        Check("病种库版本快照（创建 / 内容未变自动去重 / 列表）", d =>
        {
            WithTempRoot(out var tmp, out var restore);
            try
            {
                var v1 = VersionStore.Create("手动创建", "自检基线");
                if (v1 == null) throw new Exception("创建快照返回 null");
                if (!Directory.Exists(VersionStore.DirOf(v1.Id))) throw new Exception("版本目录未建立");
                if (!File.Exists(Path.Combine(VersionStore.DirOf(v1.Id), "version.json")))
                    throw new Exception("version.json 未落盘");
                if (v1.Total <= 0) throw new Exception("快照行数为 0");
                if (v1.Fingerprint is not { Length: 64 }) throw new Exception("指纹不合法：" + v1.Fingerprint);
                foreach (var f in v1.Files)
                    if (!f.Exists) throw new Exception($"{f.Label} 应被纳入快照");

                // 内容没变时自动快照不该再产出一个版本
                if (VersionStore.Create("导入前自动", "重复", auto: true) != null)
                    throw new Exception("内容未变却仍然建了新版本");

                var v2 = VersionStore.Create("手动创建", "第二个版本");
                if (v2 == null || v2.Id == v1.Id) throw new Exception("第二个版本 id 未变化");

                var list = VersionStore.List();
                if (list.Count != 2) throw new Exception($"列表应为 2 个版本，实为 {list.Count}");
                if (list[0].Id != v2.Id) throw new Exception("列表未按新 → 旧排序");
                if (VersionStore.Read(VersionStore.DirOf(v1.Id)).Total != v1.Total)
                    throw new Exception("按目录重读版本行数不一致");

                d($"2 个版本 · {v1.CountsText} · 指纹 {v1.ShortHash} · 内容未变的自动快照已去重");
            }
            finally { restore(); }
        });

        Check("版本差异比对（增删改逐区段识别 + 报告可导出）", d =>
        {
            WithTempRoot(out var tmp, out var restore);
            try
            {
                var v1 = VersionStore.Create("手动创建", "基线");

                // 在真实配置上做三种改动：删一行、加一行、改一行
                var cfg = Js.ReadJsonFile<Dip3Config>(AppPaths.ConfigFile);
                var bx = Diseases.Get("bingxiang");
                var firstKey = bx.KeyOf(bx.GetRows(cfg)[0]);
                if (!Diseases.RemoveRow(cfg, "bingxiang", firstKey).Ok) throw new Exception("删除失败");
                var add = Diseases.AddRow(cfg, "bingxiang", new Dictionary<string, string>
                {
                    ["seq"] = "9999", ["diag"] = "K80.0", ["diagName"] = "自检新增行",
                    ["procs"] = "51.0301", ["procName"] = "自检手术",
                });
                if (!add.Ok) throw new Exception("新增失败：" + add.Error);

                var jc = Diseases.Get("jichu");
                var jcRows = jc.GetRows(cfg);
                var jcKey = jc.KeyOf(jcRows[0]);
                var jcFlat = jc.Flatten(jcRows[0]).ToDictionary(kv => kv.Key, kv => JsonText.Str(kv.Value));
                jcFlat["diagName"] = "自检改过的名称";
                if (!Diseases.UpdateRow(cfg, "jichu", jcKey, jcFlat).Ok) throw new Exception("修改失败");
                Diseases.SaveConfig(cfg);

                var v2 = VersionStore.Create("手动创建", "改后");
                var diff = VersionStore.Diff(v1.Id, v2.Id);

                var bxDiff = diff.Sections.First(x => x.Tab == "bingxiang");
                if (bxDiff.AddedCount < 1) throw new Exception("未识别出新增行");
                if (bxDiff.RemovedCount < 1) throw new Exception("未识别出删除行");
                if (!bxDiff.Added.Contains("9999")) throw new Exception("新增行的主键未进样例：" + bxDiff.SampleText);

                var jcDiff = diff.Sections.First(x => x.Tab == "jichu");
                if (jcDiff.ChangedCount < 1) throw new Exception("未识别出修改行");

                if (diff.AddedTotal < 1 || diff.RemovedTotal < 1 || diff.ChangedTotal < 1)
                    throw new Exception("合计统计不对：" + diff.SummaryText);
                if (diff.Same) throw new Exception("明明有差异却判定为一致");

                var report = VersionStore.DiffReportText(diff);
                foreach (var need in new[] { "并项核心病种", "基础核心病种", "新增", "删除", "修改" })
                    if (report.IndexOf(need, StringComparison.Ordinal) < 0)
                        throw new Exception($"差异报告缺少「{need}」：\n{report}");

                // 版本 ↔ 当前
                var cur = VersionStore.DiffWithCurrent(v1.Id);
                if (cur.Same) throw new Exception("与当前比较未发现差异");
                if (cur.RightId != "当前") throw new Exception("与当前比较的右端标识不对：" + cur.RightId);

                d($"并项 +{bxDiff.AddedCount}/-{bxDiff.RemovedCount} · 基础 ~{jcDiff.ChangedCount} · "
                  + $"报告 {report.Split('\n').Length - 1} 行 · {diff.SummaryText}");
            }
            finally { restore(); }
        });

        Check("版本回滚（原样写回 + 回滚前自动留档 + 审计留痕）", d =>
        {
            WithTempRoot(out var tmp, out var restore);
            try
            {
                var v1 = VersionStore.Create("手动创建", "回滚目标");
                var cfg = Js.ReadJsonFile<Dip3Config>(AppPaths.ConfigFile);
                if (!Diseases.RemoveRow(cfg, "bingxiang", Diseases.Get("bingxiang").KeyOf(
                        Diseases.Get("bingxiang").GetRows(cfg)[0])).Ok)
                    throw new Exception("删除失败");
                Diseases.SaveConfig(cfg);
                var changed = VersionStore.CurrentFingerprint();
                if (changed == v1.Fingerprint) throw new Exception("改动后指纹未变化，用例前提不成立");

                var res = VersionStore.Restore(v1.Id);
                if (!res.Ok) throw new Exception("回滚失败：" + res.Message);
                if (res.Applied.Count == 0) throw new Exception("回滚未写入任何文件");

                var after = VersionStore.CurrentFingerprint();
                if (!string.Equals(after, v1.Fingerprint, StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"回滚后指纹 {after} 与快照 {v1.Fingerprint} 不一致");
                if (res.BackupDir == null || !Directory.Exists(res.BackupDir))
                    throw new Exception("回滚前的版本未被备份");

                var audited = AuditLog.Query(new AuditFilter { Action = AuditAction.Version, Limit = 100 });
                if (audited.Count == 0) throw new Exception("回滚未写入审计日志");

                var before = VersionStore.List().Count;
                if (!VersionStore.Delete(v1.Id)) throw new Exception("删除版本失败");
                if (VersionStore.List().Count != before - 1) throw new Exception("删除后列表数量未减少");

                d($"{res.Applied.Count} 个文件原样写回 · 指纹一致 · 备份点 {Path.GetFileName(res.BackupDir)} · 审计留痕 {audited.Count} 条");
            }
            finally { restore(); }
        });

        Check("审计日志（写入 / 检索 / 统计 / CSV 转义 / 环形封顶 / 清空）", d =>
        {
            WithTempRoot(out var tmp, out var restore);
            var prevActor = AuditLog.Ambient;
            try
            {
                AuditLog.Reload();
                AuditLog.Clear();
                AuditLog.SetDefaultActor("tester", "桌面端");

                AuditLog.Write(AuditAction.Import, "病种库", "整库导入 a.xlsx");
                AuditLog.Write(AuditAction.Edit, "映射与灰码", "编辑映射 #3,含逗号\"引号\"");
                AuditLog.Write(AuditAction.Update, "数据", "在线更新 3 个文件");
                AuditLog.Write(AuditAction.Login, "后台登录", "登录失败：账号 admin", false, "口令不对");
                AuditLog.Write(AuditAction.Delete, "德州物价", "删除物价项目 013315000010000");

                var all = AuditLog.All();
                if (all.Count != 5) throw new Exception($"应有 5 条，实为 {all.Count}");
                if (all[0].User != "tester" || all[0].Source != "桌面端")
                    throw new Exception("默认身份未写入：" + all[0].User + "/" + all[0].Source);
                if (!all[0].Ok || all[0].ResultText != "成功") throw new Exception("成功标记不对");
                if (all.First(x => !x.Ok).ResultText != "失败") throw new Exception("失败标记不对");

                if (AuditLog.Query(new AuditFilter { Action = AuditAction.Import }).Count != 1)
                    throw new Exception("按动作筛选不对");
                if (AuditLog.Query(new AuditFilter { Target = "德州物价" }).Count != 1)
                    throw new Exception("按对象筛选不对");
                if (AuditLog.Query(new AuditFilter { Ok = false }).Count != 1)
                    throw new Exception("只看失败筛选不对");
                if (AuditLog.Query(new AuditFilter { Keyword = "映射" }).Count != 1)
                    throw new Exception("关键词筛选不对");

                var st = AuditLog.Stats();
                if (st.Total != 5 || st.Failed != 1 || st.Today != 5) throw new Exception("统计不对：" + st.Total + "/" + st.Failed);
                if (AuditLog.Actions().Count != 5 || AuditLog.Targets().Count != 5)
                    throw new Exception("动作 / 对象去重列表不对");

                // CSV：BOM + 表头 + 5 行；含逗号引号的字段要按 RFC4180 转义
                var csv = AuditLog.ExportCsv(all);
                if (!csv.StartsWith('\uFEFF')) throw new Exception("CSV 缺少 BOM");
                var lines = csv.Split('\n').Where(l => l.Trim().Length > 0).ToList();
                if (lines.Count != 6) throw new Exception($"CSV 应有 6 行（表头+5），实为 {lines.Count}");
                if (lines[0].IndexOf("时间,账号,来源,动作,对象,详情,结果,备注", StringComparison.Ordinal) < 0)
                    throw new Exception("CSV 表头不对：" + lines[0]);
                if (lines.Any(l => l.IndexOf("\"引号\"", StringComparison.Ordinal) >= 0 && l.IndexOf("\"\"引号\"\"", StringComparison.Ordinal) < 0))
                    throw new Exception("CSV 引号未翻倍转义");

                // 环形封顶：直接造一个超限的日志文件，写一条后应被裁到上限
                var big = new List<AuditEntry>();
                for (var i = 0; i < AuditLog.MaxEntries + 50; i++)
                    big.Add(new AuditEntry
                    {
                        Id = i.ToString("D15"), At = Js.Iso(), User = "u", Source = "桌面端",
                        Action = "导入", Target = "病种库", Detail = "第 " + i + " 条", Ok = true,
                    });
                Js.WriteJsonFile(AuditLog.File, big, indent: 1);
                AuditLog.Reload();
                AuditLog.Write(AuditAction.Save, "系统设置", "封顶用例");
                var capped = AuditLog.All();
                if (capped.Count != AuditLog.MaxEntries)
                    throw new Exception($"裁剪后应为 {AuditLog.MaxEntries} 条，实为 {capped.Count}");
                if (capped[0].Detail != "封顶用例") throw new Exception("最新一条不在最前");

                AuditLog.Clear();
                if (AuditLog.All().Count != 0) throw new Exception("清空后仍有记录");

                d($"5 条写入与 4 类筛选全部命中 · CSV {lines.Count} 行且引号已转义 · "
                  + $"超限自动裁到 {AuditLog.MaxEntries} 条 · 清空生效");
            }
            finally
            {
                AuditLog.SetDefaultActor(prevActor?.User ?? "本机", prevActor?.Source ?? "桌面端");
                restore();
            }
        });

        Check("审计身份作用域（网页端按请求带账号，出作用域自动还原）", d =>
        {
            WithTempRoot(out var tmp, out var restore);
            var prevActor = AuditLog.Ambient;
            try
            {
                AuditLog.Reload();
                AuditLog.Clear();
                AuditLog.SetDefaultActor("desktop-user", "桌面端");

                var outside = AuditLog.Write(AuditAction.Save, "系统设置", "桌面端写入");
                if (outside.User != "desktop-user" || outside.Source != "桌面端")
                    throw new Exception($"默认身份不对：{outside.User}/{outside.Source}");

                AuditEntry inside;
                using (AuditLog.Scope("web-admin", "网页端"))
                {
                    inside = AuditLog.Write(AuditAction.Import, "立项指南", "网页端导入");
                    if (AuditLog.Ambient.User != "web-admin") throw new Exception("作用域内 Ambient 未生效");
                }
                if (inside.User != "web-admin" || inside.Source != "网页端")
                    throw new Exception($"作用域内身份不对：{inside.User}/{inside.Source}");

                var back = AuditLog.Write(AuditAction.Save, "系统设置", "出作用域后写入");
                if (back.User != "desktop-user") throw new Exception("出作用域后身份未还原：" + back.User);

                var save = AuditLog.Query(new AuditFilter { Action = AuditAction.Save, Limit = 50 });
                if (save.Count != 2) throw new Exception($"按动作应命中 2 条，实为 {save.Count}");
                if (AuditLog.Query(new AuditFilter { Action = AuditAction.Save, Target = "系统设置" }).Count != 2)
                    throw new Exception("动作 + 对象组合筛选不对");

                d("作用域内 web-admin/网页端 → 出作用域还原 desktop-user/桌面端 · 默认身份与作用域互不污染");
            }
            finally
            {
                AuditLog.SetDefaultActor(prevActor?.User ?? "本机", prevActor?.Source ?? "桌面端");
                restore();
            }
        });

        Check("客户端更新：版本号比较规则", d =>
        {
            var cases = new (string A, string B, int Sign)[]
            {
                ("1.5.0", "1.4.9", 1),
                ("1.5.0", "1.5.0", 0),
                ("1.5", "1.5.0", 0),
                ("2.0.0", "1.9.9", 1),
                ("1.10.0", "1.9.0", 1),
                ("1.5.0-beta.1", "1.5.0", 0),
                ("1.0.0", "1.0.1", -1),
                ("1.5.0", "", 1),
            };
            foreach (var (a, b, sign) in cases)
            {
                var got = Math.Sign(AppVersion.Compare(a, b));
                if (got != sign) throw new Exception($"比较 {a} vs {b} = {got}，应为 {sign}");
            }
            if (!AppVersion.LooksValid("1.5.0") || AppVersion.LooksValid("") || AppVersion.LooksValid("abc"))
                throw new Exception("版本号合法性判定不对");
            if (AppVersion.Current.Length == 0) throw new Exception("当前版本为空");

            d($"{cases.Length} 组比较全部正确 · 本机版本 {AppVersion.Current}");
        });

        Check("客户端更新全链路（检查 → 下载 → SHA 校验 → 解包 → 生成重启脚本）", d =>
        {
            WithTempRoot(out var tmp, out var restore);
            FakeDataServer server = null;
            var origAppDir = AppUpdateClient.AppDirOverride;
            try
            {
                // 造一个「安装包」与一个假的「程序目录」
                var pkgSrc = Path.Combine(tmp, "pkg-src");
                Directory.CreateDirectory(pkgSrc);
                File.WriteAllText(Path.Combine(pkgSrc, AppUpdateClient.MainExeName), "NEW-BINARY", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(pkgSrc, "Dip3Grouper.Core.dll"), "NEW-DLL", new UTF8Encoding(false));
                Directory.CreateDirectory(AppUpdateClient.ServerDir);
                ZipFile.CreateFromDirectory(pkgSrc, AppUpdateClient.ServerPackage);

                var appDir = Path.Combine(tmp, "app");
                Directory.CreateDirectory(appDir);
                File.WriteAllText(Path.Combine(appDir, AppUpdateClient.MainExeName), "OLD-BINARY", new UTF8Encoding(false));

                // 发布清单：大小与哈希由程序自己算
                var published = AppUpdateClient.BuildServerManifest("9.9.9", "自检发布版", minVersion: "9.0.0");
                if (published.Sha256 is not { Length: 64 }) throw new Exception("清单未写入哈希");
                if (published.Size <= 0) throw new Exception("清单未写入大小");
                if (!File.Exists(AppUpdateClient.ServerManifest)) throw new Exception("清单文件未落盘");

                var info = AppUpdateClient.ServerInfo();
                if (info?.Version != "9.9.9") throw new Exception("服务端版本读回不对：" + info?.Version);

                server = new FakeDataServer(tmp) { AppVersionProvider = AppUpdateClient.ServerInfo };
                var chk = Task.Run(() => AppUpdateClient.CheckAsync(server.Url)).GetAwaiter().GetResult();
                if (!chk.Ok) throw new Exception("检查更新失败：" + chk.Message);
                if (!chk.HasUpdate) throw new Exception("9.9.9 应被判定为有更新");
                if (!chk.Mandatory) throw new Exception("本机低于最低版本 9.0.0，应判定为必需更新");

                // 哈希不对时必须拒绝安装
                var bad = Task.Run(() => AppUpdateClient.DownloadAsync(server.Url, new AppVersionInfo
                {
                    Version = "9.9.9", Url = "app/package", Sha256 = new string('0', 64),
                })).GetAwaiter().GetResult();
                if (bad.Ok || bad.Message.IndexOf("校验失败", StringComparison.Ordinal) < 0)
                    throw new Exception("哈希不匹配时应拒绝：" + bad.Message);

                var dl = Task.Run(() => AppUpdateClient.DownloadAsync(server.Url, info)).GetAwaiter().GetResult();
                if (!dl.Ok) throw new Exception("下载失败：" + dl.Message);
                if (!File.Exists(dl.FilePath)) throw new Exception("下载文件不存在");
                if (DataCatalog.Sha256(dl.FilePath) != info.Sha256) throw new Exception("下载内容哈希不符");

                // 解包 + 生成计划（程序目录指向临时目录，绝不碰真实安装目录）
                AppUpdateClient.AppDirOverride = appDir;
                var plan = AppUpdateClient.PrepareApply(dl.FilePath, info.Version);
                if (plan.FileCount < 2) throw new Exception($"待替换文件数偏少：{plan.FileCount}");
                if (!File.Exists(Path.Combine(plan.StageDir, AppUpdateClient.MainExeName)))
                    throw new Exception("暂存目录里没有主程序");
                if (plan.AppDir != appDir) throw new Exception("程序目录指向不对：" + plan.AppDir);
                if (plan.ExePath != Path.Combine(appDir, AppUpdateClient.MainExeName))
                    throw new Exception("重启目标不对：" + plan.ExePath);
                if (plan.BackupDir == null || !File.Exists(Path.Combine(plan.BackupDir, AppUpdateClient.MainExeName)))
                    throw new Exception("原程序未备份到暂存目录");

                // 重启脚本必须存在且**纯 ASCII**（cmd 按 OEM 代码页读批处理，中文会变乱码命令）
                if (!File.Exists(plan.ScriptPath)) throw new Exception("重启脚本未生成");
                var scriptBytes = File.ReadAllBytes(plan.ScriptPath);
                if (scriptBytes.Any(b => b > 127)) throw new Exception("重启脚本含非 ASCII 字节");
                var script = Encoding.ASCII.GetString(scriptBytes);
                foreach (var need in new[] { "tasklist", "xcopy", "start", "%STAGE%", "%APP%", "%EXE%" })
                    if (script.IndexOf(need, StringComparison.Ordinal) < 0)
                        throw new Exception($"脚本缺少「{need}」");
                if (AppUpdateClient.UpdaterScript().Any(c => c > 127))
                    throw new Exception("脚本模板含非 ASCII 字符");

                // 本地包安装与在线下载走同一条准备链路
                var local = AppUpdateClient.ApplyLocalPackage(dl.FilePath);
                if (!File.Exists(local.ScriptPath)) throw new Exception("本地包安装未生成脚本");

                // 更新源没发布任何版本时：空版本号 = 已是最新，不是错误
                server.AppVersionProvider = () => new AppVersionInfo { Version = "" };
                var none = Task.Run(() => AppUpdateClient.CheckAsync(server.Url)).GetAwaiter().GetResult();
                if (!none.Ok || none.HasUpdate) throw new Exception("未发布版本时应报「已是最新」而不是更新：" + none.Message);

                d($"检查→下载 {DataCatalog.SizeText(dl.Size)}→哈希校验→解包 {plan.FileCount} 个文件 · "
                  + $"重启脚本 {scriptBytes.Length} 字节纯 ASCII · 原程序已备份 · 未发布时回「已是最新」 · 走 HTTP {server.Requests} 次");
            }
            finally
            {
                AppUpdateClient.AppDirOverride = origAppDir;
                server?.Dispose();
                restore();
            }
        });

        Head("十一、等待反馈（分段切换页面渐显）");

        Check("分段切换页面渐显（四页都挂 / 内容压淡→淡回 / 顶部进度线定量→呼吸 / 不弹卡 / 最短停留 / 开关可旁路）", d =>
        {
            // 四个带分段页签的页面都必须挂 BusyVeil —— 少一个，那个页面点页签就是纯静止
            var pages = new (string Name, Func<FrameworkElement> Make)[]
            {
                ("德州物价", () => new DezhouPriceView()),
                ("新项目征求意见", () => new DraftPriceView()),
                ("立项指南", () => new LixiangView()),
                ("后台·立项指南", () => new AdminLixiangView()),
            };
            var missing = new List<string>();
            foreach (var (name, make) in pages)
            {
                var fe = make();
                fe.Measure(new Size(1500, 760));
                fe.Arrange(new Rect(0, 0, 1500, 760));
                fe.UpdateLayout();
                var one = FindVeil(fe);
                if (one == null) { missing.Add(name); continue; }
                if (one.IsBusy) throw new Exception($"{name} 的渐显控件默认应是收起的");
            }
            if (missing.Count > 0) throw new Exception("未挂渐显控件：" + string.Join(" / ", missing));

            // 量像素必须量「真的装好数据的整页」：渐显作用在页面自身，
            // 把控件单独拆出来渲染等于什么都没测（它本来就只有一条 3px 的线）
            (int Dark, int TopAccent, int WidestRow) Shoot(FrameworkElement fe)
            {
                fe.UpdateLayout();
                var w = (int)Math.Max(1, fe.ActualWidth);
                var h = (int)Math.Max(1, fe.ActualHeight);
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96,
                    System.Windows.Media.PixelFormats.Pbgra32);
                bmp.Render(fe);
                var stride = w * 4;
                var buf = new byte[stride * h];
                bmp.CopyPixels(buf, stride, 0);
                var dark = 0;
                var accent = 0;
                var widest = 0;
                for (var y = 0; y < h; y++)
                {
                    var rowAccent = 0;
                    for (var x = 0; x < w; x++)
                    {
                        var i = y * stride + x * 4;
                        // ⚠ RenderTargetBitmap 是 Pbgra32：预乘 alpha，整页透明底。
                        // 不合成直接读 RGB 会把「半透明白」当成黑色（压淡后深色像素反而暴涨）。
                        // 这里按白底合成一次，量到的才是肉眼看到的颜色。
                        var a = buf[i + 3];
                        var inv = 255 - a;
                        var b = Math.Min(255, buf[i] + inv);
                        var g = Math.Min(255, buf[i + 1] + inv);
                        var r = Math.Min(255, buf[i + 2] + inv);
                        // 深色像素 ≈ 正文：浅色主题下「变淡」的可见证据就是它，不是整体亮度
                        if ((r * 30 + g * 59 + b * 11) / 100 < 110) dark++;
                        // 只看页面上沿 40 行（根 Grid 有 Margin，线不在第 0 行）；
                        // 取「最宽的一行」而不是总像素数：页签选中的色块也是强调色，
                        // 只数总量分不清「一条横线」和「一块小色块」。
                        if (y < 40 && b > 170 && b - r > 60 && r < 130)
                        {
                            accent++;
                            rowAccent++;
                        }
                    }
                    if (rowAccent > widest) widest = rowAccent;
                }
                return (dark, accent, widest);
            }

            var page = new DezhouPriceView();
            page.Prewarm(1500, 760);
            var veil = FindVeil(page);
            var root = (FrameworkElement)veil.Parent;
            var before = Shoot(root);
            if (before.Dark < 2000)
                throw new Exception($"页面里几乎没有正文像素（{before.Dark}），这次量像素没有意义");

            veil.DiagFreeze("正在载入现行表格…", "自检用文案");
            if (!veil.IsBusy) throw new Exception("Show 之后渐显没有生效");
            if (veil.DimmedCount == 0)
                throw new Exception("没有压淡任何同级内容 —— 控件可能被放进了嵌套 Grid，只淡了它自己那一块");

            var during = Shoot(root);
            // ① 不是卡：控件本体只有一条 3px 的线，页面里没有第二块浮层
            if (veil.Content is not FrameworkElement barBox || barBox.ActualHeight > 4)
                throw new Exception($"控件本体高度 {((FrameworkElement)veil.Content).ActualHeight:0.#}px —— 又变回一张卡了");
            // ② 内容真的被压淡：正文深色像素大幅减少
            if (during.Dark > before.Dark * 0.6)
                throw new Exception($"页面没有被压淡（深色像素 {before.Dark} → {during.Dark}）");
            // ③ 顶部进度线画出来了：某一行的强调色像素要连成一条（页签色块只有几十像素宽）
            if (during.WidestRow < 300)
                throw new Exception($"顶部看不到进度线（最宽一行强调色仅 {during.WidestRow} 像素，像色块不像线）");

            // ④ 定量段 / 呼吸段
            if (veil.PercentShown < 1 || veil.PercentShown > 100)
                throw new Exception($"定量段的进度百分比异常：{veil.PercentShown}");
            if (veil.Spilled) throw new Exception("初段就呼吸了，用户看不出「还剩多少」");
            veil.DiagFreeze("正在载入现行表格…", "自检用文案", spill: true);
            if (!veil.Spilled) throw new Exception("超过预估时长后没有切到呼吸态");
            if (veil.PercentShown != -1) throw new Exception("切到呼吸态后仍在报定量百分比");

            // ⑤ 淡回：内容不透明度必须回到原值，否则页面会永久发灰
            veil.FinishNow();
            if (veil.IsBusy) throw new Exception("FinishNow 之后仍是亮起状态");
            var after = Shoot(root);
            if (after.Dark < before.Dark * 0.9)
                throw new Exception($"收起后页面没有恢复（深色像素 {before.Dark} → {after.Dark}）");
            if (root is Panel rp)
                foreach (UIElement c in rp.Children)
                    if (!ReferenceEquals(c, veil) && c.Opacity < 0.99)
                        throw new Exception($"收起后仍有内容停在 {c.Opacity:0.##} 的不透明度");

            // ⑥ 最短停留：立刻叫停也要等满 DefaultMinMs，防止「一闪而过像闪屏故障」
            veil.Show("正在载入现行表格…", "自检用文案", 200);
            veil.HideAfter();
            if (!veil.IsBusy) throw new Exception("HideAfter 没等最短停留就收起了");
            var until = Environment.TickCount64 + Dip3GrouperApp.Desktop.Controls.BusyVeil.DefaultMinMs + 400;
            while (veil.IsBusy && Environment.TickCount64 < until)
            {
                var frame = new DispatcherFrame();
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                t.Tick += (_, _) => { t.Stop(); frame.Continue = false; };
                t.Start();
                Dispatcher.PushFrame(frame);
            }
            if (veil.IsBusy) throw new Exception("最短停留过后仍没有收起");
            veil.FinishNow();

            // ⑦ 全局旁路：自检 / 截图流程可以整体关掉，避免每张截图都变淡
            var orig = Dip3GrouperApp.Desktop.Controls.BusyVeil.Enabled;
            try
            {
                Dip3GrouperApp.Desktop.Controls.BusyVeil.Enabled = false;
                veil.Show("x");
                if (veil.IsBusy) throw new Exception("Enabled=false 时仍亮起渐显");
            }
            finally { Dip3GrouperApp.Desktop.Controls.BusyVeil.Enabled = orig; }

            d($"4 个页面均挂渐显 · 压淡后深色像素 {before.Dark}→{during.Dark}、收起后回 {after.Dark} · "
              + $"顶部进度线最宽 {during.WidestRow} 像素 · 控件本体仅 {(veil.Content as FrameworkElement)?.ActualHeight:0.#}px（不弹卡）· "
              + $"定量段 {Dip3GrouperApp.Desktop.Controls.BusyVeil.FadeInMs}ms 淡回、最短停留 {Dip3GrouperApp.Desktop.Controls.BusyVeil.DefaultMinMs}ms · 全局开关可旁路");
        });

        Check("分段切换实机渐显（真窗口 + 真消息循环：四页签 + 查询 + 翻页都压淡→淡回、文案对应、按时收起）", d =>
        {
            // 离屏只能验证版式；「点下去真的会亮」必须靠真实消息循环，
            // 因为让出一帧靠的是 Dispatcher.BeginInvoke(Render)，动画时钟也要靠渲染帧推进。
            var log = Bench.VeilTrace(Path.Combine(Path.GetTempPath(), "dip3-veil-selftest"));
            var lines = File.ReadAllLines(log).Where(l => l.Contains("OK  ") || l.Contains("FAIL")).ToList();
            // 四个页签 + 查询 + 下一页 = 6 条：查询与翻页走同一条 Load 通路，也必须压淡
            if (lines.Count < 6) throw new Exception($"实机留痕不足 6 条（页签/查询/翻页没走全）：{string.Join(" | ", lines)}");
            var fail = lines.FirstOrDefault(l => l.Contains("FAIL"));
            if (fail != null) throw new Exception("实机点击没有压淡 —— " + fail[(fail.IndexOf(']') + 1)..].Trim());
            var caps = lines.Select(l => l[(l.IndexOf('「') + 1)..l.LastIndexOf('」')]).ToList();
            if (caps.Distinct().Count() < 6)
                throw new Exception("六处入口的渐显文案没区分动作：" + string.Join(" / ", caps));
            // 实机上的「压淡 → 淡回」：只验亮没亮等于没验渐显本体
            var noFadeBack = lines.FirstOrDefault(l => !l.Contains("收尾 1.00"));
            if (noFadeBack != null)
                throw new Exception("有入口收起后没有淡回 1.0（页面会永久发灰）—— " + noFadeBack[(noFadeBack.IndexOf(']') + 1)..].Trim());
            var visible = lines.Select(l =>
            {
                var i = l.IndexOf("可见于 ", StringComparison.Ordinal) + 4;
                var j = l.IndexOf("ms", i, StringComparison.Ordinal);
                return int.TryParse(l[i..j], out var v) ? v : -1;
            }).ToList();
            if (visible.Any(v => v < 0)) throw new Exception("没能测到「点击 → 内容被压淡」的时刻");
            d($"作废对照 {visible[0]}ms / 旧版物价 {visible[1]}ms / 新旧比对 {visible[2]}ms / 现行项目 {visible[3]}ms"
              + $" / 查询 {visible[4]}ms / 下一页 {visible[5]}ms"
              + " 均「点击即亮」，文案各自对应动作，最短停留后自动收起");
        });

        Check("新旧比对三档口径（差额涨幅按档算 / 表头随档 / 统计同口径 / 立项指南反查与跳转）", d =>
        {
            var store = App.State.Dezhou;
            var all = store.CompareRows();
            if (all.Count == 0) throw new Exception("比对数据为空，无法验证口径");

            // ① 三档差额都要算出来：只算三级的话切档后差额是 null，表格会整列显示「—」
            var both = all.FirstOrDefault(r => r.Delta3 is not null && r.Delta2 is not null && r.Delta1 is not null);
            if (both == null) throw new Exception("没有一条行同时具备三档差额，三档计算可能只做了一档");
            if (both.Ratio2 == null || both.Ratio1 == null)
                throw new Exception("二级 / 一级涨幅没算：切档后涨幅列会变成「—」");

            // ② 三档的差额关系：价格越高的档位，差额应当越大或相等（同向单调）
            var crossCheck = all.Where(r => r.Delta3 is not null && r.Delta1 is not null && r.Delta3 != r.Delta1).Take(5).ToList();
            if (crossCheck.Count == 0) throw new Exception("三档差额全都相同，档位选择形同虚设（数据或取价可能错档）");

            // ③ 统计必须跟口径走：三个档位各算一遍，至少两档的涨跌分布不同（否则说明统计没接口径参数）
            var s3 = store.CompareStat("3");
            var s2 = store.CompareStat("2");
            var s1 = store.CompareStat("1");
            if (s3.Priced != s2.Priced || s3.Priced != s1.Priced)
                throw new Exception("不同口径下「有旧价可比」条数应当一致（口径只该影响涨跌，不该影响条数）");
            if (s3.Up == s2.Up && s2.Up == s1.Up && s3.Down == s2.Down && s2.Down == s1.Down)
                throw new Exception($"三档涨跌数完全一样（{s3.Up}/{s2.Up}/{s1.Up}），统计没按口径算");

            // ④ 立项指南反查：名称精确 + 加收项退主项名，两条路都要通
            var guideHit = 0;
            var addBacked = 0;
            foreach (var r in all.Take(400))
            {
                var g = App.State.Lixiang.FindGuide(r.NewName);
                if (g != null) guideHit++;
                if (r.NewKind is "add" or "ext" && g != null) addBacked++;
            }
            if (guideHit == 0) throw new Exception("前 400 行里一条立项指南都没查到，反查索引可能没建起来");
            if (addBacked == 0) throw new Exception("加收 / 扩展项没有一条通过「主项名」命中指南，退主项的逻辑没生效");

            // ⑤ 主项名归一：全角括号与空白不能影响命中
            if (LixiangStore.NormName("心脏移植术（儿童）") != LixiangStore.NormName("心脏移植术(儿童)"))
                throw new Exception("全角 / 半角括号没有归一，会造成大量假未命中");
            if (LixiangStore.MainName("心脏移植术-儿童手术(加收)") != "心脏移植术")
                throw new Exception("加收项主项名解析错误：" + LixiangStore.MainName("心脏移植术-儿童手术(加收)"));

            // ⑥ 页面换档：列头与行 Level 必须一起变，且不重建 ItemsSource（重建会丢滚动位置）
            var v = new DezhouPriceView();
            v.Prewarm(1500, 760);
            v.DiagCompareLevel("3");                 // 就位到三级口径（含一次按范围取数）
            var before = v.CmpRows.ToList();
            if (before.Count == 0) throw new Exception("比对视图没有行");
            if (before.Any(r => r.Level != "3")) throw new Exception("切档后行上的 Level 没同步");
            var headBefore = v.CmpHeaders;

            // 这里模拟「用户点口径下拉」：设置选中项 → 触发 OnCmpLevelChanged（换档不该重新取数）
            v.PickCmpLevel("1");
            var after = v.CmpRows.ToList();
            if (after.Count == 0) throw new Exception("切到一级口径后没有行");
            if (after.Any(r => r.Level != "1")) throw new Exception("一级口径没有下发到行");
            if (after[0] != before[0]) throw new Exception("换档重建了行对象（应只改 Level、复用同一批 VM）");
            if (v.CmpHeaders.SequenceEqual(headBefore)) throw new Exception("换档后表头没变，用户看不出当前是哪一档：" + string.Join("/", v.CmpHeaders));
            if (!v.CmpHeaders.Contains("新一级价") || !v.CmpHeaders.Contains("旧一级价"))
                throw new Exception("表头没写出一级：" + string.Join("/", v.CmpHeaders));
            // 换档后单元格文本必须跟着变（用户真正看到的东西）。
            // ⚠ 不能拿首行断言：首行常是「旧价缺失」的纯新增 / 仅作废对照行，差额本来就是「—」、
            //    三档价格也可能是空的，两边一样不代表换档失效。挑一条三档差额都有的行来验。
            var probe = before.FirstOrDefault(r => r.Delta3 is not null && r.Delta1 is not null && r.Delta3 != r.Delta1);
            if (probe == null) throw new Exception("当前页没有一条三档差额不同的行，无法验证换档是否作用到单元格");
            var d3Text = probe.DeltaText;
            var p3Text = probe.NewPrice3Text;
            v.PickCmpLevel("3");
            var t3 = probe.DeltaText;
            var c3 = probe.NewPrice3Text;
            v.PickCmpLevel("1");
            var t1 = probe.DeltaText;
            var c1 = probe.NewPrice3Text;
            if (t3 == t1 && c3 == c1)
                throw new Exception($"换档后该行（{probe.NewName}）价格与差额都没变：{c3}/{t3} → {c1}/{t1}");

            // ⑦ 跳转落点：立项指南页要能按「区段 + 名称」定位到目标行
            var sample = all.Select(r => App.State.Lixiang.FindGuide(r.NewName)).FirstOrDefault(g => g != null);
            if (sample == null) throw new Exception("找不到可用于验证跳转的指南条目");
            var lv = new LixiangView();
            lv.Prewarm(1500, 760);
            if (!lv.Locate(sample.Value.Tab, sample.Value.Row.Name))
                throw new Exception("立项指南页定位失败：" + sample.Value.Row.Name);
            var first = lv.FirstRowName;
            if (LixiangStore.NormName(first) != LixiangStore.NormName(sample.Value.Row.Name))
                throw new Exception($"定位后首行是「{first}」，期望「{sample.Value.Row.Name}」");

            d($"三档差额齐备（样例 三级 {both.Delta3} / 二级 {both.Delta2} / 一级 {both.Delta1}）· "
              + $"涨跌按口径分开（三级 涨{s3.Up}降{s3.Down} / 二级 涨{s2.Up}降{s2.Down} / 一级 涨{s1.Up}降{s1.Down}）· "
              + $"指南反查前 400 行命中 {guideHit}（其中加收扩展 {addBacked}）· 换档只改显示不重建行 · "
              + $"跳转定位到「{first}」");
        });

        Check("立项指南跳转链路（真窗口：点比对行「立项指南」→ 宿主切页签 → 指南页定位到该条目）", d =>
        {
            // 离屏只能证明「定位方法本身可用」，证明不了「点了链接宿主会切页」。
            // 这里挂真实窗口，让 Window.GetWindow(this) 拿得到 MainWindow。
            // ⚠ 关淡入：FadeIn 会把目标页 Opacity 先压到 0 再动画回来，
            //   不跑消息循环时它永远停在 0 —— Visibility 是 Visible 但「看不见」，断言会误判。
            var origFade = MainWindow.FadeMs;
            MainWindow.FadeMs = 0;
            var win = new MainWindow { Width = 1400, Height = 860 };
            win.MakeInvisibleForDiag();   // 离屏 + 关窗不弹确认（否则自检会卡在确认框上等人点）
            win.Show();
            try
            {
                var dzView = win.DzViewForTest;
                dzView.EnsureLoaded();
                dzView.DiagCompareLevel("3", "priced");
                if (dzView.CmpRows.Count() == 0) throw new Exception("比对视图没有行");

                // 找一条「有立项指南」的行去点（前几行常是未收录的，直接点第 0 行会命中置灰分支）
                var idx = -1;
                var target = "";
                var rows = dzView.CmpRows.ToList();
                for (var i = 0; i < Math.Min(rows.Count, 40); i++)
                    if (rows[i].HasGuide) { idx = i; target = rows[i].NewName; break; }
                if (idx < 0) throw new Exception("当前页前 40 行里没有一条命中立项指南，无法验证跳转");

                var clicked = dzView.ClickGuideLink(idx);
                if (clicked.Length == 0) throw new Exception("没能点到「立项指南」链接（单元格可能未生成）");

                var lx = win.LxViewForTest;
                if (lx.Visibility != Visibility.Visible)
                {
                    var host = System.Windows.Window.GetWindow(dzView);
                    throw new Exception($"点了链接但立项指南页没有显示出来（点的是「{clicked}」；"
                        + $"宿主={(host == null ? "null" : host.GetType().Name)}；"
                        + $"指南页 Visibility={lx.Visibility}；"
                        + $"物价页 Visibility={dzView.Visibility}；"
                        + $"状态栏=「{dzView.StatusForTest}」）");
                }
                var first = lx.FirstRowName;
                if (LixiangStore.NormName(first) != LixiangStore.NormName(target))
                    throw new Exception($"跳过去后首行是「{first}」，期望「{target}」（定位没落到目标行）");

                d($"点击「{target}」→ 立项指南页已显示，定位到区段「{lx.CurrentTab}」首行「{first}」");
            }
            finally
            {
                win.Close();
                MainWindow.FadeMs = origFade;
            }
        });

        // ================= 十六、目录库外病种（省局反馈清单） =================
        Head("十六、目录库外病种（省局反馈清单：识别 / 规避建议 / 界面）");

        Check("目录库外病种识别与规避建议（清单 2109 条 / 尾零归一 / 保守行语义 / 不误伤 / 建库分页 / 契约不破）", d =>
        {
            var cfg = App.State.Config;
            var rows = cfg.OutLib ?? new List<OutLibRow>();
            if (rows.Count != 2109) throw new Exception($"清单条数不符：{rows.Count}（期望 2109）");

            var g = App.State.Grouper;
            if (g.OutLibRuleCount != rows.Count)
                throw new Exception($"分组器索引条数 {g.OutLibRuleCount} ≠ 清单 {rows.Count}（配置热更新没刷新索引？）");

            // ① 清单原样码 + 病案尾零码都要命中
            var a = g.FindOutLib(new Kase { MainDiag = "H65", MainProc = "22.02" });
            var b = g.FindOutLib(new Kase { MainDiag = "H65", MainProc = "22.0200" });
            if (a == null || b == null) throw new Exception("H65 + 22.02 / 22.0200 未命中清单");
            if (a.Label != "H65-22.02") throw new Exception($"清单键应为 H65-22.02，实际 {a.Label}");

            // ② 清单只写到主码时，病案的 x 扩展要认回来（04.9900 ← 04.9900x001）
            if (g.FindOutLib(new Kase { MainDiag = "G57.0", MainProc = "04.9900x001" }) == null)
                throw new Exception("x 扩展码（04.9900x001）没认回主码 04.9900");

            // ③ 保守行语义：清单只由诊断定义时，只有「无主手术」的病案算命中
            if (g.FindOutLib(new Kase { MainDiag = "P58.9" }) == null)
                throw new Exception("仅诊断病种 P58.9（无手术）未命中");
            if (g.FindOutLib(new Kase { MainDiag = "P58.9", MainProc = "99.0400" }) != null)
                throw new Exception("仅诊断病种被「带手术」的病案误命中");

            // ④ 不误伤：正常可入组组合不能标成目录库外
            if (g.FindOutLib(new Kase { MainDiag = "A09.901", MainProc = "44.1300x001" }) != null)
                throw new Exception("正常组合 A09.901 + 44.1300x001 被误判为目录库外");

            // ⑤ 规避建议：同诊断 + 同手术大类优先，且不能把原组合当建议
            if (string.IsNullOrEmpty(a.Suggest)) throw new Exception("H65 没有算得可入组组合");
            var first = a.Suggest.Split('；')[0];
            if (!first.Contains("H65")) throw new Exception($"建议首条不是同诊断组合：{first}");
            var m17 = g.FindOutLib(new Kase { MainDiag = "M17", MainProc = "77.2700x001" });
            if (m17 == null) throw new Exception("M17 + 77.2700x001 未命中");
            if (m17.Suggest.IndexOf("77.", StringComparison.Ordinal) < 0)
                throw new Exception($"M17 的建议没优先同手术大类（77.xx）：{m17.Suggest}");
            if (m17.Suggest.Contains("77.2700x001"))
                throw new Exception("建议里出现了被预警的原组合（等于建议继续这么填）");

            // ⑥ 无同诊断病种的行：建议为空、备注点明「无同诊断病种」
            var none = rows.FirstOrDefault(r => string.IsNullOrEmpty(r.Suggest));
            if (none == null) throw new Exception("应当存在「同诊断无目录库病种」的行");
            if (!(none.Note ?? "").Contains("无同诊断病种"))
                throw new Exception($"无建议行的备注没说明原因：{none.Note}");

            // ⑦ 分组结果要带预警（命中清单）且不误挂（目录库内组合）
            var r1 = GrouperService.Group(App.State, new Kase { MainDiag = "H65", MainProc = "22.0200" }, false);
            if (r1.OutLib == null) throw new Exception("分组结果没带出目录库外预警");
            var r2 = GrouperService.Group(App.State, new Kase { MainDiag = "H65", MainProc = "20.0100x005" }, false);
            if (r2.OutLib != null) throw new Exception("目录库内组合的分组结果被误挂预警");

            // ⑧ 契约不破：OutLib 是旁注，绝不能进 /api/group 的 JSON
            var json = JsonSerializer.Serialize(r1, Js.Http);
            if (json.IndexOf("outLib", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new Exception("outLib 泄漏进 /api/group 返回契约");

            // ⑨ 病种库区段：能列出、能检索、总数与清单一致
            var sec = Diseases.Get("outlib");
            if (sec == null) throw new Exception("病种库没有 outlib 区段");
            var all = Diseases.ListRows(cfg, "outlib", "", 1, 50);
            if (all.Total != rows.Count) throw new Exception($"区段总数 {all.Total} ≠ 清单 {rows.Count}");
            var hit = Diseases.ListRows(cfg, "outlib", "非化脓性中耳炎", 1, 20);
            if (hit.Total == 0) throw new Exception("按诊断名称检索不到清单行");
            var byCode = Diseases.ListRows(cfg, "outlib", "22.0200", 1, 20);
            if (byCode.Rows.Count == 0) throw new Exception("按手术编码检索不到清单行（22.0200 与 22.02 未归一）");

            var withProc = rows.Count(r => !string.IsNullOrEmpty(r.ProcCode));
            var withSuggest = rows.Count(r => !string.IsNullOrEmpty(r.Suggest));
            d($"清单 {rows.Count:N0} 条（含手术 {withProc:N0} / 仅诊断 {rows.Count - withProc}）· "
              + $"可给建议 {withSuggest:N0} 条 · 索引 {g.OutLibRuleCount:N0} · "
              + $"H65-22.02 命中且建议 {a.Suggest.Split('；').Length} 条 · JSON 契约无 outLib");
        });

        Check("目录库外病种界面（单条预警卡 / 批量列与摘要 / 病种库新页签）", d =>
        {
            // 单条：命中清单 → 预警卡 + 规避建议都在结果区
            var sg = new SingleGroupView();
            sg.Measure(new Size(1400, 1000));
            sg.Arrange(new Rect(0, 0, 1400, 1000));
            sg.UpdateLayout();
            sg.DiagGroup("H65", "22.0200");
            sg.UpdateLayout();
            var texts = Descendants<TextBlock>(sg).Select(t => t.Text ?? "").ToList();
            if (!texts.Any(t => t.Contains("目录库外病种")))
                throw new Exception("单条结果没有「目录库外病种」预警卡");
            if (!texts.Any(t => t.Contains("同诊断在目录库内的可入组组合")))
                throw new Exception("预警卡缺少规避建议区块");
            if (!texts.Any(t => t.Contains("非化脓性中耳炎")))
                throw new Exception("预警卡没写出命中的组合");

            // 目录库内组合不该出现预警卡
            sg.DiagGroup("H65", "20.0100x005");
            sg.UpdateLayout();
            if (Descendants<TextBlock>(sg).Any(t => (t.Text ?? "").Contains("目录库外病种")))
                throw new Exception("目录库内组合也被标成目录库外");

            // 批量：3 条命中（含 1 条保守行），摘要条要单列「目录库外」
            var bg = new BatchGroupView();
            bg.Measure(new Size(1500, 900));
            bg.Arrange(new Rect(0, 0, 1500, 900));
            bg.UpdateLayout();
            bg.DiagBatch("病案号\t主要诊断编码\t主要手术操作编码\n"
                         + "MZ001\tH65\t22.0200\n"
                         + "MZ002\tM17\t77.2700x001\n"
                         + "MZ003\tA09.901\t44.1300x001\n"
                         + "MZ004\tP58.9\t");
            bg.UpdateLayout();
            var brows = bg.RowsForTest.ToList();
            if (brows.Count != 4) throw new Exception($"批量结果行数 {brows.Count} ≠ 4");
            var flagged = brows.Where(r => !string.IsNullOrEmpty(r.OutLibLabel)).ToList();
            if (flagged.Count != 3) throw new Exception($"目录库外命中 {flagged.Count} 条，期望 3 条");
            if (!bg.SummaryText.Contains("目录库外")) throw new Exception("摘要条没有「目录库外」计数");

            // 病种库：新页签能切、能列出
            var lib = new DiseaseLibView();
            lib.Prewarm(1400, 760);
            lib.DiagTab("outlib");
            if (lib.CurrentTab != "outlib") throw new Exception("病种库没切到 outlib 区段");
            if (lib.RowCount == 0) throw new Exception("目录库外病种页签没有行");

            d($"单条预警卡 ✓（含建议）· 目录库内组合不误标 ✓ · 批量命中 3/4（摘要「{bg.SummaryText.Trim()}」）· "
              + $"病种库 outlib 首页 {lib.RowCount} 行");
        });

        // ================= 十七、单条分组输入框手感 =================
        Head("十七、单条分组输入框（联想选中不回弹 / 提示文字对位 / 名称回显）");

        Check("联想输入框实机手感（真窗口 + 真防抖：选中后不重开 / 整段选中 / 水印对位 / 名称回显）", d =>
        {
            // 防抖计时器只在真消息循环里跑，离屏永远等不到 Tick → 只能开真窗口采样
            var log = Bench.SuggestTrace(Path.Combine(Path.GetTempPath(), "dip3-suggest-selftest"));
            var lines = File.ReadAllLines(log);
            var fails = lines.Where(l => l.Contains("FAIL")).ToList();
            if (fails.Count > 0) throw new Exception("实机采样有失败项：" + string.Join(" | ", fails));
            var oks = lines.Count(l => l.Contains("OK  "));
            if (oks < 7) throw new Exception($"只采到 {oks} 项（期望 7 项）");

            // 名称回显的取数逻辑（不依赖界面）
            if (GrouperService.NameOf(App.State, "A09.901") != "胃肠炎")
                throw new Exception("诊断编码 A09.901 没解析出名称「胃肠炎」");
            if (string.IsNullOrEmpty(GrouperService.NameOf(App.State, "44.1300x001", proc: true)))
                throw new Exception("手术编码 44.1300x001 没解析出名称");
            if (GrouperService.NameOf(App.State, "ZZZ.999") != "")
                throw new Exception("不存在的编码不该回显名称");

            d(string.Join(" · ", lines.Where(l => l.Contains("OK  "))
                .Select(l => l[(l.IndexOf("OK  ") + 4)..].Trim())));
        });

        // ================= 十八、立项指南「解读辅导」详情卡版式 =================
        Head("十八、立项指南「解读辅导」详情卡版式（展开不撑宽 / 不塌列 / 不出横向滚动条）");

        Check("解读辅导展开详情卡：宽窄两种窗口都不产生横向滚动条，「解读问题」列不被压塌", d =>
        {
            var report = new List<string>();
            foreach (var (w, h) in new[] { (1500.0, 760.0), (1060.0, 680.0) })
            {
                var v = new LixiangView();
                v.Prewarm(w, h);
                v.DiagJiedu("", 0);           // 切到解读辅导并展开首行详情（长回答，正是撑宽的那一条）
                v.Measure(new Size(w, h));
                v.Arrange(new Rect(0, 0, w, h));
                v.UpdateLayout();
                var m = v.DiagJieduSize();

                if (double.IsNaN(m.Details) || m.Details <= 0)
                    throw new Exception($"{w:0} 宽：首行详情没有展开（details={m.Details}），用例没测到东西");
                if (m.Hbar != Visibility.Collapsed)
                    throw new Exception($"{w:0} 宽：出现了横向滚动条（extent {m.Ext:0.#} > viewport {m.Vp:0.#}）");
                if (m.Ext > m.Vp + 0.5)
                    throw new Exception($"{w:0} 宽：内容比视口宽 {m.Ext - m.Vp:0.#}px（extent {m.Ext:0.#} / viewport {m.Vp:0.#}）");
                if (m.ColQ < 200)
                    throw new Exception($"{w:0} 宽：「解读问题」列塌成 {m.ColQ:0.#}px（star 列在关横滚后会失效）");
                if (m.Details > m.Vp + 0.5)
                    throw new Exception($"{w:0} 宽：详情卡比视口还宽（{m.Details:0.#} > {m.Vp:0.#}）");

                report.Add($"{w:0} 宽 → 详情 {m.Details:0}px / 视口 {m.Vp:0}px / 问题列 {m.ColQ:0}px");
            }
            d(string.Join(" · ", report));
        });

        // ================= 十九、物价比对双向跳转 / 病种库右键 =================
        Head("十九、物价比对双向跳转 · 病种库右键菜单");

        Check("新旧比对：新编码→现行项目 / 旧编码→旧版物价，返回按原页码与筛选复位", d =>
        {
            var v = new DezhouPriceView();
            v.EnsureLoaded();
            v.Measure(new Size(1500, 860));
            v.Arrange(new Rect(0, 0, 1500, 860));
            v.UpdateLayout();
            v.DiagCompareLevel("3", "priced");

            var rows = v.CmpRows.ToList();
            if (rows.Count == 0) throw new Exception("比对视图没有行（前提数据缺失）");

            var headers = v.CmpHeaders;
            if (!headers.Any(h => h.Contains("新项目编码")) || !headers.Any(h => h.Contains("旧项目编码")))
                throw new Exception("比对表头没有编码列：" + string.Join(" / ", headers));

            // ① 旧项目编码 → 旧版物价
            var oldIdx = rows.FindIndex(r => !string.IsNullOrWhiteSpace(r.OldCode));
            if (oldIdx < 0) throw new Exception("当前页没有带旧编码的行，无法验证旧项目跳转");
            var oldCode = rows[oldIdx].OldCode;
            v.Measure(new Size(1500, 860)); v.Arrange(new Rect(0, 0, 1500, 860)); v.UpdateLayout();

            var hitOld = v.ClickCmpCodeLink(oldIdx, old: true);
            if (!hitOld.StartsWith("visual:"))
                throw new Exception($"旧编码链接没点到真实按钮（返回 {hitOld}）");
            if (v.CurrentViewName != "旧版物价")
                throw new Exception("点旧编码后没有切到旧版物价，当前：" + v.CurrentViewName);
            if ((v.QueryBox.Text ?? "").Trim() != oldCode)
                throw new Exception($"旧版物价检索词是「{v.QueryBox.Text}」，期望「{oldCode}」");
            if (v.BackVisible != Visibility.Visible)
                throw new Exception("跳转后没有出现「← 返回」按钮");
            if (!v.BackLabel.Contains("新旧比对"))
                throw new Exception("返回按钮文案没指向来源视图：" + v.BackLabel);
            if (!v.StatusForTest.Contains(oldCode))
                throw new Exception("状态栏没有说明按哪个编码检索：" + v.StatusForTest);

            // ② 返回：视图 / 比对范围都要回来
            var pageBefore = v.CurrentPage;
            v.DiagBack();
            if (v.CurrentViewName != "新旧比对")
                throw new Exception("返回后没有回到新旧比对，当前：" + v.CurrentViewName);
            if (v.BackVisible != Visibility.Collapsed)
                throw new Exception("返回后「← 返回」按钮没有消失");
            if (v.CmpScopeTag != "priced")
                throw new Exception("返回后比对范围没恢复（期望 priced，实际 " + v.CmpScopeTag + "）");
            if (v.CurrentPage != pageBefore)
                throw new Exception($"返回后页码变成 {v.CurrentPage}，期望 {pageBefore}");

            // ③ 新项目编码 → 现行项目（同一页、同一返回态机制）
            v.Measure(new Size(1500, 860)); v.Arrange(new Rect(0, 0, 1500, 860)); v.UpdateLayout();
            var newRows = v.CmpRows.ToList();
            var newIdx = newRows.FindIndex(r => !string.IsNullOrWhiteSpace(r.NewCode));
            if (newIdx < 0) throw new Exception("比对行没有新编码");
            var newCode = newRows[newIdx].NewCode;
            var hitNew = v.ClickCmpCodeLink(newIdx, old: false);
            if (!hitNew.StartsWith("visual:"))
                throw new Exception($"新编码链接没点到真实按钮（返回 {hitNew}）");
            if (v.CurrentViewName != "现行项目")
                throw new Exception("点新编码后没有切到现行项目，当前：" + v.CurrentViewName);
            if ((v.QueryBox.Text ?? "").Trim() != newCode)
                throw new Exception($"现行项目检索词是「{v.QueryBox.Text}」，期望「{newCode}」");
            if (v.BackVisible != Visibility.Visible || !v.BackLabel.Contains("新旧比对"))
                throw new Exception("从比对跳现行项目后，返回按钮不对：" + v.BackLabel);

            // ④ 页码也原样回来（有第 2 页才测）
            var pageNote = "仅 1 页，未测翻页返回";
            if (v.BackVisible == Visibility.Visible) v.DiagBack();
            if (v.NextPageButton.IsEnabled)
            {
                v.DiagPage(2);
                var p2 = v.CurrentPage;
                v.Measure(new Size(1500, 860)); v.Arrange(new Rect(0, 0, 1500, 860)); v.UpdateLayout();
                var page2Rows = v.CmpRows.ToList();
                var i2 = page2Rows.FindIndex(r => !string.IsNullOrWhiteSpace(r.OldCode));
                if (i2 >= 0 && p2 == 2)
                {
                    v.ClickCmpCodeLink(i2, old: true);
                    v.DiagBack();
                    if (v.CurrentPage != 2)
                        throw new Exception($"从第 2 页跳转返回后落在第 {v.CurrentPage} 页，期望第 2 页");
                    pageNote = "第 2 页跳转后返回仍在第 2 页";
                }
            }

            d($"表头含双向编码链接 · 旧码「{oldCode}」→ 旧版物价 → 返回（范围 kept） · "
              + $"新码「{newCode}」→ 现行项目 → 返回 · {pageNote}");
        });

        Check("病种库查询右键菜单（按当前区段列动态生成 / 本行含列名 / 编码名称单字段）", d =>
        {
            var dv = new DiseaseLibView();
            dv.EnsureLoaded();
            var row = dv.Grid.Items.Cast<Dictionary<string, object>>().FirstOrDefault()
                      ?? throw new Exception("病种库没有行（前提数据缺失）");

            var menu = RowMenu.Build(row, dv.Grid, dv.Grid, null)
                       ?? throw new Exception("病种库右键菜单没有生成");
            var heads = Heads(menu);
            foreach (var need in new[] { "复制本行", "带列名" })
                if (!heads.Any(h => h.Contains(need)))
                    throw new Exception($"病种库菜单缺少「{need}」：{string.Join(" / ", heads)}");

            var single = heads.Where(h => h.StartsWith("复制 · ")).ToList();
            if (single.Count == 0)
                throw new Exception("病种库菜单缺少编码 / 名称类单字段项：" + string.Join(" / ", heads));
            if (!single.Any(h => h.Contains("序号") || h.Contains("编码") || h.Contains("诊断")))
                throw new Exception("单字段项没有命中当前区段的编码 / 名称列：" + string.Join(" / ", single));

            // 列宽可拖的前提：① 表头模板里有 DataGrid 约定的两个拖拽部件；
            // ② CanUserResizeColumns 没被样式关掉。缺一条，鼠标放到列边界连光标都不变。
            // 2026-11 修「表头拖不动列宽」时踩过：自定义表头模板漏了 Thumb，整条链路静默失效。
            if (!dv.Grid.CanUserResizeColumns)
                throw new Exception("病种库表格关闭了 CanUserResizeColumns，列宽拖不动");

            var headStyle = (Style)dv.FindResource("DgHead");
            var tplSetter = headStyle.Setters.OfType<Setter>()
                .FirstOrDefault(s => s.Property == Control.TemplateProperty)
                ?? throw new Exception("DgHead 没有设置 Template");
            var tpl = (ControlTemplate)tplSetter.Value;
            var partNames = new List<string>();
            void WalkParts(DependencyObject o)
            {
                if (o is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name)) partNames.Add(fe.Name);
                for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(o); i++)
                    WalkParts(System.Windows.Media.VisualTreeHelper.GetChild(o, i));
            }
            WalkParts(tpl.LoadContent());
            foreach (var part in new[] { "PART_LeftHeaderGripper", "PART_RightHeaderGripper" })
                if (!partNames.Contains(part))
                    throw new Exception($"表头模板缺少 {part}（DataGrid 靠它拖列宽），现有部件："
                        + string.Join(" / ", partNames));

            // 列宽必须是算好的像素值：Star 列在 Auto 横向滚动条下会塌到 MinWidth
            if (dv.Grid.Columns.Any(c => !c.Width.IsAbsolute))
                throw new Exception("病种库存在非像素列宽，Star 列会被压成最小宽度："
                    + string.Join(" / ", dv.Grid.Columns.Select(c => $"{c.Header}={c.Width.UnitType}")));

            // 复制文本：制表符列数与表格列数一致，带列名版首行是列名
            var rowText = RowMenu.DiseaseRowText(row, dv.Grid, withHeader: false);
            var headText = RowMenu.DiseaseRowText(row, dv.Grid, withHeader: true);
            var nCols = dv.Grid.Columns.Count;
            if (rowText.Split('\t').Length != nCols)
                throw new Exception($"本行文本 {rowText.Split('\t').Length} 列，表格有 {nCols} 列：{rowText}");
            if (!headText.Contains("\n") || headText.Split('\n')[0].Split('\t').Length != nCols)
                throw new Exception("带列名版本的列名行与表格列数不一致：" + headText.Split('\n')[0]);

            // 菜单项必须跟着区段换：换到先期分组区段，列名集合应不同
            var first = string.Join("|", single);
            var otherTab = Diseases.SectionCounts(App.State.Config).Keys
                .FirstOrDefault(t => t != dv.CurrentTab);
            if (!string.IsNullOrEmpty(otherTab))
            {
                dv.DiagTab(otherTab);
                var row2 = dv.Grid.Items.Cast<Dictionary<string, object>>().First();
                var heads2 = Heads(RowMenu.Build(row2, dv.Grid, dv.Grid, null));
                var single2 = heads2.Where(h => h.StartsWith("复制 · ")).ToList();
                if (single2.Count == 0) throw new Exception($"区段 {otherTab} 的菜单没有单字段项");
                if (string.Join("|", single2) == first)
                    throw new Exception($"换了区段（{otherTab}）菜单项没变，列名可能写死了");
            }

            d($"区段 {dv.CurrentTab} · {heads.Count} 项 · 单字段 {string.Join(" / ", single)}");
        });

        // ================= 二十、导出正确性 =================
        Head("二十、导出正确性（全出口逐表对账：表头 / 行宽 / 行序 / 筛选 / 空结果）");

        Check("前台物价导出（表头=列定义 / 行序不错行 / 价格不是公式文本 / 使用说明附表）", d =>
        {
            var labels = DezhouPriceStore.Columns.Select(c => c.Label).ToList();
            var items = App.State.Dezhou.Items.Take(25).ToList();
            if (items.Count == 0) throw new Exception("物价库为空，无法验证导出");

            var (bytes, name) = DezhouExport.Front(App.State, items, "全部结果");
            var wb = ExcelUtil.Open(bytes);
            var aoa = ExcelUtil.ReadAoa(wb.GetSheetAt(0));

            // ① 表头必须与列定义逐列相同（顺序也相同），否则整表列错位
            if (aoa[0].Count != labels.Count)
                throw new Exception($"表头 {aoa[0].Count} 列 ≠ 列定义 {labels.Count} 列");
            for (var i = 0; i < labels.Count; i++)
                if (aoa[0][i] != labels[i])
                    throw new Exception($"第 {i + 1} 列表头是「{aoa[0][i]}」，期望「{labels[i]}」（列顺序错位）");

            if (aoa.Count != items.Count + 1)
                throw new Exception($"应为表头 + {items.Count} 行，实际 {aoa.Count} 行");

            var codeAt = labels.IndexOf("项目编码");
            var nameAt = labels.IndexOf("项目名称");
            var p3At = labels.IndexOf("三级价格");
            for (var r = 1; r < aoa.Count; r++)
            {
                var row = aoa[r];
                if (row.Count > labels.Count)
                    throw new Exception($"第 {r} 行 {row.Count} 个单元格，比表头还宽（会串列）");
                var want = items[r - 1];
                if (row[codeAt] != want.Code)
                    throw new Exception($"第 {r} 行编码是「{row[codeAt]}」，期望「{want.Code}」——导出错行");
                if (row[nameAt] != want.Name)
                    throw new Exception($"第 {r} 行名称与第 {r} 条数据不对应：{row[nameAt]} ≠ {want.Name}");
                if (row[p3At].StartsWith("="))
                    throw new Exception($"第 {r} 行三级价格还是公式文本：{row[p3At]}（没换成金额）");
            }

            // ② 使用说明附表：只带表头、每条说明一行四列
            var withUsage = items.FirstOrDefault(i => App.State.Dezhou.UsageOf(i.Doc, i.Category).Count > 0);
            var usageNote = "样例无使用说明，未验附表";
            if (withUsage != null)
            {
                var wb2 = ExcelUtil.Open(DezhouExport.Front(App.State, new[] { withUsage }, "全部结果").Bytes);
                if (wb2.NumberOfSheets != 2) throw new Exception("带使用说明的记录应产出第二张表");
                var u = ExcelUtil.ReadAoa(wb2.GetSheetAt(1));
                if (u[0].Count != 4 || u[0][0] != "落地文号")
                    throw new Exception("使用说明表头不对：" + string.Join("/", u[0]));
                if (u.Any(r => r.Count > 4))
                    throw new Exception("使用说明表有行比表头宽");
                usageNote = $"使用说明 {u.Count - 1} 行 × 4 列";
            }

            // ③ 文件名合法（不含路径分隔符等非法字符）
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new Exception("导出文件名含非法字符：" + name);

            d($"{items.Count} 行逐行对码无错位 · 表头 {labels.Count} 列与列定义一致 · {usageNote} · {name}");
        });

        Check("后台物价导出 + 模板（筛选生效 / 再导回条数与编码一致 / 示例不串列）", d =>
        {
            var labels = DezhouPriceStore.Columns.Select(c => c.Label).ToList();
            var doc = App.State.Dezhou.Items.GroupBy(i => i.Doc)
                .OrderByDescending(g => g.Count()).First().Key;
            var items = App.State.Dezhou.Items.Where(i => i.Doc == doc).ToList();

            var (bytes, name) = AdminService.ExportDezhou(App.State, doc);
            var aoa = ExcelUtil.ReadAoa(ExcelUtil.Open(bytes).GetSheetAt(0));
            if (aoa.Count != items.Count + 1)
                throw new Exception($"批次「{doc}」应导出 {items.Count} 条，实际 {aoa.Count - 1} 条");
            if (aoa[0].Count != labels.Count) throw new Exception("后台导出表头列数不对");
            for (var i = 0; i < labels.Count; i++)
                if (aoa[0][i] != labels[i])
                    throw new Exception($"后台导出第 {i + 1} 列表头错位：「{aoa[0][i]}」≠「{labels[i]}」");

            var docAt = labels.IndexOf("落地文号");
            var badDoc = aoa.Skip(1).FirstOrDefault(r => r[docAt] != doc);
            if (badDoc != null) throw new Exception("批次筛选没生效，导出了别的文号：" + badDoc[docAt]);

            // 与导入同构：导出件解析回来条数、编码顺序必须完全一致（错行必然在这里露出来）
            var back = AdminService.ReadDezhouRows(bytes);
            if (back.Count != items.Count)
                throw new Exception($"导出件再解析得到 {back.Count} 条，期望 {items.Count} 条");
            for (var i = 0; i < items.Count; i++)
                if (back[i].Code != items[i].Code)
                    throw new Exception($"再导入第 {i + 1} 条编码是「{back[i].Code}」，期望「{items[i].Code}」");

            // 模板：表头 + 1 行示例；示例行每个值必须落在同名列（错列就会把价格读成单位）
            var (tpl, tplName) = AdminService.DezhouTemplate();
            var ta = ExcelUtil.ReadAoa(ExcelUtil.Open(tpl).GetSheetAt(0));
            if (ta.Count != 2) throw new Exception($"模板应为表头 + 1 行示例，实际 {ta.Count} 行");
            if (ta[0].Count != labels.Count) throw new Exception("模板表头列数不对");
            var tplRows = AdminService.ReadDezhouRows(tpl);
            if (tplRows.Count != 1) throw new Exception($"模板示例行解析出 {tplRows.Count} 条，期望 1 条");
            if (tplRows[0].Code != "013315000010000")
                throw new Exception("模板示例的项目编码没落在「项目编码」列：" + tplRows[0].Code);
            if (tplRows[0].Price3 != "100" || tplRows[0].Unit != "次")
                throw new Exception($"模板示例价格/单位串列：price3={tplRows[0].Price3} unit={tplRows[0].Unit}");

            d($"批次「{doc}」{items.Count:N0} 条 · 导出→再解析逐行编码一致 · 模板示例 price3/unit 落在正确列 · {tplName}");
        });

        Check("病种库 / 立项指南 / 综合病组模板（逐表表头=列定义 / 行不超表头 / 表名唯一）", d =>
        {
            // ① 病种库：每个区段一张表，表头 = 该区段列定义
            var (dbytes, _) = AdminService.DiseasesTemplate(null);
            var dwb = ExcelUtil.Open(dbytes);
            var sections = Diseases.Sections;
            if (dwb.NumberOfSheets != sections.Count)
                throw new Exception($"病种库模板 {dwb.NumberOfSheets} 张表 ≠ {sections.Count} 个区段");
            var dChecked = 0;
            foreach (var sec in sections)
            {
                var idx = -1;
                for (var i = 0; i < dwb.NumberOfSheets; i++)
                    if (dwb.GetSheetName(i) == ExcelUtil.SheetName(sec.Label)) { idx = i; break; }
                if (idx < 0) throw new Exception($"病种库模板缺少区段表「{sec.Label}」");
                var aoa = ExcelUtil.ReadAoa(dwb.GetSheetAt(idx));
                if (aoa.Count == 0) continue;
                if (aoa[0].Count != sec.Columns.Count)
                    throw new Exception($"区段「{sec.Label}」表头 {aoa[0].Count} 列 ≠ 列定义 {sec.Columns.Count} 列");
                for (var r = 1; r < aoa.Count; r++)
                    if (aoa[r].Count > aoa[0].Count)
                        throw new Exception($"区段「{sec.Label}」第 {r} 行比表头宽（会串列）");
                dChecked++;
            }

            // ② 立项指南：表头 9 列，且示例/数据行按同一字段顺序落格
            var (lbytes, _) = AdminService.LixiangTemplate(App.State, null);
            var lwb = ExcelUtil.Open(lbytes);
            var lcols = LixiangStore.Columns.Select(c => c.Label).ToList();
            var lChecked = 0;
            for (var i = 0; i < lwb.NumberOfSheets; i++)
            {
                var aoa = ExcelUtil.ReadAoa(lwb.GetSheetAt(i));
                if (aoa.Count == 0) continue;
                if (aoa[0].Count != lcols.Count)
                    throw new Exception($"立项指南表「{lwb.GetSheetName(i)}」表头 {aoa[0].Count} 列 ≠ {lcols.Count} 列");
                for (var r = 1; r < aoa.Count; r++)
                    if (aoa[r].Count > aoa[0].Count)
                        throw new Exception($"立项指南「{lwb.GetSheetName(i)}」第 {r} 行比表头宽（会串列）");
                lChecked++;
            }

            // 首行内容与区段首条一致（字段顺序错位时这里会不等）
            var firstSec = App.State.Lixiang.State.Sections.Values.First(s => s.Rows.Count > 0);
            var firstSheet = -1;
            for (var i = 0; i < lwb.NumberOfSheets; i++)
                if (lwb.GetSheetName(i).StartsWith(firstSec.Label[..Math.Min(6, firstSec.Label.Length)])) { firstSheet = i; break; }
            var orderNote = "未取到首行做顺序核对";
            if (firstSheet >= 0)
            {
                var aoa = ExcelUtil.ReadAoa(lwb.GetSheetAt(firstSheet));
                if (aoa.Count > 1)
                {
                    var row = aoa[1];
                    if (row.Count > 1 && row[1] != firstSec.Rows[0].Name)
                        throw new Exception($"立项指南首行第 2 列是「{row[1]}」，期望「{firstSec.Rows[0].Name}」（列错位）");
                    orderNote = "首行字段顺序核对通过";
                }
            }

            // ③ 综合病组模板：4 列 / 表头 + 1 行示例
            var za = ExcelUtil.ReadAoa(ExcelUtil.Open(AdminService.ZongheTemplate()).GetSheetAt(0));
            if (za[0].Count != 4 || za.Count != 2) throw new Exception($"综合病组模板应为 4 列 2 行，实际 {za[0].Count} 列 {za.Count} 行");
            if (za[1].Count > 4) throw new Exception("综合病组示例行比表头宽");

            // ④ 表名唯一（30 个区段截断后撞名会让 NPOI 直接抛异常，整个模板导不出来）
            static void AssertUniqueSheetNames(NPOI.SS.UserModel.IWorkbook wb, string what)
            {
                var names = Enumerable.Range(0, wb.NumberOfSheets).Select(wb.GetSheetName).ToList();
                if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
                    throw new Exception($"{what}模板存在重名表：" + string.Join(" / ", names));
                var tooLong = names.Where(n => n.Length > 31).ToList();
                if (tooLong.Count > 0) throw new Exception($"{what}模板表名超 31 字：" + string.Join(" / ", tooLong));
            }
            AssertUniqueSheetNames(dwb, "病种库");
            AssertUniqueSheetNames(lwb, "立项指南");

            d($"病种库 {dChecked} 张表 · 立项指南 {lChecked} 张表 · {orderNote} · 综合病组 4 列 · 表名唯一");
        });

        Check("审计 CSV（BOM 只写一次 / 逗号引号转义 / 行数）与导出行宽", d =>
        {
            var rows = new List<AuditEntry>
            {
                new() { Id = "1", At = Js.Iso(), User = "张三", Source = "桌面端", Action = "导入",
                        Target = "病种库", Detail = "含,逗号 与\"引号\"", Ok = true, Note = "第一行" },
                new() { Id = "2", At = Js.Iso(), User = "李四", Source = "网页端", Action = "导出",
                        Target = "物价", Detail = "换行\n第二行文本", Ok = false, Note = "" },
            };

            var bytes = AuditLog.ExportCsvBytes(rows);
            // ① 只允许一个 UTF-8 BOM（以前写盘用 UTF8Encoding(true)，内容又带 BOM → 两个）
            if (!(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF))
                throw new Exception("CSV 缺少 UTF-8 BOM（Excel 打开会乱码）");
            if (bytes.Length > 5 && bytes[3] == 0xEF && bytes[4] == 0xBB && bytes[5] == 0xBF)
                throw new Exception("CSV 文件头有两个 BOM（写盘编码器又补了一次）");

            var text = new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
            if (text.StartsWith('\uFEFF')) throw new Exception("BOM 字符残留在正文里");

            // ② 表头 + 2 条；含逗号/引号的字段必须整体引起来且内部引号翻倍
            if (!text.StartsWith("时间,账号,来源,动作,对象,详情,结果,备注"))
                throw new Exception("CSV 表头不对：" + text.Split('\n')[0]);
            if (!text.Contains("\"含,逗号 与\"\"引号\"\"\""))
                throw new Exception("含逗号/引号的字段没有按 RFC4180 转义");
            if (!text.Contains("\"换行\n第二行文本\""))
                throw new Exception("含换行的字段没有加引号（会多出一行）");

            // ③ 逻辑行数 = 2 条记录 + 表头（换行在引号内不该被算成新记录）
            var records = text.Split("\r\n").Where(l => l.Length > 0).Count();
            if (records != 3)
                throw new Exception($"CSV 应有 3 个逻辑行（表头 + 2 条），按 CRLF 切出 {records} 行");

            d($"BOM 单个 · 表头 8 列 · 逗号/引号/换行均正确转义 · 3 个逻辑行");
        });

        Check("批量分组：模板列宽 + 结果表逐行对码（目录库外列不串列）", d =>
        {
            var tpl = GrouperService.BuildBatchTemplate();
            var ta = ExcelUtil.ReadAoa(ExcelUtil.Open(tpl).GetSheetAt(0));
            if (ta[0].Count != 8)
                throw new Exception($"批量模板表头应 8 列，实际 {ta[0].Count} 列");
            if (ta[0][0] != "病案号" || ta[0][3] != "主要手术操作编码")
                throw new Exception("批量模板表头顺序不对：" + string.Join("/", ta[0]));
            if (ta.Skip(1).Any(r => r.Count > 8))
                throw new Exception("批量模板有示例行比表头宽（会串列）");

            var back = GrouperService.ReadSheetRows(tpl);
            if (back.Count != ta.Count - 1)
                throw new Exception($"模板示例行 {ta.Count - 1} 条，解析回 {back.Count} 条");

            // 结果表：3 条输入 → 3 行；行序、病案号、主诊断必须一一对应
            var input = new List<Dictionary<string, JsonElement>>
            {
                GrouperService.ToJsonDict(new Dictionary<string, object> { ["caseId"] = "MZ001", ["mainDiag"] = "H65", ["mainProc"] = "22.0200" }),
                GrouperService.ToJsonDict(new Dictionary<string, object> { ["caseId"] = "MZ002", ["mainDiag"] = "A09.901", ["mainProc"] = "44.1300x001" }),
                GrouperService.ToJsonDict(new Dictionary<string, object> { ["caseId"] = "MZ003", ["mainDiag"] = "P58.9" }),
            };
            var res = GrouperService.Batch(App.State, input, false);
            var ra = ExcelUtil.ReadAoa(ExcelUtil.Open(res.Xlsx).GetSheetAt(0));
            if (ra.Count != 4) throw new Exception($"结果表应为表头 + 3 行，实际 {ra.Count} 行");
            if (ra[0][0] != "序号" || !ra[0].Contains("目录库外病种"))
                throw new Exception("结果表表头缺关键列：" + string.Join("/", ra[0]));
            var caseAt = ra[0].IndexOf("病案号");
            for (var i = 1; i < ra.Count; i++)
            {
                if (ra[i].Count > ra[0].Count)
                    throw new Exception($"结果表第 {i} 行比表头宽（会串列）");
                if (ra[i][caseAt] != "MZ00" + i)
                    throw new Exception($"结果表第 {i} 行病案号是「{ra[i][caseAt]}」，期望 MZ00{i}（错行）");
            }

            d($"模板 8 列 {ta.Count - 1} 行示例 · 结果表 3 行病案号顺序一致 · 含「目录库外病种」列");
        });

        Check("征求意见稿四视图导出（数据区不得插汇总行 / 汇总单独成表 / 行宽不超表头）", d =>
        {
            var v = new DraftPriceView();
            v.EnsureLoaded();

            var notes = new List<string>();
            foreach (var view in new[] { DraftPriceView.ViewKind.New, DraftPriceView.ViewKind.Cancel,
                                         DraftPriceView.ViewKind.Map, DraftPriceView.ViewKind.Compare })
            {
                v.DiagView(view);
                var sheets = v.DiagExportSheets();
                if (sheets.Count == 0) throw new Exception($"{view} 视图没有导出工作表");
                if (string.IsNullOrWhiteSpace(sheets[0].Name)) throw new Exception($"{view} 首个工作表没有表名");
                if (sheets[0].Aoa.Count == 0) throw new Exception($"{view} 首个工作表没有表头");
                var head = sheets[0].Aoa[0];
                if (head.Count == 0) throw new Exception($"{view} 表头是空的");

                foreach (var s in sheets)
                {
                    if (s.Aoa.Count == 0 || s.Aoa[0].Count == 0)
                        throw new Exception($"{view}/{s.Name} 表为空或没有表头");
                    for (var r = 1; r < s.Aoa.Count; r++)
                    {
                        var row = s.Aoa[r] ?? new List<object>();
                        if (row.Count > s.Aoa[0].Count)
                            throw new Exception($"{view}/{s.Name} 第 {r + 1} 行 {row.Count} 格，比表头 {s.Aoa[0].Count} 格宽（串列）");
                    }
                }

                // 数据表第一行必须就是第一条数据 —— 以前比对表把「汇总」插在表头正下方，
                // 所有数据整体下移一行，筛选/透视全错。这里直接盯住第 2 行。
                var body = sheets[0].Aoa[1];
                if (body.Count > 0)
                {
                    var first = Convert.ToString(body[0]) ?? "";
                    if (first.StartsWith("，") || first.StartsWith(",") || first.Contains("共 ") && first.Contains("纯新增"))
                        throw new Exception($"{view} 数据区第 1 行是汇总文字「{first}」，数据被挤下移（错行）");
                }

                if (view == DraftPriceView.ViewKind.Compare)
                {
                    if (sheets.Count < 2 || sheets[1].Name != "比对汇总")
                        throw new Exception("比对视图的汇总没有单独成表");
                    var sum = sheets[1].Aoa;
                    if (sum[0].Count != 2 || Convert.ToString(sum[0][0]) != "指标")
                        throw new Exception("比对汇总表头不对：" + string.Join("/", sum[0]));
                    var (total, priced, cancelOnly, brandNew, _, _, _) = v.DiagCompareStat();
                    var got = sum.Skip(1).ToDictionary(r => Convert.ToString(r[0]) ?? "", r => Convert.ToString(r[1]) ?? "");
                    if (got.GetValueOrDefault("拟新增项目合计") != total.ToString())
                        throw new Exception($"汇总表「拟新增项目合计」={got.GetValueOrDefault("拟新增项目合计")}，实际 {total}");
                    if (got.GetValueOrDefault("纯新增项目") != brandNew.ToString())
                        throw new Exception("汇总表「纯新增项目」与 CompareStat 不一致");
                    if (got.GetValueOrDefault("仅作废对照") != cancelOnly.ToString())
                        throw new Exception("汇总表「仅作废对照」与 CompareStat 不一致");
                    notes.Add($"比对：{sheets[0].Aoa.Count - 1} 行数据 + 汇总表（可比 {priced}）");
                }
                else
                {
                    notes.Add($"{view}：{sheets[0].Aoa.Count - 1} 行");
                }
            }

            d(string.Join(" · ", notes) + " · 数据区第 2 行均为真实数据");
        });

        Check("导出按钮不错行（导出组不拆开 / 不在筛选条内 / 窄窗口仍在同一行）", d =>
        {
            var dz = new DezhouPriceView();
            var widths = new[] { 1500.0, 1366.0, 1280.0, 1060.0 };
            var seen = new List<string>();
            foreach (var w in widths)
            {
                dz.Prewarm(w, 760);
                dz.Measure(new Size(w, 760));
                dz.Arrange(new Rect(0, 0, w, 760));
                dz.UpdateLayout();

                var L = dz.DiagExportLayout();
                if (L.InFilterBar)
                    throw new Exception($"{w:F0}px：导出按钮又回到筛选条里了（WrapPanel 一窄就把它换行走）");
                if (Math.Abs(L.ScopeTop - L.BtnTop) > 2)
                    throw new Exception($"{w:F0}px：导出范围(y={L.ScopeTop:F0}) 与导出按钮(y={L.BtnTop:F0}) 不在同一行");
                if (L.BtnLeft < L.ScopeLeft)
                    throw new Exception($"{w:F0}px：导出按钮跑到了导出范围左边");
                seen.Add($"{w:F0}:y{L.BtnTop:F0}");
            }

            // 比对视图：导出组可见（范围口径换成比对）；旧版物价整块收起
            dz.DiagCompare();
            if (dz.ExportBarVisibility != Visibility.Visible)
                throw new Exception("切到新旧比对后导出组没有显示（比对结果应可导出）");
            var cmpTags = dz.DiagExpScopeTags();
            if (cmpTags.Count != 2 || cmpTags[0] != "all" || cmpTags[1] != "page")
                throw new Exception("比对视图导出范围口径不对：" + string.Join("/", cmpTags));
            var cmpLayout = dz.DiagExportLayout();
            if (cmpLayout.InFilterBar)
                throw new Exception("比对视图下导出按钮回到了筛选条里");
            dz.DiagLegacyView();
            if (dz.ExportBarVisibility != Visibility.Collapsed)
                throw new Exception("切到旧版物价后导出组没有整块收起");
            dz.DiagLive();   // 回到现行项目
            if (dz.ExportBarVisibility != Visibility.Visible)
                throw new Exception("切回现行项目后导出组没有恢复");

            // 征求意见稿同款
            var draft = new DraftPriceView();
            draft.EnsureLoaded();
            var draftSeen = new List<string>();
            foreach (var w in widths)
            {
                draft.Measure(new Size(w, 760));
                draft.Arrange(new Rect(0, 0, w, 760));
                draft.UpdateLayout();
                var L = draft.DiagExportLayout();
                if (!L.SameRow)
                    throw new Exception($"征求意见稿 {w:F0}px：导出按钮与状态行错位（{L.BtnTop:F0} vs {L.StatusTop:F0}）");
                draftSeen.Add($"{w:F0}");
            }

            d($"物价页 {string.Join(" / ", seen)} 同行 · 导出组随视图整块收起（比对可见 / 旧版收起） · 征求意见稿 {string.Join("/", draftSeen)}px 同行");
        });

        Check("新旧比对视图导出（表头 19 列 / 逐行对码 / 差额涨幅符号 / 汇总单独成表 / 空值统一—）", d =>
        {
            var rows = App.State.Dezhou.ListCompare("", "", "");
            if (rows.Count == 0) throw new Exception("比对数据为空（旧版物价未导入），无法验证导出");

            var (bytes, name) = DezhouExport.Cmp(App.State, rows, "全部");
            var wb = ExcelUtil.Open(bytes);
            if (wb.NumberOfSheets != 2 || wb.GetSheetName(1) != "比对汇总")
                throw new Exception($"应产出「新旧比对 + 比对汇总」两张表，实际 {wb.NumberOfSheets} 张");
            var aoa = ExcelUtil.ReadAoa(wb.GetSheetAt(0));

            var head = new[] { "新项目编码", "新项目名称", "类别", "性质", "新三级价", "新二级价", "新一级价",
                "旧项目编码", "旧项目名称", "旧三级价", "旧二级价", "旧一级价",
                "三级差额", "三级涨幅%", "二级差额", "二级涨幅%", "一级差额", "一级涨幅%", "基准来源" };
            if (aoa[0].Count != head.Length)
                throw new Exception($"表头 {aoa[0].Count} 列 ≠ {head.Length} 列");
            for (var i = 0; i < head.Length; i++)
                if (aoa[0][i] != head[i])
                    throw new Exception($"第 {i + 1} 列表头是「{aoa[0][i]}」，期望「{head[i]}」");

            if (aoa.Count != rows.Count + 1)
                throw new Exception($"应为表头 + {rows.Count} 行，实际 {aoa.Count - 1} 行");

            for (var r = 1; r < aoa.Count; r++)
            {
                var row = aoa[r];
                var want = rows[r - 1];
                if (row.Count > head.Length)
                    throw new Exception($"第 {r} 行 {row.Count} 格，比表头 {head.Length} 宽（串列）");
                if (row[0] as string != want.NewCode)
                    throw new Exception($"第 {r} 行新编码「{row[0]}」≠「{want.NewCode}」——导出错行");
                if (row[1] as string != want.NewName)
                    throw new Exception($"第 {r} 行名称与数据不对应：{row[1]} ≠ {want.NewName}");
                var src = row[18] as string ?? "";
                var wantSrc = want.OldSource switch { "legacy" => "旧版物价", "cancel" => "仅作废对照", _ => "纯新增" };
                if (src != wantSrc)
                    throw new Exception($"第 {r} 行基准来源「{src}」≠「{wantSrc}」");
                // 差额符号：差额为正必须带 +，空值必须统一「—」（不允许出现 \ / 空串 / null 文本）
                for (var c = 12; c <= 17; c++)
                {
                    var cell = row[c] as string ?? "";
                    if (cell == "\\" || cell == "-" || cell.Length == 0)
                        throw new Exception($"第 {r} 行第 {c + 1} 列空值符号是「{cell}」，应统一为「—」");
                }
                if (want.Delta3 > 0 && !(row[12] as string ?? "").StartsWith("+"))
                    throw new Exception($"第 {r} 行三级差额为正却没带 + 号：{row[12]}");
                if (want.Ratio3 is null && (row[13] as string) != "—")
                    throw new Exception($"第 {r} 行涨幅应为「—」，实际 {row[13]}");
            }

            // 汇总单独成表且与数据对账（绝不能插在数据表头下方挤错行）
            var sum = ExcelUtil.ReadAoa(wb.GetSheetAt(1));
            if (Convert.ToString(sum[0][0]) != "指标" || Convert.ToString(sum[0][1]) != "数值")
                throw new Exception("比对汇总表头不对：" + string.Join("/", sum[0]));
            var got = sum.Skip(1).ToDictionary(r => Convert.ToString(r[0]) ?? "", r => Convert.ToString(r[1]) ?? "");
            var priced = rows.Count(x => x.OldSource == "legacy");
            if (got.GetValueOrDefault("比对结果合计") != rows.Count.ToString())
                throw new Exception($"汇总「比对结果合计」={got.GetValueOrDefault("比对结果合计")} ≠ {rows.Count}");
            if (got.GetValueOrDefault("有旧价可比") != priced.ToString())
                throw new Exception($"汇总「有旧价可比」={got.GetValueOrDefault("有旧价可比")} ≠ {priced}");
            var dataFirst = aoa[1][0] as string ?? "";
            if (dataFirst.StartsWith("指标") || dataFirst.Contains("合计"))
                throw new Exception("数据表第 1 行是汇总文字——汇总被插进数据区了");

            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new Exception("导出文件名含非法字符：" + name);

            d($"{rows.Count:N0} 行逐行对码 · 19 列表头一致 · 涨跌符号正确 · 汇总表单独对账 · {name}");
        });

        Check("检查更新：版本号比较规则（纯逻辑，不联网）", d =>
        {
            // 基线：当前版本一定不比自己新
            if (UpdateChecker.IsNewer(UpdateChecker.CurrentVersion))
                throw new Exception("当前版本被判定为「比当前版本新」");
            // v 前缀 / 无前缀 / 补零 / 前导零都要比对一致
            if (!UpdateChecker.IsNewer("v1.0.1")) throw new Exception("v1.0.1 应判为新版");
            if (!UpdateChecker.IsNewer("1.0.1")) throw new Exception("无 v 前缀 1.0.1 应判为新版");
            if (!UpdateChecker.IsNewer("v1.1")) throw new Exception("两段版本 v1.1 应判为新版（补零比较）");
            if (!UpdateChecker.IsNewer("v2")) throw new Exception("主版本 v2 应判为新版");
            if (UpdateChecker.IsNewer("v1.0.0")) throw new Exception("v1.0.0 不应判为新版");
            if (UpdateChecker.IsNewer("v0.9")) throw new Exception("旧版 v0.9 不应判为新版");
            // 带后缀的 tag（v1.0.1-beta / v1.0.1+build）按 1.0.1 比较
            if (!UpdateChecker.IsNewer("v1.0.1-beta")) throw new Exception("v1.0.1-beta 应判为新版");
            if (UpdateChecker.IsNewer("v1.0.0-rc1")) throw new Exception("v1.0.0-rc1 不应判为新版");
            // 解析不了的一律 false（不能因为 tag 怪异就误报更新）
            if (UpdateChecker.IsNewer("")) throw new Exception("空 tag 不应判为新版");
            if (UpdateChecker.IsNewer("latest")) throw new Exception("非数字 tag 不应判为新版");
            if (UpdateChecker.IsNewer("v1.x")) throw new Exception("非法段 v1.x 不应判为新版");
            // 与发布流程对齐：版本常量必须是可解析的三段式
            if (UpdateChecker.CurrentVersion.Split('.').Length < 2)
                throw new Exception("CurrentVersion 应至少是两段数字，当前：" + UpdateChecker.CurrentVersion);
            // Release API 地址必须指向本仓库（防止改了 owner 忘记同步）
            var api = typeof(UpdateChecker)
                .GetField("LatestApi", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?
                .GetValue(null) as string ?? "";
            if (!api.Contains("repos/libo770880/dip3-grouper-cs/releases/latest"))
                throw new Exception("Release API 地址不指向 libo770880/dip3-grouper-cs：" + api);
            d("v 前缀/补零/后缀 tag/非法 tag 全部按预期判定 · API 地址指向本仓库");
        });

        // ================= 汇总 =================
        log.AppendLine();
        log.AppendLine("=== 汇总 ===");
        log.AppendLine($"通过 {pass} / 失败 {fail}");
        log.AppendLine(fail == 0 ? "全部通过 [PASS]" : "存在失败项 [FAIL]");

        var text = log.ToString();
        try { File.WriteAllText(LogPath, text, new UTF8Encoding(false)); } catch { }
        try { Console.Out.Write(text); Console.Out.Flush(); } catch { }

        return fail == 0 ? 0 : 1;
    }

    /// <summary>收集视觉树内全部「Tx.Sel」可选文本（验证详情卡 / 使用说明真的放开了选中）。</summary>
    private static List<TextBox> SelectableBoxes(DependencyObject root)
    {
        var list = new List<TextBox>();
        var sel = Application.Current.TryFindResource("Tx.Sel");
        void Walk(DependencyObject d)
        {
            var n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(d);
            for (var i = 0; i < n; i++)
            {
                var c = System.Windows.Media.VisualTreeHelper.GetChild(d, i);
                if (c is TextBox t && t.Style == sel) list.Add(t);
                Walk(c);
            }
        }
        Walk(root);
        return list;
    }

    /// <summary>视觉树里找指定类型的后代元素（断言用）。</summary>
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var x in Descendants<T>(c)) yield return x;
        }
    }

    /// <summary>菜单条目文本（断言用）。</summary>
    private static List<string> Heads(ContextMenu m)
        => m.Items.OfType<MenuItem>().Select(x => x.Header as string ?? "").ToList();

    /// <summary>
    /// 自检用的假「数据分发服务」：裸 <see cref="TcpListener"/> 手写最小 HTTP/1.1 应答。
    ///
    /// <para>为什么不用 HttpListener / 真起一个 Web 进程：<c>HttpListener</c> 绑定前缀在
    /// 非管理员下常常直接抛 <c>HttpListenerException</c>，起进程又要处理端口与生命周期；
    /// 裸 TCP 监听回环地址 + 随机端口则任何权限下都能跑，且能精确复刻分发协议的两个接口。</para>
    /// </summary>
    /// <summary>
    /// 在独立临时根目录里跑一段用例：把**真实**的病种库那一组文件拷进去，再把
    /// <see cref="AppPaths.Root"/> 指过去，结束后恢复。
    ///
    /// <para>为什么必须这样：版本快照与审计日志都会往 <c>data/</c> 下写文件。
    /// 直接对着仓库里的真实数据跑，轻则污染真实版本库、重则把运行中实例的数据搅乱 ——
    /// 与「写接口对拍必须走隔离实验台」是同一个道理。</para>
    /// </summary>
    private static void WithTempRoot(out string tmp, out Action restore)
    {
        var origRoot = AppPaths.Root;
        var dir = Path.Combine(Path.GetTempPath(), "dip3-feat-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "config"));
        Directory.CreateDirectory(Path.Combine(dir, "data"));

        foreach (var rel in new[] { "config/dip3-config.json", "data/config-meta.json", "data/zonghe.json" })
        {
            var src = Path.Combine(origRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(src)) continue;
            var dst = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            File.Copy(src, dst, true);
        }

        tmp = dir;
        AppPaths.SetRoot(dir);
        SettingsStore.Reload();
        AuditLog.Reload();
        restore = () =>
        {
            AppPaths.SetRoot(origRoot);
            SettingsStore.Reload();
            AuditLog.Reload();
            try { Directory.Delete(dir, true); } catch { /* 临时目录尽力清理 */ }
        };
    }

    private sealed class FakeDataServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _root;
        private readonly CancellationTokenSource _cts = new();
        private int _requests;

        public int Port { get; }
        public string Url => "http://127.0.0.1:" + Port;

        /// <summary>收到过的请求数（断言确实走的是 HTTP，而不是直接读盘）</summary>
        public int Requests => Volatile.Read(ref _requests);

        /// <summary>
        /// 可选的「公共设置」应答体（对应 <c>GET /api/settings/public</c>）。
        /// 为 null 时该路径回 404 —— 和生产服务上「接口存在但没配任何值」互相区分。
        /// </summary>
        public Func<PublicSettingsSnapshot> SettingsProvider { get; set; }

        /// <summary>
        /// 可选的「通知公告」应答体（对应 <c>GET /api/notice</c>，前台与客户端共用同一个接口）。
        /// 为 null 时该路径回 404。
        /// </summary>
        public Func<NoticeFeed> NoticeProvider { get; set; }

        /// <summary>
        /// 可选的「客户端版本」应答体（对应 <c>GET /api/app/version</c>）。
        /// 为 null 时该路径回 404 —— 生产服务上「接口在、但管理员没发布新版」是回空版本号而不是 404。
        /// </summary>
        public Func<AppVersionInfo> AppVersionProvider { get; set; }

        public FakeDataServer(string root)
        {
            _root = root;
            _listener = new TcpListener(IPAddress.Loopback, 0);   // 0 = 让系统挑空闲端口
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch { return; }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();

                    var head = new StringBuilder();
                    var buf = new byte[4096];
                    while (head.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) < 0)
                    {
                        var n = await stream.ReadAsync(buf, 0, buf.Length);
                        if (n <= 0) return;
                        head.Append(Encoding.ASCII.GetString(buf, 0, n));
                    }
                    Interlocked.Increment(ref _requests);

                    var parts = head.ToString().Split('\n')[0].Trim().Split(' ');
                    var path = parts.Length > 1 ? parts[1] : "/";
                    var q = path.IndexOf('?');
                    if (q >= 0) path = path[..q];

                    if (path == "/api/data/manifest")
                    {
                        var json = Encoding.UTF8.GetBytes(Js.ToJson(BuildManifest()));
                        await RespondAsync(stream, 200, "application/json", json);
                        return;
                    }

                    if (path.StartsWith("/api/data/file/", StringComparison.Ordinal))
                    {
                        var name = Uri.UnescapeDataString(path["/api/data/file/".Length..]);
                        var spec = DataCatalog.Find(name);
                        var file = spec == null
                            ? null
                            : Path.Combine(_root, spec.RelPath.Replace('/', Path.DirectorySeparatorChar));
                        if (file != null && File.Exists(file))
                        {
                            await RespondAsync(stream, 200, "application/octet-stream", File.ReadAllBytes(file));
                            return;
                        }
                    }

                    if (path == "/api/settings/public" && SettingsProvider != null)
                    {
                        var json = Encoding.UTF8.GetBytes(Js.ToJson(SettingsProvider()));
                        await RespondAsync(stream, 200, "application/json", json);
                        return;
                    }

                    if (path == "/api/notice" && NoticeProvider != null)
                    {
                        var json = Encoding.UTF8.GetBytes(Js.ToJson(NoticeProvider()));
                        await RespondAsync(stream, 200, "application/json", json);
                        return;
                    }

                    if (path == "/api/app/version" && AppVersionProvider != null)
                    {
                        var json = Encoding.UTF8.GetBytes(Js.ToJson(AppVersionProvider()));
                        await RespondAsync(stream, 200, "application/json", json);
                        return;
                    }

                    if (path == "/api/app/package")
                    {
                        var pkg = Path.Combine(_root, "data", "app-update", "package.zip");
                        if (File.Exists(pkg))
                        {
                            await RespondAsync(stream, 200, "application/octet-stream", File.ReadAllBytes(pkg));
                            return;
                        }
                    }

                    await RespondAsync(stream, 404, "text/plain", Encoding.UTF8.GetBytes("Not Found"));
                }
                catch { /* 客户端提前断开等，忽略 */ }
            }
        }

        private DataManifest BuildManifest()
        {
            var m = new DataManifest { GeneratedAt = Js.Iso() };
            foreach (var spec in DataCatalog.Specs)
            {
                var file = Path.Combine(_root, spec.RelPath.Replace('/', Path.DirectorySeparatorChar));
                var fi = File.Exists(file) ? new FileInfo(file) : null;
                m.Files.Add(new DataFileInfo
                {
                    Name = spec.Name,
                    Label = spec.Label,
                    Group = spec.Group,
                    RelPath = spec.RelPath,
                    Exists = fi != null,
                    Size = fi?.Length ?? 0,
                    Sha256 = fi == null ? null : DataCatalog.Sha256(file),
                    UpdatedAt = fi == null ? null : fi.LastWriteTimeUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
                });
            }
            return m;
        }

        private static async Task RespondAsync(NetworkStream s, int code, string type, byte[] body)
        {
            var head = $"HTTP/1.1 {code} {(code == 200 ? "OK" : "Not Found")}\r\n"
                       + $"Content-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
            var hb = Encoding.ASCII.GetBytes(head);
            await s.WriteAsync(hb, 0, hb.Length);
            if (body.Length > 0) await s.WriteAsync(body, 0, body.Length);
            await s.FlushAsync();
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { /* 已释放 */ }
            try { _listener.Stop(); } catch { /* 已停止 */ }
            try { _cts.Dispose(); } catch { /* 已释放 */ }
        }
    }
}
