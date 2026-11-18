namespace Dip3Grouper;

/// <summary>
/// 分组溯源：按优先级逐层记录判定过程，回答「为什么分到这个病种 / 为什么没入组」。
///
/// <para><b>为什么不放进 GroupResult 的 JSON 契约</b>：<c>/api/group</c> 的返回体与 Node 版
/// 逐字节一致（见 README §6.1）是对拍基线，多一个键就破。因此溯源结果只挂在
/// <see cref="GroupResult.Trace"/> 上（<c>GroupResultConverter</c> 只写固定键，天然忽略它），
/// 由桌面端直接消费，Web 端另走 <c>/api/group-trace</c> 独立接口。</para>
/// </summary>
public class GroupTrace
{
    public List<GroupTraceStep> Steps { get; set; } = new();

    /// <summary>命中所在层（未命中为 null）</summary>
    public string HitLayer { get; set; }

    internal GroupTraceStep Add(string layer, string code)
    {
        var s = new GroupTraceStep { Layer = layer, LayerCode = code };
        Steps.Add(s);
        return s;
    }
}

/// <summary>溯源的一层（一个分组阶段）。</summary>
public class GroupTraceStep
{
    /// <summary>层名，如「先期分组」</summary>
    public string Layer { get; set; }
    /// <summary>层代码：EXCL / XQ / FZ / BX / JC / JICENG / ZH</summary>
    public string LayerCode { get; set; }
    /// <summary>hit = 命中该层；miss = 判定过但未命中；skip = 未参与判定；ok = 参与但无影响</summary>
    public string Status { get; set; } = "miss";
    /// <summary>该层规则条数</summary>
    public int RuleCount { get; set; }
    /// <summary>命中的病种序号</summary>
    public int? Seq { get; set; }
    /// <summary>命中的病种名称</summary>
    public string Name { get; set; }
    /// <summary>一句话结论</summary>
    public string Title { get; set; }
    /// <summary>判定依据（键值，逐行展示）</summary>
    public List<GroupTraceFact> Facts { get; set; } = new();

    internal GroupTraceStep Fact(string label, string value, string tone = "mute")
    {
        Facts.Add(new GroupTraceFact { Label = label, Value = value, Tone = tone });
        return this;
    }

    internal GroupTraceStep Set(string status, string title)
    {
        Status = status;
        Title = title;
        return this;
    }
}

/// <summary>溯源依据的一行（左标签 + 右值）。tone: ok / warn / mute。</summary>
public class GroupTraceFact
{
    public string Label { get; set; }
    public string Value { get; set; }
    public string Tone { get; set; } = "mute";
}
