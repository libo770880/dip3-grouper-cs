using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dip3Grouper;

// =====================================================================
// 病种库配置模型（config/dip3-config.json）
// =====================================================================

public class Dip3Config
{
    [JsonPropertyName("xianqi")] public List<XianqiRow> Xianqi { get; set; } = new();
    [JsonPropertyName("bingxiang")] public List<BingxiangRow> Bingxiang { get; set; } = new();
    [JsonPropertyName("fz_shaoshang")] public List<ShaoshangRow> FzShaoshang { get; set; } = new();
    [JsonPropertyName("fz_zhongliu")] public List<ZhongliuRow> FzZhongliu { get; set; } = new();
    [JsonPropertyName("fz_jiehe")] public List<JieheRow> FzJiehe { get; set; } = new();
    [JsonPropertyName("jiehe_resist_codes")] public List<ResistCode> JieheResistCodes { get; set; } = new();
    [JsonPropertyName("jichu")] public List<DiagRow> Jichu { get; set; } = new();
    [JsonPropertyName("excludedDiags")] public List<CodeName> ExcludedDiags { get; set; } = new();
    [JsonPropertyName("excludedProcs")] public List<ExcludedProc> ExcludedProcs { get; set; } = new();
    [JsonPropertyName("jiceng")] public List<DiagRow> Jiceng { get; set; } = new();
    /// <summary>
    /// 目录库外病种（省局反馈清单）：诊断 + 手术的组合不在 DIP3.0 核心病种目录内，
    /// 2027 年起不再作为核心病种结算。分组时命中该表即给预警并提示可用组合，
    /// 同时它本身也是病种库的一个可维护区段（tab: outlib）。
    /// </summary>
    [JsonPropertyName("outlib")] public List<OutLibRow> OutLib { get; set; } = new();
    [JsonPropertyName("_meta")] public ConfigMeta Meta { get; set; }
}

/// <summary>
/// 一条「目录库外病种」：省局反馈的 诊断+手术 组合（原表第一列即「诊断码-手术码」复合键）。
/// <para>手术码为空表示该病种只由主要诊断定义（保守治疗），与病种库其他区段的
/// 「空手术码 = 保守行」语义一致。</para>
/// </summary>
public class OutLibRow
{
    public int Seq { get; set; }
    /// <summary>主要诊断编码（原表「主要诊断编码」）</summary>
    public string DiagCode { get; set; } = "";
    public string DiagName { get; set; } = "";
    /// <summary>手术操作编码（可空 = 该病种无手术要求）</summary>
    public string ProcCode { get; set; } = "";
    public string ProcName { get; set; } = "";
    /// <summary>
    /// 目录库内同诊断的可入组组合（参考，最多 3 条，用「；」分隔）。
    /// 由导入时按当前病种库算好并落盘，后台可人工改写。
    /// </summary>
    public string Suggest { get; set; } = "";
    /// <summary>备注（2027 年起不作核心病种的原因 / 无可用组合时的说明）</summary>
    public string Note { get; set; } = "";
}

public class ConfigMeta
{
    [JsonPropertyName("version")] public string Version { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; }
    [JsonPropertyName("counts")] public Dictionary<string, int> Counts { get; set; }
}

