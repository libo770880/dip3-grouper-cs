using System.Text.Json;

namespace Dip3Grouper;

/// <summary>
/// ICD-10 → 医保编码 映射表存储（对齐 src/store.js）
///
/// 灰码语义:
///   - gray=true 的映射行表示该 ICD-10 编码标注为"灰码"（不映射医保编码）
///   - 导入时: 医保编码为空，或"灰码"列为 是/1/true/灰码 → 自动标注灰码
/// </summary>
public class MappingStore
{
    public List<MappingRow> Rows { get; private set; } = new();
    public MappingMeta Meta { get; private set; } = new() { ImportedAt = null, SourceName = null, Total = 0 };

    private int _nextId = 1;
    private Dictionary<string, MappingRow> _exactIndex = new();
    private Dictionary<string, MappingRow> _nameIndex;   // withDetailNames 用（首个命中优先）
    private Dictionary<string, string> _insNameByCode;   // 快速名称查询

    public MappingStore() => Load();

    private static bool IsGrayFlag(string insCode, string grayField)
    {
        var g = (grayField ?? "").Trim().ToLowerInvariant();
        if (new[] { "是", "1", "true", "灰码", "y", "yes" }.Contains(g)) return true;
        if (new[] { "否", "0", "false", "", "n", "no" }.Contains(g)) return insCode == "";
        return insCode == "";
    }

    private static string Norm(string code) => (code ?? "").Trim().ToUpperInvariant();

    public void Load()
    {
        try
        {
            var data = Js.ReadJsonFile<MappingFile>(AppPaths.MappingFile);
            if (data != null)
            {
                Rows = data.Rows ?? new List<MappingRow>();
                Meta = data.Meta ?? Meta;
                _nextId = Rows.Count == 0 ? 1 : Rows.Max(r => r.Id) + 1;
            }
        }
        catch
        {
            Rows = new List<MappingRow>();
        }
        RebuildIndex();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        Js.WriteJsonFile(AppPaths.MappingFile, new MappingFile { Rows = Rows, Meta = Meta });
        RebuildIndex();
    }

    public void RebuildIndex()
    {
        _exactIndex = new Dictionary<string, MappingRow>();
        // Node: Map.set 顺序写入 → 后者覆盖前者（LAST wins）
        foreach (var r in Rows) _exactIndex[Norm(r.Code)] = r;

        // 名称查询索引（FIRST wins，对齐 rows.find 语义）
        _nameIndex = new Dictionary<string, MappingRow>();
        _insNameByCode = new Dictionary<string, string>();
        foreach (var r in Rows)
        {
            foreach (var key in new[] { Norm(r.Code), Norm(r.InsCode) })
            {
                if (key.Length == 0) continue;
                if (!_nameIndex.ContainsKey(key))
                {
                    _nameIndex[key] = r;
                    _insNameByCode[key] = string.IsNullOrEmpty(r.InsName) ? (r.Name ?? "") : r.InsName;
                }
            }
        }
    }

    /// <summary>按编码取名称（先 code 后 insCode 的等效首次命中语义），返回 名称 或 ""。</summary>
    public string NameOfCode(string code)
    {
        if (string.IsNullOrEmpty(code)) return "";
        return _insNameByCode.TryGetValue(Norm(code), out var n) ? n : "";
    }

    /// <summary>
    /// 按编码取 ICD-10 名称（联想列表里显示的那个名称，不是医保版名称），输入框回显用。
    /// 名称为空时回落医保版名称，都没有返回空串。
    /// </summary>
    public string DisplayNameOfCode(string code)
    {
        if (string.IsNullOrEmpty(code)) return "";
        if (!_exactIndex.TryGetValue(Norm(code), out var r)) return "";
        if (!string.IsNullOrEmpty(r.Name)) return r.Name;
        return r.InsName ?? "";
    }

