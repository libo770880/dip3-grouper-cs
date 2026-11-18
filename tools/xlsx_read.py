# -*- coding: utf-8 -*-
"""极简 xlsx 读取（只用标准库）：解 zip → 解析 sharedStrings + sheet XML → 打印结构。

只用于「看结构 / 抽数据」，不写文件。避免依赖 openpyxl。
"""
import re
import sys
import zipfile
import xml.etree.ElementTree as ET

NS = "{http://schemas.openxmlformats.org/spreadsheetml/2006/main}"
RNS = "{http://schemas.openxmlformats.org/officeDocument/2006/relationships}"


def _text(node):
    """<si>/<is> 里的全部 <t> 文本拼接（富文本会拆成多个 run）。"""
    if node is None:
        return ""
    return "".join(t.text or "" for t in node.iter(NS + "t"))


def col_index(ref):
    """A1 → 0，AB12 → 27。"""
    m = re.match(r"([A-Z]+)", ref)
    if not m:
        return 0
    n = 0
    for ch in m.group(1):
        n = n * 26 + (ord(ch) - 64)
    return n - 1


def read_book(path):
    z = zipfile.ZipFile(path)
    names = z.namelist()

    # sharedStrings
    shared = []
    if "xl/sharedStrings.xml" in names:
        root = ET.fromstring(z.read("xl/sharedStrings.xml"))
        shared = [_text(si) for si in root.findall(NS + "si")]

    # workbook.xml → sheet 名 + rId
    wb = ET.fromstring(z.read("xl/workbook.xml"))
    rels = ET.fromstring(z.read("xl/_rels/workbook.xml.rels"))
    rid2target = {}
    for rel in rels:
        rid2target[rel.get("Id")] = rel.get("Target")

    sheets = []
    for sh in wb.find(NS + "sheets"):
        rid = sh.get(RNS + "id")
        target = rid2target.get(rid, "")
        if not target.startswith("xl/"):
            target = "xl/" + target.lstrip("/")
        sheets.append((sh.get("name"), target, sh.get("state") or "visible"))

    out = []
    for name, target, state in sheets:
        if target not in names:
            out.append((name, state, [], []))
            continue
        root = ET.fromstring(z.read(target))
        rows = []
        for row in root.iter(NS + "row"):
            cells = {}
            for c in row.findall(NS + "c"):
                ref = c.get("r") or ""
                t = c.get("t")
                v = c.find(NS + "v")
                isel = c.find(NS + "is")
                if t == "s" and v is not None:
                    idx = int(v.text)
                    val = shared[idx] if 0 <= idx < len(shared) else ""
                elif t == "inlineStr":
                    val = _text(isel)
                elif t == "str":
                    val = v.text if v is not None else ""
                else:
                    val = v.text if v is not None else ""
                cells[col_index(ref)] = (val or "").strip()
            if cells:
                width = max(cells) + 1
                rows.append([cells.get(i, "") for i in range(width)])
        merges = [m.get("ref") for m in root.iter(NS + "mergeCell")]
        out.append((name, state, rows, merges))
    return out


if __name__ == "__main__":
    path = sys.argv[1]
    mode = sys.argv[2] if len(sys.argv) > 2 else "brief"
    for name, state, rows, merges in read_book(path):
        print(f"\n===== SHEET: {name}  state={state}  rows={len(rows)}  merges={len(merges)} =====")
        if mode == "brief":
            for i, r in enumerate(rows[:8]):
                cells = " | ".join(c if c else "" for c in r)
                print(f"  r{i + 1}: {cells[:400]}")
            if len(rows) > 8:
                print(f"  ... (共 {len(rows)} 行)")
                for i, r in enumerate(rows[-3:]):
                    print(f"  r{len(rows) - 3 + i + 1}: " + " | ".join(r)[:400])
            if merges:
                print("  merges:", ", ".join(merges[:20]))