public class XianqiRow
{
    public int Seq { get; set; }
    public string Name { get; set; } = "";
    public List<string> DiagCodes { get; set; } = new();
    public List<List<string>> ProcAlts { get; set; } = new();
    // 以下三键在国家配置中每行都存在（值可为 null），故强制写出以保持键存在性一致
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int? AgeDaysMax { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int? BirthWeightMin { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int? BirthWeightMax { get; set; }
    public string Note { get; set; } = "";
    /// <summary>
    /// 仅新生儿体重档行含该键，其余行省略（值 null）。
    /// 注意: 必须排在 Note 之后 —— Node 侧 build-config.js 是给已构造好的 row 对象
    /// **追加** birthWeightNote，故 JSON.stringify 输出中该键位于 note 之后；
    /// 属性顺序若不同会导致 config/dip3-config.json 出现字节级差异。
    /// </summary>
    public string BirthWeightNote { get; set; }
}

public class BingxiangRow
{
    public int Seq { get; set; }
    public List<string> DiagCodes { get; set; } = new();
    public string DiagName { get; set; } = "";
    public List<string> ProcCodes { get; set; } = new();
    public string ProcName { get; set; } = "";
    public List<List<string>> RelProcs { get; set; } = new();
    public List<string> RelNames { get; set; } = new();
}

public class ShaoshangRow
{
    public int Seq { get; set; }
    public List<string> DiagCodes { get; set; } = new();
    public string Degree { get; set; } = "";
    public List<string> OtherDiagCodes { get; set; } = new();
    public string Area { get; set; } = "";
    public List<string> ProcCodes { get; set; } = new();
    public string ProcName { get; set; } = "";
    public List<List<string>> RelProcs { get; set; } = new();
    /// <summary>
    /// 相关手术名称。注意: 国家配置中仅部分行含该键，未含的行反序列化为 null，
    /// 写出时省略该键（对齐 Node 端 <c>JSON.stringify</c> 保留原键存在性的行为）。
    /// </summary>
    public List<string> RelNames { get; set; }
}

public class ZhongliuRow
{
    public int Seq { get; set; }
    public List<string> DiagCodes { get; set; } = new();
    public string DiagName { get; set; } = "";
    public List<string> OtherDiagCodes { get; set; } = new();
    public List<string> ProcAlt { get; set; } = new();
    public string ProcName { get; set; } = "";
}

public class JieheRow
{
    public int Seq { get; set; }
    public List<string> DiagCodes { get; set; } = new();
    public string DiagName { get; set; } = "";
    public List<string> ProcCodes { get; set; } = new();
    public string ProcName { get; set; } = "";
    public bool DrugResistant { get; set; }
    public string ResistNote { get; set; } = "";
}

/// <summary>诊断行（基础规则 / 基层病种通用）</summary>
public class DiagRow
{
    public int Seq { get; set; }
    public List<string> DiagCodes { get; set; } = new();
    public string DiagName { get; set; } = "";
    public List<string> ProcCodes { get; set; } = new();
    public string ProcName { get; set; } = "";
}

public class CodeName
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
}

public class ExcludedProc
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Note { get; set; } = "";
}

/// <summary>结核耐药诊断附表行：兼容字符串 或 {code,name} 对象两种形态。</summary>
[JsonConverter(typeof(ResistCodeConverter))]
public class ResistCode
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
}

public class ResistCodeConverter : JsonConverter<ResistCode>
{
    public override ResistCode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new ResistCode { Code = reader.GetString() ?? "", Name = "" };
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var rc = new ResistCode();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                var prop = reader.GetString();
                reader.Read();
                if (string.Equals(prop, "code", StringComparison.OrdinalIgnoreCase))
                    rc.Code = reader.TokenType == JsonTokenType.Null ? "" : reader.GetString() ?? "";
                else if (string.Equals(prop, "name", StringComparison.OrdinalIgnoreCase))
                    rc.Name = reader.TokenType == JsonTokenType.Null ? "" : reader.GetString() ?? "";
                else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    reader.Skip();
            }
            return rc;
        }
        reader.Skip();
        return new ResistCode();
    }

    public override void Write(Utf8JsonWriter writer, ResistCode value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("code", value?.Code ?? "");
        writer.WriteString("name", value?.Name ?? "");
        writer.WriteEndObject();
    }
}

// =====================================================================
// 综合病组（data/zonghe.json）
// =====================================================================

public class ZongheFile
{
    public List<ZongheRow> Rows { get; set; } = new();
    public ZongheMeta Meta { get; set; } = new();
}

public class ZongheRow
{
    public int Id { get; set; }
    /// <summary>可选序号（国家配置行常带 seq；后台维护行无此字段，输出时省略）</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Seq { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Ranges { get; set; } = new();
    public string Note { get; set; } = "";
}

public class ZongheMeta
{
    public string ImportedAt { get; set; }
    public string SourceName { get; set; }
}

// =====================================================================
// ICD-10 → 医保编码映射（data/mappings.json）
// =====================================================================

public class MappingFile
{
    public List<MappingRow> Rows { get; set; } = new();
    public MappingMeta Meta { get; set; } = new();
}

public class MappingRow
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string InsCode { get; set; } = "";
    public string InsName { get; set; } = "";
    public bool Gray { get; set; }
    public string Note { get; set; } = "";
}

public class MappingMeta
{
    public string ImportedAt { get; set; }
    public string SourceName { get; set; }
    public int Total { get; set; }
}

// =====================================================================
// 立项指南（data/lixiang.json）
// =====================================================================

public class LixiangFile
{
    public Dictionary<string, LixiangSection> Sections { get; set; } = new();
    public LixiangMeta Meta { get; set; } = new();
    public JieduStore Jiedu { get; set; } = new();
}