    /// <summary>批量导入, mode: replace=清空后导入 / append=追加(同编码覆盖)</summary>
    public (int Parsed, int Added, int Updated, int Total) ImportRows(IEnumerable<MappingImportRow> rawRows, string mode, string sourceName)
    {
        var parsed = new List<MappingRow>();
        foreach (var r in rawRows ?? Enumerable.Empty<MappingImportRow>())
        {
            var code = (r.Code ?? "").Trim();
            var insCode = (r.InsCode ?? "").Trim();
            var gray = r.Gray ?? IsGrayFlag(insCode, r.GrayField);
            parsed.Add(new MappingRow
            {
                Code = code,
                Name = (r.Name ?? "").Trim(),
                InsCode = insCode,
                InsName = (r.InsName ?? "").Trim(),
                Gray = gray,
                Note = (r.Note ?? "").Trim(),
            });
        }
        parsed = parsed.Where(r => r.Code.Length > 0).ToList();

        if (mode == "replace")
        {
            Rows = new List<MappingRow>();
            _nextId = 1;
            _exactIndex.Clear(); // 同步清空索引, 避免旧条目误判为"已存在"
        }

        int added = 0, updated = 0;
        foreach (var p in parsed)
        {
            var key = Norm(p.Code);
            _exactIndex.TryGetValue(key, out var exist);
            if (exist != null)
            {
                if (!string.IsNullOrEmpty(p.Name)) exist.Name = p.Name;
                exist.InsCode = p.InsCode;
                if (!string.IsNullOrEmpty(p.InsName)) exist.InsName = p.InsName;
                exist.Gray = p.Gray;
                if (!string.IsNullOrEmpty(p.Note)) exist.Note = p.Note;
                updated++;
            }
            else
            {
                p.Id = _nextId++;
                Rows.Add(p);
                added++;
            }
        }
        Meta = new MappingMeta { ImportedAt = Js.Iso(), SourceName = sourceName, Total = Rows.Count };
        Save();
        return (parsed.Count, added, updated, Rows.Count);
    }

    public MappingStats Stats()
    {
        var gray = Rows.Count(r => r.Gray);
        return new MappingStats
        {
            Total = Rows.Count,
            Gray = gray,
            Mapped = Rows.Count(r => !r.Gray && !string.IsNullOrEmpty(r.InsCode)),
            ImportedAt = Meta.ImportedAt,
            SourceName = Meta.SourceName,
        };
    }

    /// <summary>查询列表: q 按编码/名称模糊; gray: true/false/null; 分页</summary>
    public (int Total, int Page, int PageSize, List<MappingRow> Rows) List(string q = "", bool? gray = null, int page = 1, int pageSize = 50)
    {
        IEnumerable<MappingRow> rows = Rows;
        var kw = (q ?? "").Trim().ToUpperInvariant();
        if (kw.Length > 0)
        {
            rows = rows.Where(r =>
                Norm(r.Code).Contains(kw) ||
                (r.Name ?? "").ToUpperInvariant().Contains(kw) ||
                Norm(r.InsCode).Contains(kw) ||
                (r.InsName ?? "").ToUpperInvariant().Contains(kw));
        }
        if (gray == true) rows = rows.Where(r => r.Gray);
        if (gray == false) rows = rows.Where(r => !r.Gray);
        var list = rows.ToList();
        var total = list.Count;
        var start = (page - 1) * pageSize;
        if (start < 0) start = 0;
        return (total, page, pageSize, list.Skip(start).Take(pageSize).ToList());
    }

    public bool SetGray(int id, bool gray)
    {
        var row = Rows.FirstOrDefault(r => r.Id == id);
        if (row == null) return false;
        row.Gray = gray;
        Save();
        return true;
    }

    public bool Update(int id, JsonElement patch)
    {
        var row = Rows.FirstOrDefault(r => r.Id == id);
        if (row == null) return false;
        if (patch.ValueKind != JsonValueKind.Object) return true;
        foreach (var k in new[] { "code", "name", "insCode", "insName", "note" })
        {
            if (!patch.TryGetProperty(k, out var v)) continue;
            var s = v.ValueKind == JsonValueKind.String ? v.GetString() : (v.ValueKind == JsonValueKind.Null ? "" : v.GetRawText());
            s = (s ?? "").Trim();
            switch (k)
            {
                case "code": row.Code = s; break;
                case "name": row.Name = s; break;
                case "insCode": row.InsCode = s; break;
                case "insName": row.InsName = s; break;
                case "note": row.Note = s; break;
            }
        }
        if (patch.TryGetProperty("gray", out var gv) && gv.ValueKind is JsonValueKind.True or JsonValueKind.False)
            row.Gray = gv.GetBoolean();
        Save();
        return true;
    }

