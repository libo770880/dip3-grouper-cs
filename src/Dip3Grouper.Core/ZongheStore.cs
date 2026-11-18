using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dip3Grouper;

/// <summary>
/// 综合病组存储（对齐 src/zonghe-store.js）
/// 行: { id, code, name, ranges: ['A15-A16', ...], note }
/// </summary>
public class ZongheStore
{
    private static readonly Regex RangeRe = new(@"^([A-Z]\d{2})(?:\s*-\s*([A-Z]\d{2}))?$", RegexOptions.Compiled);

    public List<ZongheRow> Rows { get; private set; } = new();
    public ZongheMeta Meta { get; private set; } = new();
    private int _nextId = 1;

    public ZongheStore() => Load();

    /// <summary>解析范围字段: 'A15-A16|A20' / 'A15,A16-A17' → ['A15-A16','A20']</summary>
    public static List<string> ParseRanges(string v)
    {
        var outList = new List<string>();
        foreach (var part in Regex.Split(v ?? "", @"[|;；,，、\s]+"))
        {
            var s = part.Trim().ToUpperInvariant();
            if (s.Length == 0) continue;
            var m = RangeRe.Match(s);
            if (m.Success) outList.Add(m.Groups[2].Success ? $"{m.Groups[1].Value}-{m.Groups[2].Value}" : m.Groups[1].Value);
        }
        return outList.Distinct().ToList();
    }

    /// <summary>解析范围字段（JSON 值）：数组逐元素解析，字符串按分隔符拆分。</summary>
    public static List<string> ParseRanges(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Array)
        {
            var outList = new List<string>();
            foreach (var it in v.EnumerateArray())
            {
                var s = (it.ValueKind == JsonValueKind.String
                    ? it.GetString() ?? ""
                    : it.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "" : it.GetRawText())
                    .Trim().ToUpperInvariant();
                if (s.Length == 0) continue;
                var m = RangeRe.Match(s);
                if (m.Success) outList.Add(m.Groups[2].Success ? $"{m.Groups[1].Value}-{m.Groups[2].Value}" : m.Groups[1].Value);
            }
            return outList.Distinct().ToList();
        }
        if (v.ValueKind == JsonValueKind.String) return ParseRanges(v.GetString());
        if (v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return new List<string>();
        return ParseRanges(v.GetRawText());
    }

    public void Load()
    {
        try
        {
            var data = Js.ReadJsonFile<ZongheFile>(AppPaths.ZongheFile);
            if (data != null)
            {
                Rows = data.Rows ?? new List<ZongheRow>();
                Meta = data.Meta ?? Meta;
                _nextId = Rows.Count == 0 ? 1 : Rows.Max(r => r.Id) + 1;
            }
        }
        catch
        {
            Rows = new List<ZongheRow>();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        Js.WriteJsonFile(AppPaths.ZongheFile, new ZongheFile { Rows = Rows, Meta = Meta });
    }

    public ZongheListResult List(string q = "", int page = 1, int pageSize = 50)
    {
        IEnumerable<ZongheRow> rows = Rows;
        var kw = (q ?? "").Trim().ToUpperInvariant();
        if (kw.Length > 0)
        {
            rows = rows.Where(r =>
                (r.Code ?? "").ToUpperInvariant().Contains(kw) ||
                (r.Name ?? "").ToUpperInvariant().Contains(kw) ||
                (r.Ranges ?? new List<string>()).Any(g => g.ToUpperInvariant().Contains(kw)));
        }
        var list = rows.ToList();
        var total = list.Count;
        var start = (page - 1) * pageSize;
        if (start < 0) start = 0;
        return new ZongheListResult
        {
            Total = total,
            Page = page,
            PageSize = pageSize,
            Rows = list.Skip(start).Take(pageSize).ToList(),
        };
    }

    public ZongheStats Stats() => new ZongheStats
    {
        Total = Rows.Count,
        MultiRange = Rows.Count(r => (r.Ranges ?? new List<string>()).Count > 1),
        ImportedAt = Meta.ImportedAt,
        SourceName = Meta.SourceName,
    };

    public (string Error, int Id) Add(JsonElement body)
    {
        string Str(string key)
        {
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(key, out var v)) return "";
            return v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : (v.ValueKind == JsonValueKind.Null ? "" : v.GetRawText());
        }
        var ranges = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("ranges", out var rv0)
            ? ParseRanges(rv0)
            : new List<string>();
        if (ranges.Count == 0) return ("诊断范围不能为空（如 A15-A16 或 A15）", 0);
        var code = Str("code").Trim();
        if (code.Length == 0) return ("病组编码不能为空", 0);
        if (Rows.Any(r => (r.Code ?? "").ToUpperInvariant() == code.ToUpperInvariant()))
            return ($"病组编码 {code} 已存在", 0);

        var rec = new ZongheRow
        {
            Id = _nextId++,
            Code = code,
            Name = Str("name").Trim() is { Length: > 0 } n ? n : $"综合病组（{ranges[0]}）",
            Ranges = ranges,
            Note = Str("note").Trim(),
        };
        Rows.Add(rec);
        Meta.ImportedAt ??= Js.Iso();
        Save();
        return (null, rec.Id);
    }