public class LixiangSection
{
    public string Label { get; set; } = "";
    public string Guide { get; set; } = "";
    public string GuideUsage { get; set; } = "";
    public int NextId { get; set; } = 1;
    public List<LixiangRow> Rows { get; set; } = new();
}

public class LixiangRow
{
    public string Seq { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Unit { get; set; } = "";
    public string ServiceOutput { get; set; } = "";
    public string PriceComposition { get; set; } = "";
    public string AddItems { get; set; } = "";
    public string ExtItems { get; set; } = "";
    public string Usage { get; set; } = "";

    /// <summary>
    /// 区段内序号（每个区段各自从 1 编，因此**跨区段不唯一**）。
    /// 编辑/删除按 (tab, Id) 定位；跨库跳转必须用 <see cref="Gid"/>。
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// 全局跳转键（内存内自增，不落盘）。
    ///
    /// <para>立项指南的 <see cref="Id"/> 只在所属区段内唯一：40 个区段各自从 1 编，
    /// 2627 行里只有 662 个不同 Id（Id=11 出现 33 次）。拿 Id 直接当跳转键会串到
    /// 别的区段同号条目上——2026-10-30 修的「点关联·立项指南『睡眠呼吸监测费』
    /// 弹出 s01 的『供小肠切取术』」就是这个（两者 Id 都是 11）。</para>
    ///
    /// <para>由 <c>LixiangStore.Reindex()</c> 在加载 / 导入 / 增删改后统一分配，
    /// 只在当前会话内稳定；CrossMap / Related 的索引缓存带数据戳，数据变了会重建。</para>
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int Gid { get; set; }

    /// <summary>
    /// 所属区段名（**只在查询结果里填**，不落盘）。
    ///
    /// <para>桌面端靠 <see cref="Gid"/> 反查区段，网页端拿不到 Gid（JsonIgnore），
    /// 所以列表接口直接把区段名带下来 —— 网页端「区段」列和桌面端显示同一份文字。
    /// 写盘时为空即忽略（<c>config/lixiang.json</c> 不会多出 2627 个 <c>"section": null</c>）。</para>
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public string Section { get; set; }
}

public class LixiangMeta
{
    public string ImportedAt { get; set; }
    public string SourceName { get; set; }
    public int Sections { get; set; }
    public int Total { get; set; }
}

public class JieduStore
{
    public List<JieduItem> List { get; set; } = new();
    public string Preface { get; set; } = "";
    public string Source { get; set; } = "";
    public string ImportedAt { get; set; }