    public (string Error, int Id) Add(JsonElement body)
    {
        string Str(string key)
        {
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(key, out var v)) return "";
            return v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : (v.ValueKind == JsonValueKind.Null ? "" : v.GetRawText());
        }
        var code = Str("code").Trim();
        if (code.Length == 0) return ("ICD-10 编码不能为空", 0);
        if (_exactIndex.TryGetValue(Norm(code), out var exist))
            return ($"编码 {code} 已存在(#{exist.Id})", exist.Id);

        var insCode = Str("insCode").Trim();
        var grayRaw = Str("gray").Trim();
        var rec = new MappingRow
        {
            Id = _nextId++,
            Code = code,
            Name = Str("name").Trim(),
            InsCode = insCode,
            InsName = Str("insName").Trim(),
            Gray = IsGrayFlag(insCode, grayRaw),
            Note = Str("note").Trim(),
        };
        Rows.Add(rec);
        Save();
        return (null, rec.Id);
    }

    public bool Remove(int id)
    {
        var idx = Rows.FindIndex(r => r.Id == id);
        if (idx < 0) return false;
        Rows.RemoveAt(idx);
        Save();
        return true;
    }

    /// <summary>名称关键词总命中数（供下拉提示"共 N 条"）</summary>
    public int CountName(string q)
    {
        var kw = (q ?? "").Trim().ToUpperInvariant();
        if (kw.Length == 0) return 0;
        return Rows.Count(r =>
            (r.Name ?? "").ToUpperInvariant().Contains(kw) ||
            (r.InsName ?? "").ToUpperInvariant().Contains(kw) ||
            PinyinHelper.PyMatch(r.Name, kw) ||
            PinyinHelper.PyMatch(r.InsName, kw));
    }

    public class SuggestItem
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public string InsCode { get; set; }
        public string InsName { get; set; }
        public bool Gray { get; set; }
    }

    /// <summary>诊断联想（前台下拉）: 编码前缀命中优先（短码在前）, 其次名称包含。</summary>
    public List<SuggestItem> Suggest(string q, int limit = 10)
    {
        var kw = (q ?? "").Trim().ToUpperInvariant();
        if (kw.Length == 0) return new List<SuggestItem>();
        var prefix = new List<MappingRow>();
        var nameHit = new List<MappingRow>();
        var isChinese = kw.Any(ch => ch > 0x7F);
        var minNameLen = isChinese ? 1 : 2;

        foreach (var r in Rows)
        {
            var code = (r.Code ?? "").ToUpperInvariant();
            var ins = (r.InsCode ?? "").ToUpperInvariant();
            if (!isChinese && (code.StartsWith(kw, StringComparison.Ordinal) || (ins.Length > 0 && ins.StartsWith(kw, StringComparison.Ordinal))))
            {
                prefix.Add(r);
                if (prefix.Count >= limit * 3) break;
            }
            else if (kw.Length >= minNameLen)
            {
                if ((r.Name ?? "").ToUpperInvariant().Contains(kw) ||
                    (r.InsName ?? "").ToUpperInvariant().Contains(kw) ||
                    PinyinHelper.PyMatch(r.Name, kw) ||
                    PinyinHelper.PyMatch(r.InsName, kw))
                {
                    nameHit.Add(r);
                    if (nameHit.Count >= 500) break;
                }
            }
        }

        prefix.Sort((a, b) =>
        {
            var c = (a.Code ?? "").Length.CompareTo((b.Code ?? "").Length);
            return c != 0 ? c : string.CompareOrdinal(a.Code, b.Code);
        });

        static int ComboOf(MappingRow r)
        {
            var s = string.IsNullOrEmpty(r.InsCode) ? (r.Code ?? "") : r.InsCode;
            return s.Contains('+') || s.Contains('*') ? 1 : 0;
        }

        nameHit.Sort((a, b) =>
        {
            var an = a.Name ?? ""; var bn = b.Name ?? "";
            var asx = an.ToUpperInvariant().StartsWith(kw, StringComparison.Ordinal) ? 0 : 1;
            var bsx = bn.ToUpperInvariant().StartsWith(kw, StringComparison.Ordinal) ? 0 : 1;
            if (asx != bsx) return asx - bsx;
            if (asx == 0 && an.Length != bn.Length) return an.Length - bn.Length;
            var ai = an.ToUpperInvariant().IndexOf(kw, StringComparison.Ordinal);
            var bi = bn.ToUpperInvariant().IndexOf(kw, StringComparison.Ordinal);
            if (ai != bi) return ai - bi;
            if (an.Length != bn.Length) return an.Length - bn.Length;
            var c = ComboOf(a) - ComboOf(b);
            if (c != 0) return c;
            return string.CompareOrdinal(a.Code, b.Code);
        });

        return prefix.Concat(nameHit).Take(limit).Select(r => new SuggestItem
        {
            Code = r.Code,
            Name = r.Name,
            InsCode = r.InsCode,
            InsName = r.InsName,
            Gray = r.Gray,
        }).ToList();
    }

    public sealed class TranslateResult
    {
        public string From { get; set; }
        public string To { get; set; }
        public bool Gray { get; set; }
        public bool Matched { get; set; }
        public string Via { get; set; }
        public bool HasVia { get; set; }
        public string Icd10Name { get; set; }
        public string InsuranceName { get; set; }
    }

    /// <summary>翻译单个诊断编码: 精确匹配 → 最长前缀匹配。</summary>
    public TranslateResult Translate(string code)
    {
        var c = Norm(code);
        if (c.Length == 0) return new TranslateResult { From = code, To = code, Gray = false, Matched = false, HasVia = false };
        _exactIndex.TryGetValue(c, out var row);
        string via = "exact";
        if (row == null)
        {
            for (var len = c.Length - 1; len >= 3; len--)
            {
                if (_exactIndex.TryGetValue(c[..len], out row)) { via = "prefix"; break; }
            }
        }
        if (row == null)
            return new TranslateResult { From = code, To = code, Gray = false, Matched = false, Via = null, HasVia = true };
        return new TranslateResult
        {
            From = code,
            To = row.Gray ? code : row.InsCode,
            Gray = row.Gray,
            Matched = true,
            Via = via,
            HasVia = true,
            Icd10Name = row.Name,
            InsuranceName = row.InsName,
        };
    }

    /// <summary>翻译结果 → 动态字典（键存在性对齐 Node）。</summary>
    public static Dictionary<string, object> TranslateToDict(TranslateResult t)
    {
        var d = new Dictionary<string, object>
        {
            ["from"] = t.From,
            ["to"] = t.To,
            ["gray"] = t.Gray,
            ["matched"] = t.Matched,
        };
        if (t.HasVia) d["via"] = t.Via;
        if (t.Matched)
        {
            d["icd10Name"] = t.Icd10Name;
            d["insuranceName"] = t.InsuranceName;
        }
        return d;
    }

    // ---------- 病种库元信息 ----------
    public static ConfigMetaFile LoadConfigMeta()
    {
        try { return Js.ReadJsonFile<ConfigMetaFile>(AppPaths.ConfigMetaFile) ?? new ConfigMetaFile(); }
        catch { return new ConfigMetaFile(); }
    }

    public static void SaveConfigMeta(ConfigMetaFile meta)
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        // Node 侧: JSON.stringify(meta, null, 2) → 2 空格缩进
        Js.WriteJsonFile(AppPaths.ConfigMetaFile, meta, null, 2);
    }
}

public class ConfigMetaFile
{
    public string ImportedAt { get; set; }
    public string SourceName { get; set; }
    public Dictionary<string, SectionCount> Counts { get; set; }
}

public class SectionCount
{
    public string Label { get; set; }
    public int Total { get; set; }
}

/// <summary>
/// 映射表统计。原为匿名类型（属性名小写），改为具名类型后由 camelCase 命名策略序列化，
/// JSON 字段名 total/gray/mapped/importedAt/sourceName 保持逐字段一致。
/// </summary>
public sealed class MappingStats
{
    public int Total { get; set; }
    public int Gray { get; set; }
    public int Mapped { get; set; }
    public string ImportedAt { get; set; }
    public string SourceName { get; set; }
}

/// <summary>映射表导入中间行</summary>
public class MappingImportRow
{
    public string Code { get; set; }
    public string Name { get; set; }
    public string InsCode { get; set; }
    public string InsName { get; set; }
    public string GrayField { get; set; }
    public bool? Gray { get; set; }
    public string Note { get; set; }
}
