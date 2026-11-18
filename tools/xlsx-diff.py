#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
xlsx 语义比较：按 sheet 名 + 单元格地址 比较文本内容，忽略样式/压缩差异。
用法: python tools/xlsx-diff.py A.xlsx B.xlsx
退出码: 0 一致 / 1 有差异
"""
import sys
import re
import zipfile
import xml.etree.ElementTree as ET

NS = {'m': 'http://schemas.openxmlformats.org/spreadsheetml/2006/main',
      'r': 'http://schemas.openxmlformats.org/officeDocument/2006/relationships'}


def shared_strings(z):
    try:
        root = ET.fromstring(z.read('xl/sharedStrings.xml'))
    except KeyError:
        return []
    out = []
    for si in root.findall('m:si', NS):
        parts = [t.text or '' for t in si.iter('{%s}t' % NS['m'])]
        out.append(''.join(parts))
    return out


def sheet_order(z):
    """返回 [(sheetName, zipPath), ...]（按 workbook 声明顺序）"""
    wb = ET.fromstring(z.read('xl/workbook.xml'))
    rels = ET.fromstring(z.read('xl/_rels/workbook.xml.rels'))
    rid2target = {}
    for rel in rels:
        rid2target[rel.get('Id')] = rel.get('Target')
    out = []
    sheets_el = wb.find('m:sheets', NS)
    for sh in (sheets_el if sheets_el is not None else []):
        name = sh.get('name')
        rid = sh.get('{%s}id' % NS['r'])
        target = rid2target.get(rid, '')
        target = target[1:] if target.startswith('/') else ('xl/' + target.lstrip('./'))
        out.append((name, target))
    return out


def cell_text(c, sst):
    t = c.get('t')
    if t == 's':
        v = c.find('m:v', NS)
        if v is None:
            return ''
        idx = int(v.text)
        return sst[idx] if 0 <= idx < len(sst) else ''
    if t == 'inlineStr':
        is_el = c.find('m:is', NS)
        if is_el is None:
            return ''
        return ''.join(x.text or '' for x in is_el.iter('{%s}t' % NS['m']))
    v = c.find('m:v', NS)
    return (v.text or '') if v is not None else ''


def col_row(ref):
    m = re.match(r'^([A-Z]+)(\d+)$', ref or '')
    return (m.group(1), int(m.group(2))) if m else (ref, 0)


def read_sheet(z, path, sst):
    root = ET.fromstring(z.read(path))
    cells = {}
    for c in root.iter('{%s}c' % NS['m']):
        ref = c.get('r')
        txt = cell_text(c, sst)
        if txt == '':
            continue
        cells[ref] = txt
    merged = []
    mc = root.find('m:mergeCells', NS)
    if mc is not None:
        merged = sorted(x.get('ref') for x in mc)
    return cells, merged


def read_file(path):
    z = zipfile.ZipFile(path)
    sst = shared_strings(z)
    sheets = {}
    for name, tpath in sheet_order(z):
        try:
            sheets[name] = read_sheet(z, tpath, sst)
        except KeyError:
            sheets[name] = ({}, [])
    return sheets


def main():
    a, b = read_file(sys.argv[1]), read_file(sys.argv[2])
    diffs = []
    na, nb = list(a.keys()), list(b.keys())
    if na != nb:
        diffs.append('sheet 列表不同: A=%s B=%s' % (na, nb))
    for name in [x for x in na if x in b]:
        (ca, ma), (cb, mb) = a[name], b[name]
        if ma != mb:
            diffs.append('%s: 合并单元格不同 A=%d项 B=%d项' % (name, len(ma), len(mb)))
        keys = set(ca) | set(cb)
        for k in sorted(keys, key=lambda x: (col_row(x)[1], col_row(x)[0])):
            va, vb = ca.get(k), cb.get(k)
            if va != vb:
                diffs.append('%s!%s: A=%r B=%r' % (name, k, va, vb))
    if diffs:
        print('\n'.join(diffs[:40]))
        if len(diffs) > 40:
            print('... 另 %d 处' % (len(diffs) - 40))
        sys.exit(1)
    total = sum(len(a[n][0]) for n in a)
    print('一致 (%d sheet, %d 个非空单元格)' % (len(a), total))
    sys.exit(0)


if __name__ == '__main__':
    main()