    /// <summary>
    /// 导入时原文字节的 SHA-256（小写十六进制）。国家医保局那个下载地址是固定的，
    /// 服务器又不返回 ETag / Last-Modified，只能靠内容哈希判断「有没有出新一期」。
    /// 老数据没这个字段（null），首次检查会解析一遍做全量比对，无差异则只补记哈希、不打扰用户。
    /// </summary>
    public string SourceHash { get; set; }
    /// <summary>原文字节数，仅用于提示里显示「255 KB」，不参与判断。</summary>
    public long SourceSize { get; set; }
}

public class JieduItem
{
    public int Issue { get; set; }
    public int No { get; set; }
    public string Q { get; set; } = "";
    public string A { get; set; } = "";
}

// =====================================================================
// 德州市物价（立项指南规范整合项目，data/dezhou-price.json）
// 数据来源：德医保发〔2025〕33号/37号、〔2026〕3号/11号 附件项目表
// =====================================================================

/// <summary>一条已落地的立项指南整合项目（含 15 位项目编码与三级价格）。</summary>
public class DezhouPriceItem
{
    public int Id { get; set; }
    /// <summary>落地文号，如「德医保发〔2026〕3号」</summary>
    public string Doc { get; set; } = "";
    /// <summary>执行日期</summary>
    public string Exec { get; set; } = "";
    /// <summary>类别，如「骨骼肌肉系统类」</summary>
    public string Category { get; set; } = "";
    /// <summary>原表序号（加收/扩展项为空）</summary>
    public string Seq { get; set; } = "";
    /// <summary>15 位项目编码</summary>
    public string Code { get; set; } = "";
    /// <summary>main=主项目 / add=加收项 / ext=扩展项</summary>
    public string Kind { get; set; } = "main";
    public string Name { get; set; } = "";
    public string ServiceOutput { get; set; } = "";
    public string PriceComposition { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Price3 { get; set; } = "";
    public string Price2 { get; set; } = "";
    public string Price1 { get; set; } = "";
    /// <summary>是否医保支付（是/否）</summary>
    public string Insurance { get; set; } = "";
    /// <summary>自付比例，如 20%</summary>
    public string SelfPay { get; set; } = "";
    /// <summary>备注 / 限定支付范围</summary>
    public string Note { get; set; } = "";
    /// <summary>计价说明</summary>
    public string Usage { get; set; } = "";
    /// <summary>
    /// 扣减基本物耗价格（元）。仅「一般治疗」类项目表有此列（如注射费扣减 1.5、换药费按档扣减），
    /// 指该价格中含有的可另行收费的基本物耗金额，收费时须从项目价中扣减。
    /// 其余批次 / 类别无此列，反序列化为空串；序列化时空值不落盘（不污染历史数据格式）。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public string Deduct { get; set; } = "";
    /// <summary>
    /// 批次状态标记：空串（默认）= 已生效的现行项目；<c>draft</c> = 征求意见稿项目（未生效）。
    /// 主库 <c>dezhou-price.json</c> 的历史数据没有这个键，反序列化后即空串，行为与既有完全一致；
    /// 只有征求意见稿（<c>dezhou-draft.json</c>）会显式写 <c>draft</c>。
    /// </summary>
    public string Status { get; set; } = "";
}

public class DezhouCategory
{
    public string Name { get; set; } = "";
    public int Items { get; set; }
    public int Main { get; set; }
    public int Add { get; set; }
    public int Ext { get; set; }
    /// <summary>
    /// 本类别的「使用说明」条目（逐条对应通知 PDF 中该类项目表末的「使用说明：」编号条款）。
    /// 类别级说明，不是单条项目的计价说明（后者是 <see cref="DezhouPriceItem.Usage"/>）。
    /// </summary>
    public List<string> Usage { get; set; } = new();
}

public class DezhouBatch
{
    public string Doc { get; set; } = "";
    public string Exec { get; set; } = "";
    /// <summary>对应省级文件</summary>
    public string Prov { get; set; } = "";
    /// <summary>涉及类别概述</summary>
    public string Scope { get; set; } = "";
    /// <summary>官方公布的项目数（主项目）</summary>
    public int Expect { get; set; }
    /// <summary>实际解析行数（含加收/扩展项）</summary>
    public int Rows { get; set; }
    public int Mains { get; set; }
    public int Adds { get; set; }
    public int Exts { get; set; }
    public List<DezhouCategory> Categories { get; set; } = new();
}

public class DezhouPriceMeta
{
    public string ImportedAt { get; set; }
    public string Source { get; set; } = "";
    public int Total { get; set; }
    public List<DezhouBatch> Batches { get; set; } = new();
}

public class DezhouPriceFile
{
    public DezhouPriceMeta Meta { get; set; } = new();
    public List<DezhouPriceItem> Items { get; set; } = new();
    /// <summary>作废项目对照（各批次「映射关系表」：作废的原物价项目 → 对应新立项指南项目）。</summary>
    public List<DezhouCancelItem> Cancels { get; set; } = new();
    /// <summary>旧版物价（德医保发〔2024〕45号对码表，规范整合前的旧价格项目）。</summary>
    public List<DezhouLegacyItem> Legacy { get; set; } = new();
}

/// <summary>
/// 一条「作废项目 → 对应新项目」映射行，来自各批次通知附件的「映射关系表」
/// （左列 = 新立项指南项目，右列 = 映射我市的原价格项目，即本次作废的项目）。
/// </summary>
public class DezhouCancelItem
{
    /// <summary>公布作废并落地的批次文号，如「德医保发〔2025〕33号」</summary>
    public string Doc { get; set; } = "";
    /// <summary>执行日期（新项目开始执行、原项目同时作废）</summary>
    public string Exec { get; set; } = "";
    /// <summary>类别，如「骨骼肌肉系统类」</summary>
    public string Category { get; set; } = "";
    /// <summary>作废项目编码（我市原物价项目编码，9 位或带字母后缀）</summary>
    public string OldCode { get; set; } = "";
    /// <summary>作废项目名称</summary>
    public string OldName { get; set; } = "";
    /// <summary>对应新项目编码（15 位立项指南编码；空 = 直接废止、无替代项目）</summary>
    public string NewCode { get; set; } = "";
    /// <summary>对应新项目名称</summary>
    public string NewName { get; set; } = "";
    /// <summary>映射备注（如「自评、他评均可映射」），可为空</summary>
    public string Note { get; set; } = "";
    /// <summary>
    /// 尚待作废标记：<c>false</c>（默认）= 本条对照已随批次正式生效，旧项目确实已作废；
    /// <c>true</c> = 仅征求意见阶段的拟作废对照，**不参与**主库
    /// <c>LegacyCanceled</c> / <c>CancelOf</c> 的「是否已作废」判定。
    /// 主库历史数据无此键，反序列化得 <c>false</c>，判定行为与既有完全一致。
    /// </summary>
    public bool Pending { get; set; }
    /// <summary>
    /// 保留标记（划线=不作废）：通知附件「废止项目表」里该行被划横线（删除线）时为 <c>true</c>。
    /// 这类项目虽然照样印在废止表上（并同时出现在映射关系表里），但原文划线即表示**不作废**，
    /// 所以本行仍留在作废对照里供核对映射，判定口径上一律按「未作废」处理：
    /// <c>LegacyCanceled</c> 返回 false、<c>LegacyCurrentStd</c> / <c>CancelOf</c> 不给「现行标准」。
    /// </summary>
    public bool Keep { get; set; }
}

/// <summary>作废对照解析中间产物（.tmp/dezhou/cancels-clean.json）。</summary>
public class DezhouCancelFile
{
    public int Total { get; set; }
    public List<DezhouCancelItem> Cancels { get; set; } = new();
}

/// <summary>
/// 旧版物价一行：德医保发〔2024〕45号《德州市医疗服务项目价格对码表（发县市区版，241230）》，
/// 即规范整合（立项指南落地）前全市执行的旧价格项目。是否已作废由运行时与 <see cref="DezhouCancelItem"/> 联查判定。
/// </summary>
public class DezhouLegacyItem
{
    // 注意：JSON 键名对齐解析脚本（parse_legacy.py）输出的短键（p3/p2/p1/ins/self…），
    // 必须显式 JsonPropertyName——默认的大小写宽容匹配对不上「ins ↔ Insurance」，会导致价格 / 医保 / 自付整列读成空串。

