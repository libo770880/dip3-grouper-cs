using Dip3GrouperApp.Desktop.Views;
using Dip3Grouper;

namespace Dip3GrouperApp.Desktop;

/// <summary>跨库关联命中：Chip =「同名/相似」，Text = 名称 + 摘要。</summary>
public class CrossHit
{
    public string Chip { get; set; }
    public string Text { get; set; }

    /// <summary>命中条目所属库（guifan / dezhou / lixiang），用于点击打开详情弹窗</summary>
    public string Lib { get; set; }

    /// <summary>命中条目 Id（三库内唯一）</summary>
    public int Id { get; set; }
}

/// <summary>
/// 「德州物价 ↔ 立项指南 ↔ 项目技术规范」三库互相映射 —— 桌面端包装层。
///
/// <para>算法（归一化、二元组 Dice 相似度 0.62、top3、Gid 跳转键、数据戳重建）只有一份，
/// 在 <see cref="CrossLinker"/>（Core）。这里只负责把 <see cref="CrossLinkGroup"/>
/// 转成详情卡能直接追加的 <see cref="LxBlock"/>。</para>
///
/// <para>2026-10-30 前这段逻辑整个在桌面端，网页版因此没有「关联·立项指南」等块；
/// 下沉后两端同源，不会各算一套。</para>
/// </summary>
public static class CrossMap
{
    /// <summary>立项指南行 → 关联的德州物价 / 技术规范</summary>
    public static List<LxBlock> ForLixiang(string name)
        => Wrap(CrossLinker.ForLixiang(App.State, name));

    /// <summary>德州物价行 → 关联的立项指南 / 技术规范</summary>
    public static List<LxBlock> ForDezhou(string name)
        => Wrap(CrossLinker.ForDezhou(App.State, name));

    /// <summary>技术规范条目 → 关联的立项指南 / 德州物价</summary>
    public static List<LxBlock> ForGuifan(string name)
        => Wrap(CrossLinker.ForGuifan(App.State, name));

    /// <summary>预热索引（启动后空闲时后台调用，避免首次点开详情卡时停顿）</summary>
    public static void Warm() => CrossLinker.Warm(App.State);

    private static List<LxBlock> Wrap(List<CrossLinkGroup> groups)
        => groups.Select(g => new LxBlock
        {
            Label = g.Label,
            Lines = g.Hits.Select(h => new LxLine
            {
                Chip = h.Chip, Text = h.Text, LinkLib = h.Lib, LinkId = h.Id,
            }).ToList(),
        }).ToList();
}
