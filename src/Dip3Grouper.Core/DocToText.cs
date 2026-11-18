using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using NPOI.POIFS.FileSystem;

namespace Dip3Grouper;

/// <summary>
/// Word 兼容文档 → 纯文本（对齐 tools/doc2txt.py，纯托管实现，无需 Python/COM）
///   - .docx: 解压 word/document.xml, 取段落文本（剔除表格块，对齐 python-docx 的 paragraphs 语义）
///   - .txt : UTF-8（自动去 BOM）
///   - .doc/.wps: OLE 复合文档 → FIB → CLX → piece table 逐片解码
/// </summary>
public static class DocToText
{
    static DocToText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static readonly Regex CtrlRe = new(@"[\x00-\x08\x0e-\x1f\x13\x14\x15]", RegexOptions.Compiled);
    private static readonly Regex TblRe = new(@"<w:tbl\b[\s\S]*?</w:tbl>", RegexOptions.Compiled);
    private static readonly Regex WtRe = new(@"<w:t(?:\s[^>]*)?>(.*?)</w:t>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ParaRe = new(@"</w:p>", RegexOptions.Compiled);

    public static string Extract(string path) => Extract(File.ReadAllBytes(path), Path.GetExtension(path));

    public static string Extract(byte[] bytes, string ext)
    {
        var e = (ext ?? "").ToLowerInvariant();
        string t;
        if (e == ".docx") t = ExtractDocx(bytes);
        else if (e == ".txt") t = ExtractTxt(bytes);
        else t = ExtractDocBinary(bytes);

        t = t.Replace("\r", "\n").Replace("\x07", "\t").Replace("\x0b", "\n");
        t = CtrlRe.Replace(t, "");
        return t;
    }

    // ------------------------------------------------------------------
    // .txt
    // ------------------------------------------------------------------
    private static string ExtractTxt(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var sr = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }

    // ------------------------------------------------------------------
    // .docx
    // ------------------------------------------------------------------
    private static string ExtractDocx(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml")
                    ?? zip.Entries.FirstOrDefault(x => x.FullName.EndsWith("document.xml", StringComparison.OrdinalIgnoreCase));
        if (entry == null) throw new InvalidOperationException("docx 中未找到 word/document.xml");
        string xml;
        using (var s = entry.Open())
        using (var sr = new StreamReader(s, Encoding.UTF8))
            xml = sr.ReadToEnd();

        xml = TblRe.Replace(xml, ""); // 剔除表格块（对齐 python-docx paragraphs 语义）
        var sb = new StringBuilder();
        foreach (var para in ParaRe.Split(xml))
        {
            var text = "";
            foreach (Match m in WtRe.Matches(para)) text += XmlUnescape(m.Groups[1].Value);
            sb.Append(text).Append('\n');
        }
        return sb.ToString();
    }

    private static string XmlUnescape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'");
        s = Regex.Replace(s, @"&#x([0-9A-Fa-f]+);", m => char.ConvertFromUtf32(Convert.ToInt32(m.Groups[1].Value, 16)));
        s = Regex.Replace(s, @"&#(\d+);", m => char.ConvertFromUtf32(int.Parse(m.Groups[1].Value)));
        return s.Replace("&amp;", "&");
    }

    // ------------------------------------------------------------------
    // .doc / .wps（Word 97-2003 二进制）
    // ------------------------------------------------------------------
    private static ushort U16(byte[] b, int off) => (ushort)(b[off] | (b[off + 1] << 8));

    private static uint U32(byte[] b, int off)
        => (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));

    private static byte[] Slice(byte[] b, int start, int count)
    {
        if (start < 0) start = 0;
        if (start > b.Length) start = b.Length;
        if (count < 0) count = 0;
        if (start + count > b.Length) count = b.Length - start;
        var outB = new byte[count];
        Array.Copy(b, start, outB, 0, count);
        return outB;
    }

    private static byte[] ReadOleStream(POIFSFileSystem fs, string name)
    {
        try
        {
            using var s = fs.CreateDocumentInputStream(name);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"OLE 流 {name} 读取失败: {e.Message}");
        }
    }

    private static string ExtractDocBinary(byte[] bytes)
    {
        var cp936 = Encoding.GetEncoding(936);
        using var ms = new MemoryStream(bytes);
        var fs = new POIFSFileSystem(ms);

        var wd = ReadOleStream(fs, "WordDocument");
        var flags = U16(wd, 0x0A);
        var tblName = (flags & 0x0200) != 0 ? "1Table" : "0Table";
        byte[] tbl;
        try { tbl = ReadOleStream(fs, tblName); }
        catch { tbl = ReadOleStream(fs, tblName == "0Table" ? "1Table" : "0Table"); }

        var fcClx = (int)U32(wd, 0x01A2);
        var lcbClx = (int)U32(wd, 0x01A6);
        var clx = Slice(tbl, fcClx, lcbClx);

        // 跳过 Prc(0x01) 块，找 Pcdt(0x02)
        var i = 0;
        byte[] plc = null;
        while (i < clx.Length)
        {
            var t = clx[i];
            if (t == 0x01)
            {
                if (i + 3 > clx.Length) break;
                var cb = U16(clx, i + 1);
                i += 3 + cb;
            }
            else if (t == 0x02)
            {
                if (i + 5 > clx.Length) break;
                var lcb = (int)U32(clx, i + 1);
                plc = Slice(clx, i + 5, lcb);
                break;
            }
            else break;
        }
        if (plc == null) throw new InvalidOperationException("未找到 Pcdt");

        var n = (plc.Length - 4) / 12;
        if (n <= 0) throw new InvalidOperationException("piece table 为空");

        var cps = new uint[n + 1];
        for (var k = 0; k <= n; k++) cps[k] = U32(plc, 4 * k);

        var sb = new StringBuilder();
        for (var k = 0; k < n; k++)
        {
            var pcdOff = 4 * (n + 1) + 8 * k;
            if (pcdOff + 8 > plc.Length) break;
            var fc = U32(plc, pcdOff + 2);
            var cch = (int)cps[k + 1] - (int)cps[k];
            if (cch <= 0) continue;
            var compressed = (fc & 0x40000000) != 0;
            var off = (int)(fc & 0x3FFFFFFF);
            if (compressed)
            {
                var raw = Slice(wd, off, cch);
                sb.Append(cp936.GetString(raw));
            }
            else
            {
                var raw = Slice(wd, off, cch * 2);
                sb.Append(Encoding.Unicode.GetString(raw));
            }
        }
        return sb.ToString();
    }
}