    /// <summary>45 号文件项目编码（旧物价编码，作废对照里的 oldCode 即此编码）</summary>
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    /// <summary>地纬库中执行编码（院内 HIS 对照用）</summary>
    [JsonPropertyName("dwCode")] public string DwCode { get; set; } = "";
    /// <summary>国家项目代码</summary>
    [JsonPropertyName("natCode")] public string NatCode { get; set; } = "";
    /// <summary>国家项目名称</summary>
    [JsonPropertyName("natName")] public string NatName { get; set; } = "";
    /// <summary>归集口径（挂号费 / 诊察费 / 护理费…）</summary>
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    /// <summary>地方项目名称</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>项目内涵</summary>
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    /// <summary>除外内容</summary>
    [JsonPropertyName("excl")] public string Excl { get; set; } = "";
    [JsonPropertyName("unit")] public string Unit { get; set; } = "";
    [JsonPropertyName("p3")] public string Price3 { get; set; } = "";
    [JsonPropertyName("p2")] public string Price2 { get; set; } = "";
    [JsonPropertyName("p1")] public string Price1 { get; set; } = "";
    /// <summary>说明</summary>
    [JsonPropertyName("note")] public string Note { get; set; } = "";
    /// <summary>是否医保支付（是 / 否）</summary>
    [JsonPropertyName("ins")] public string Insurance { get; set; } = "";
    /// <summary>自付比例或限额</summary>
    [JsonPropertyName("self")] public string SelfPay { get; set; } = "";
    /// <summary>限定支付范围</summary>
    [JsonPropertyName("limitPay")] public string LimitPay { get; set; } = "";
    /// <summary>「本次新公布项目」标记（是 / 空）</summary>
    [JsonPropertyName("isNew")] public string IsNew { get; set; } = "";
}

/// <summary>旧版物价导入中间产物：{total, legacy:[…]}。</summary>
public class DezhouLegacyFile
{
    public int Total { get; set; }
    public List<DezhouLegacyItem> Legacy { get; set; } = new();
}

/// <summary>
/// 「新旧价格比对」的一行（征求意见稿新项目 ↔ 对应的旧物价项目）。
///
/// <para>比对基准的取数顺序：**先查旧版物价（45 号对码表，字段与主库同构、有三级价格），
/// 未命中再回落到主库作废对照（旧码→新码关系，但<see cref="DezhouCancelItem"/> 没有价格字段）**。
/// 因此 <see cref="OldSource"/> 有三种取值：<c>legacy</c>（旧版物价命中，可比价）、
/// <c>cancel</c>（只在作废对照里出现，拿不到旧价格）、<c>none</c>（查无旧项目）。</para>
/// </summary>
public sealed class DezhouDraftCompareRow
{
    // ---- 新侧（征求意见稿）----
    public string NewCode { get; set; } = "";
    public string NewName { get; set; } = "";
    public string Category { get; set; } = "";
    public string NewPrice3 { get; set; } = "";
    public string NewPrice2 { get; set; } = "";
    public string NewPrice1 { get; set; } = "";
    /// <summary>main / add / ext</summary>
    public string NewKind { get; set; } = "main";