    public bool Update(int id, JsonElement patch)
    {
        var row = Rows.FirstOrDefault(r => r.Id == id);
        if (row == null) return false;
        if (patch.ValueKind != JsonValueKind.Object) return true;
        string Str(string key)
        {
            if (!patch.TryGetProperty(key, out var v)) return null;
            return v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : (v.ValueKind == JsonValueKind.Null ? "" : v.GetRawText());
        }
        var code = Str("code");
        if (code != null) row.Code = code.Trim().Length > 0 ? code.Trim() : row.Code;
        var name = Str("name");
        if (name != null) row.Name = name.Trim();
        var note = Str("note");
        if (note != null) row.Note = note.Trim();
        var rangesRaw = Str("ranges");
        if (rangesRaw != null)
        {
            var ranges = ParseRanges(patch.GetProperty("ranges"));
            if (ranges.Count == 0) return false;
            row.Ranges = ranges;
        }
        Save();
        return true;
    }

    public bool Remove(int id)
    {
        var idx = Rows.FindIndex(r => r.Id == id);
        if (idx < 0) return false;
        Rows.RemoveAt(idx);
        Save();
        return true;
    }

    public void Clear()
    {
        Rows = new List<ZongheRow>();
        _nextId = 1;
        Meta = new ZongheMeta { ImportedAt = Js.Iso(), SourceName = "已清空" };
        Save();
    }

    public ZongheImportResult ImportRows(IEnumerable<ZongheImportRow> rawRows, string mode, string sourceName)
    {
        var raw = (rawRows ?? Enumerable.Empty<ZongheImportRow>()).ToList();
        var parsed = raw.Select(r => new ZongheRow
        {
            Code = (r.Code ?? "").Trim(),
            Name = (r.Name ?? "").Trim(),
            Ranges = ParseRanges(r.Ranges),
            Note = (r.Note ?? "").Trim(),
        }).Where(r => r.Code.Length > 0 && r.Ranges.Count > 0).ToList();

        if (parsed.Count == 0) throw new InvalidOperationException("未解析到有效行（需含病组编码与合法诊断范围，如 A15-A16）");

        if (mode == "replace")
        {
            Rows = new List<ZongheRow>();
            _nextId = 1;
        }
        int added = 0, updated = 0;
        var index = new Dictionary<string, ZongheRow>();
        foreach (var r in Rows) index[(r.Code ?? "").ToUpperInvariant()] = r;
        foreach (var p in parsed)
        {
            if (index.TryGetValue(p.Code.ToUpperInvariant(), out var exist))
            {
                if (!string.IsNullOrEmpty(p.Name)) exist.Name = p.Name;
                exist.Ranges = p.Ranges;
                if (!string.IsNullOrEmpty(p.Note)) exist.Note = p.Note;
                updated++;
            }
            else
            {
                p.Id = _nextId++;
                Rows.Add(p);
                index[p.Code.ToUpperInvariant()] = p;
                added++;
            }
        }
        Meta = new ZongheMeta { ImportedAt = Js.Iso(), SourceName = sourceName };
        Save();
        return new ZongheImportResult
        {
            Parsed = raw.Count,
            Valid = parsed.Count,
            Added = added,
            Updated = updated,
            Total = Rows.Count,
        };
    }
}

/// <summary>综合病组分页列表（原匿名类型）</summary>
public sealed class ZongheListResult
{
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<ZongheRow> Rows { get; set; }
}

/// <summary>综合病组统计（原匿名类型）</summary>
public sealed class ZongheStats
{
    public int Total { get; set; }
    public int MultiRange { get; set; }
    public string ImportedAt { get; set; }
    public string SourceName { get; set; }
}

/// <summary>综合病组导入结果（原匿名类型）</summary>
public sealed class ZongheImportResult
{
    public int Parsed { get; set; }
    public int Valid { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Total { get; set; }
}

public class ZongheImportRow
{
    public string Code { get; set; }
    public string Name { get; set; }
    public string Ranges { get; set; }
    public string Note { get; set; }
}
