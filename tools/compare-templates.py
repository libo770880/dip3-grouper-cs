#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
模板导出语义对拍（Node 版 ↔ C# 版）
   用法: python tools/compare-templates.py [nodeBase] [csBase]
   默认: nodeBase=http://localhost:3210  csBase=http://localhost:3220

比较各模板接口返回的 xlsx 单元格内容（忽略样式与压缩差异）。
"""
import sys
import os
import urllib.request
import xml.etree.ElementTree as ET
import zipfile
import io
import re

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

NS = '{http://schemas.openxmlformats.org/spreadsheetml/2006/main}'

PATHS = [
    '/api/batch-template',
    '/api/admin/diseases-template?tab=xianqi',
    '/api/admin/diseases-template?tab=bingxiang',
    '/api/admin/diseases-template?tab=fz_shaoshang',
    '/api/admin/diseases-template?tab=fz_zhongliu',
    '/api/admin/diseases-template?tab=fz_jiehe',
    '/api/admin/diseases-template?tab=jiehe_resist_codes',
    '/api/admin/diseases-template?tab=jichu',
    '/api/admin/diseases-template?tab=excludedDiags',
    '/api/admin/diseases-template?tab=excludedProcs',
    '/api/admin/diseases-template?tab=jiceng',
    '/api/admin/zonghe-template',
    '/api/admin/lixiang-template',
]


def fetch(url):
    with urllib.request.urlopen(url, timeout=60) as r:
        return r.status, r.read()


def shared_strings(z):
    try:
        root = ET.fromstring(z.read('xl/sharedStrings.xml'))
    except KeyError:
        return []
    return [''.join(t.text or '' for t in si.iter(NS + 't')) for si in root]


def sheet_paths(z):
    wb = ET.fromstring(z.read('xl/workbook.xml'))
    try:
        rels = ET.fromstring(z.read('xl/_rels/workbook.xml.rels'))
    except KeyError:
        rels = None
    rid2t = {}
    if rels is not None:
        for rel in rels:
            rid2t[rel.get('Id')] = rel.get('Target')
    out = []
    sheets_el = wb.find(NS + 'sheets')
    for sh in (sheets_el if sheets_el is not None else []):
        name = sh.get('name')
        rid = sh.get('{http://schemas.openxmlformats.org/officeDocument/2006/relationships}id')
        t = rid2t.get(rid, '')
        out.append((name, t.lstrip('/')))
    # 兜底：按声明顺序映射 sheetN.xml
    if not all(t for _, t in out):
        out = [(n, 'xl/worksheets/sheet%d.xml' % (i + 1)) for i, (n, _) in enumerate(out)]
    return out


def cell_val(c, sst):
    t = c.get('t')
    if t == 's':
        v = c.find(NS + 'v')
        if v is None:
            return ''
        i = int(v.text)
        return sst[i] if 0 <= i < len(sst) else ''
    if t == 'inlineStr':
        is_el = c.find(NS + 'is')
        return ''.join(x.text or '' for x in is_el.iter(NS + 't')) if is_el is not None else ''
    v = c.find(NS + 'v')
    return (v.text or '') if v is not None else ''


def read_xlsx(data):
    z = zipfile.ZipFile(io.BytesIO(data))
    sst = shared_strings(z)
    sheets = {}
    for name, tpath in sheet_paths(z):
        try:
            root = ET.fromstring(z.read(tpath))
        except KeyError:
            sheets[name] = {}
            continue
        cells = {}
        for c in root.iter(NS + 'c'):
            v = cell_val(c, sst)
            if v:
                cells[c.get('r')] = v
        sheets[name] = cells
    return sheets


def ref_key(ref):
    m = re.match(r'^([A-Z]+)(\d+)$', ref or '')
    return (int(m.group(2)), m.group(1)) if m else (0, ref or '')


def diff(a, b):
    out = []
    if list(a.keys()) != list(b.keys()):
        out.append('sheet 列表不同: A=%s B=%s' % (list(a.keys()), list(b.keys())))
    for name in [x for x in a if x in b]:
        ca, cb = a[name], b[name]
        keys = set(ca) | set(cb)
        for k in sorted(keys, key=ref_key):
            if ca.get(k) != cb.get(k):
                out.append('%s!%s: A=%r B=%r' % (name, k, ca.get(k), cb.get(k)))
    return out


def main():
    node_base = sys.argv[1] if len(sys.argv) > 1 else 'http://localhost:3210'
    cs_base = sys.argv[2] if len(sys.argv) > 2 else 'http://localhost:3220'
    ok = fail = 0
    for p in PATHS:
        try:
            sa, ba = fetch(node_base + p)
            sb, bb = fetch(cs_base + p)
        except Exception as e:
            print('  ! %-52s 请求失败: %s' % (p, e))
            fail += 1
            continue
        if sa != sb:
            print('  x %-52s HTTP %s/%s' % (p, sa, sb))
            fail += 1
            continue
        d = diff(read_xlsx(ba), read_xlsx(bb))
        if d:
            fail += 1
            print('  x %-52s (node %dB / cs %dB) %d 处差异' % (p, len(ba), len(bb), len(d)))
            for x in d[:8]:
                print('        ' + x)
        else:
            ok += 1
            print('  v %-52s (node %dB / cs %dB) 语义一致' % (p, len(ba), len(bb)))
    print('\n通过 %d / 失败 %d' % (ok, fail))
    sys.exit(1 if fail else 0)


if __name__ == '__main__':
    main()