    // ---- 旧侧（旧版物价 / 作废对照）----
    public string OldCode { get; set; } = "";
    public string OldName { get; set; } = "";
    public string OldPrice3 { get; set; } = "";
    public string OldPrice2 { get; set; } = "";
    public string OldPrice1 { get; set; } = "";
    /// <summary>legacy = 旧版物价命中（可比价）；cancel = 仅作废对照（无价）；none = 查无旧项目</summary>
    public string OldSource { get; set; } = "none";
    /// <summary>旧版物价的归集口径（挂号费 / 诊察费…），仅 legacy 有</summary>
    public string OldCategory { get; set; } = "";
    /// <summary>旧项目的医保支付 / 自付，仅 legacy 有</summary>
    public string OldInsurance { get; set; } = "";
    public string OldSelfPay { get; set; } = "";

    // ---- 差额（仅三级价可解析时才有值）----
    public double? Delta3 { get; set; }
    public double? Delta2 { get; set; }
    public double? Delta1 { get; set; }
    /// <summary>按三级价算的涨幅（%）；旧价为 0 或缺失时不计算</summary>
    public double? Ratio3 { get; set; }
    /// <summary>按二级价算的涨幅（%）；同上，切「二级医院」口径时用</summary>
    public double? Ratio2 { get; set; }
    /// <summary>按一级价算的涨幅（%）；同上，切「一级医院」口径时用</summary>
    public double? Ratio1 { get; set; }
}

// =====================================================================
// 德州市物价「新项目征求意见」稿（data/dezhou-draft.json）
// =====================================================================

/// <summary>
/// 征求意见稿的一条「映射关系」（通知附件「映射关系表」的一行）：
/// 新立项指南项目 ↔ 拟作废的原价格项目。与 <see cref="DezhouCancelItem"/> 的区别是
/// <see cref="DezhouCancelItem"/> 只保留「旧 → 新」的单向作废对照，而本表额外保留
/// 原表两侧的完整名称与序号，且带 <see cref="Pending"/> 标记（征求意见阶段尚未真作废）。
/// </summary>
public class DezhouDraftMapRow
{
    /// <summary>新项目序号（同一主项的加收 / 扩展项与主项共用序号，续行留空）</summary>
    public string Seq { get; set; } = "";
    /// <summary>新立项指南项目编码（15 位；正常不为空）</summary>
    public string NewCode { get; set; } = "";
    public string NewName { get; set; } = "";
    /// <summary>拟作废的原价格项目编码（9 位或带字母后缀）</summary>
    public string OldCode { get; set; } = "";
    public string OldName { get; set; } = "";
    /// <summary>本行所属类别（用于按类筛选，取自 sheet 名）</summary>
    public string Category { get; set; } = "";
    /// <summary>原表备注（一般多行，如「自评、他评均可映射」）</summary>
    public string Note { get; set; } = "";
    /// <summary>
    /// 尚待作废标记：征求意见阶段恒为 true（本行只做对照展示，**不参与**主库
    /// 「旧项目是否已作废」的判定）；正式发布转正后置 false，旧项目方视为作废。
    /// </summary>
    public bool Pending { get; set; } = true;
}

/// <summary>
/// 德州市物价「新项目征求意见」稿的元信息。
/// 本批（消化系统等 9 类）处于征求意见阶段，尚无正式文号，故 <see cref="Doc"/> 允许为空。
/// </summary>
public class DezhouDraftMeta
{
    /// <summary>正式文号（征求意见阶段为空，正式发布时由后台填写）</summary>
    public string Doc { get; set; } = "";
    /// <summary>批次显示名，如「消化系统等 9 类医疗服务价格项目（征求意见稿）」</summary>
    public string Label { get; set; } = "";
    /// <summary>执行日期（正式发布后填写）</summary>
    public string Exec { get; set; } = "";
    /// <summary>
    /// 状态：<c>draft</c> = 征求意见中（本期只此一态）；<c>active</c> = 已正式发布并并入主库。
    /// 主库不认这个字段，仅本文件使用。
    /// </summary>
    public string Status { get; set; } = "draft";
    /// <summary>页面顶部黄条展示的说明文字</summary>
    public string Note { get; set; } = "";
    /// <summary>本批作废项目所涉的既有批次文号（如「德医保发〔2025〕33号」），仅作说明用</summary>
    public List<string> Refs { get; set; } = new();
    public string ImportedAt { get; set; }
    /// <summary>数据来源描述</summary>
    public string Source { get; set; } = "";
    /// <summary>原始文件名（便于溯源）</summary>
    public string FileName { get; set; } = "";
    /// <summary>各类别统计（结构复用 <see cref="DezhouBatch"/>，其中 Doc 填本批文号或显示名）</summary>
    public List<DezhouBatch> Batches { get; set; } = new();
}

/// <summary>
/// 德州市物价「新项目征求意见」稿数据文件（data/dezhou-draft.json）。
/// 与主库 <see cref="DezhouPriceFile"/> 同构但不含 <c>legacy</c>——
/// 征求意见稿不提供旧版物价表，废止对照由 <see cref="Cancels"/> 承载。
/// </summary>
public class DezhouDraftFile
{
    public DezhouDraftMeta Meta { get; set; } = new();
    /// <summary>拟新增项目（status 恒为 draft，前台整行淡黄 + 「征求意见稿」角标）</summary>
    public List<DezhouPriceItem> Items { get; set; } = new();
    /// <summary>拟作废的原价格项目（pending 恒为 true，前台「废止对照」视图）</summary>
    public List<DezhouCancelItem> Cancels { get; set; } = new();
    /// <summary>映射关系（通知附件「映射关系表」原始两列，前台「映射关系」视图）</summary>
    public List<DezhouDraftMapRow> Maps { get; set; } = new();
}

// =====================================================================
// 全国医疗服务项目技术规范（2023 年版）
// =====================================================================

/// <summary>
/// 《全国医疗服务项目技术规范（2023 年版）》的一个项目条目。
/// 含规范正文的 17 个要素（编码 / 中英文名称 / 内涵 / 耗材 / 人力 / 难度 / 风险 / 相对值 /
/// 计量单位 / 说明 / 调整系数 / 票据分类 / 会计科目 / 病案首页分类）以及所属层级与页码。
/// </summary>
public class GuifanItem
{
    public int Id { get; set; }
    /// <summary>项目编码，恒为 8 位字母数字混合码（如 AAAA0001 / FL91B701）</summary>
    public string Code { get; set; } = "";
    /// <summary>项目名称（中文）</summary>
    public string Name { get; set; } = "";
    /// <summary>项目名称（英文）</summary>
    public string En { get; set; } = "";
    /// <summary>项目内涵</summary>
    public string Content { get; set; } = "";
    /// <summary>必需耗材</summary>
    public string Required { get; set; } = "";
    /// <summary>可选耗材</summary>
    public string Optional { get; set; } = "";
    /// <summary>低值耗材分档（1-9 档）</summary>
    public string LowGrade { get; set; } = "";
    /// <summary>基本人力消耗及耗时</summary>
    public string Manpower { get; set; } = "";
    /// <summary>技术难度（各章独立 1-100）</summary>
    public string Difficulty { get; set; } = "";
    /// <summary>风险程度（各章独立 1-100）</summary>
    public string Risk { get; set; } = "";
    /// <summary>人力资源消耗相对值（各章独立 1-100）</summary>
    public string HrValue { get; set; } = "";
    /// <summary>计量单位</summary>
    public string Unit { get; set; } = "";
    /// <summary>说明</summary>
    public string Note { get; set; } = "";
    /// <summary>特殊情况资源消耗调整系数</summary>
    public string Factor { get; set; } = "";
    /// <summary>收费票据分类</summary>
    public string Bill { get; set; } = "";
    /// <summary>会计科目分类</summary>
    public string Account { get; set; } = "";
    /// <summary>病案首页费用分类</summary>
    public string CaseFee { get; set; } = "";
    /// <summary>类别：综合 / 诊断 / 治疗 / 康复 / 中医 / 辅助操作</summary>
    public string Category { get; set; } = "";
    /// <summary>所属章，如「六 临床手术」</summary>
    public string Chapter { get; set; } = "";
    /// <summary>所属节，如「(一)一般医疗服务」</summary>
    public string Section { get; set; } = "";
    /// <summary>所属组，如「西医诊察」</summary>
    public string Group { get; set; } = "";
    /// <summary>规范正文页码</summary>
    public int Page { get; set; }
}

/// <summary>技术规范的章（含该章项目数）。</summary>
public class GuifanChapter
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public int Items { get; set; }
}

public class GuifanMeta
{
    public string ImportedAt { get; set; }
    public string Source { get; set; } = "";
    public int Total { get; set; }
    /// <summary>官方公布的各章项目数（用于对账）</summary>
    public List<GuifanChapter> Chapters { get; set; } = new();
}

public class GuifanFile
{
    public GuifanMeta Meta { get; set; } = new();
    public List<GuifanItem> Items { get; set; } = new();
}


// =====================================================================
// 病案入参
// =====================================================================

/// <summary>规范化后的病案（对齐 normalizeCase 输出）。</summary>
public class Kase
{
    public string MainDiag { get; set; } = "";
    public List<string> OtherDiags { get; set; } = new();
    public string MainProc { get; set; } = "";
    public List<string> OtherProcs { get; set; } = new();
    public double? AgeDays { get; set; }
    public double? BirthWeight { get; set; }
    public string CaseId { get; set; }

    public Kase Clone() => new()
    {
        MainDiag = MainDiag,
        OtherDiags = new List<string>(OtherDiags),
        MainProc = MainProc,
        OtherProcs = new List<string>(OtherProcs),
        AgeDays = AgeDays,
        BirthWeight = BirthWeight,
        CaseId = CaseId,
    };
}

/// <summary>分组结果（对齐 grouper.group 返回对象；序列化顺序与键存在性与 Node JSON.stringify 一致）。</summary>
[JsonConverter(typeof(GroupResultConverter))]
public class GroupResult
{
    public bool Matched { get; set; }
    public string GroupType { get; set; }
    public string GroupTypeCode { get; set; }
    public int? Seq { get; set; }
    public string Name { get; set; }
    public Dictionary<string, object> Detail { get; set; } = new();
    public string Reason { get; set; }
    public Dictionary<string, object> Translate { get; set; }

    /// <summary>
    /// 分组溯源（逐层判定过程）。**不参与序列化** —— <see cref="GroupResultConverter.Write"/>
    /// 只写固定键，故它不会污染 <c>/api/group</c> 与 Node 版逐字节一致的返回契约；
    /// 只在显式要求（<c>trace: true</c> / 桌面端）时才有值。
    /// </summary>
    public GroupTrace Trace { get; set; }

    /// <summary>
    /// 命中「目录库外病种」（省局反馈清单）时的预警信息。<b>同样不参与序列化</b>，理由与
    /// <see cref="Trace"/> 相同：<c>/api/group</c> 的键集合是对拍基线，多一个键就破。
    /// 桌面端单条 / 批量结果都从这里取预警文案。
    /// </summary>
    public OutLibHit OutLib { get; set; }
}

/// <summary>
/// 精确复刻 Node 端分组结果对象:
///   命中 → { matched, groupType, groupTypeCode, seq?, name, detail, translate? }
///   未命中 → { matched, groupType:null, groupTypeCode:null, seq:null, name:null, detail, reason }
/// （seq 为 undefined 时整键省略；未命中时 seq/name 显式为 null）
/// </summary>
public class GroupResultConverter : JsonConverter<GroupResult>
{
    public override GroupResult Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException();

    public override void Write(Utf8JsonWriter w, GroupResult v, JsonSerializerOptions options)
    {
        w.WriteStartObject();
        w.WriteBoolean("matched", v.Matched);
        WriteStringOrNull(w, "groupType", v.GroupType);
        WriteStringOrNull(w, "groupTypeCode", v.GroupTypeCode);

        if (v.Matched)
        {
            if (v.Seq.HasValue) w.WriteNumber("seq", v.Seq.Value);
        }
        else
        {
            w.WriteNull("seq");
        }

        if (v.Matched)
        {
            if (v.Name != null) w.WriteString("name", v.Name);
            else w.WriteNull("name");
        }
        else
        {
            w.WriteNull("name");
        }

        w.WritePropertyName("detail");
        JsonSerializer.Serialize(w, v.Detail ?? new Dictionary<string, object>(), options);

        if (v.Reason != null) w.WriteString("reason", v.Reason);
        if (v.Translate != null)
        {
            w.WritePropertyName("translate");
            JsonSerializer.Serialize(w, v.Translate, options);
        }
        w.WriteEndObject();
    }

    private static void WriteStringOrNull(Utf8JsonWriter w, string name, string val)
    {
        if (val == null) w.WriteNull(name); else w.WriteString(name, val);
    }
}
