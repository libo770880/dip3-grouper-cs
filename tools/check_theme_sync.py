# -*- coding: utf-8 -*-
"""
校验网页版五套主题的色值是否与桌面端 ThemeCatalog 一致。

为什么要有这个脚本：两端各存一份色值（C# 常量 / CSS 变量），改一边忘了另一边，
表现是「桌面换到青碧，网页还是经典蓝的强调色」——肉眼要两屏对比才发现。
事后补一致性检查，比靠记性靠谱。

用法：python tools/check_theme_sync.py     退出码 0 = 一致，1 = 有差异
"""
import re, sys, io, os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CS = os.path.join(ROOT, 'src/Dip3Grouper.Desktop/Theming/ThemeManager.cs')
HTML = os.path.join(ROOT, 'web/index.html')

# 两端同名同义的键（C# Map 键 → CSS 变量）
KEYS = {
    'Bg': 'bg', 'Card': 'card', 'Border': 'border', 'BorderStrong': 'border-strong',
    'SoftBg': 'soft-bg', 'SegBg': 'seg-bg', 'Text': 'text', 'Muted': 'muted',
    'Faint': 'faint', 'InfoText': 'info-text', 'Accent': 'accent',
    'AccentHover': 'accent-hover', 'AccentSoft': 'accent-soft',
    'Ok': 'ok', 'Warn': 'warn', 'Danger': 'danger',
    'InfoBg': 'info-bg', 'InfoBorder': 'info-border', 'WarnBg': 'warn-bg',
    'RowAlt': 'row-alt', 'RowHover': 'row-hover', 'ScrollThumb': 'scroll-thumb',
    'LogoA': 'logo-a', 'LogoB': 'logo-b', 'LogoC': 'logo-c',
}

def parse_cs():
    src = io.open(CS, encoding='utf-8').read()
    out = {}
    for blk in re.findall(r'new ThemeDef\s*\{(.*?)\n        \},', src, re.S):
        m = re.search(r'Id\s*=\s*"(\w+)"', blk)
        if not m:
            continue
        tid = m.group(1)
        pairs = dict(re.findall(r'"([A-Za-z]+)",\s*"(#[0-9a-fA-F]{6})"', blk))
        out[tid] = pairs
    return out

def parse_css():
    src = io.open(HTML, encoding='utf-8').read()
    def varmap(text):
        return {k: v.lower() for k, v in re.findall(r'--([a-z-]+)\s*:\s*(#[0-9a-fA-F]{6})', text)}
    root = re.search(r':root \{(.*?)\n  \}', src, re.S)
    base = varmap(root.group(1)) if root else {}
    themes = {'azure': dict(base)}
    for tid, body in re.findall(r'html\[data-theme="(\w+)"\] \{(.*?)\n  \}', src, re.S):
        d = dict(base)
        d.update(varmap(body))
        themes[tid] = d
    return themes

def main():
    cs, css = parse_cs(), parse_css()
    bad = []
    for tid, m in cs.items():
        if tid not in css:
            bad.append(f'{tid}: 网页版缺少这套主题')
            continue
        for key, var in KEYS.items():
            if key not in m:
                continue
            a = m[key].lower()
            b = css[tid].get(var)
            if b != a:
                bad.append(f'{tid}.{var}: 桌面 {a} ≠ 网页 {b}')
    # 色卡预览色也必须等于主题本色（否则「看的是青碧、选出来是墨绿」）
    html = io.open(HTML, encoding='utf-8').read()
    for tid, bg, card, accent in re.findall(
            r"id: '(\w+)',\s*name: '[^']*',\s*hint: '[^']*',\s*sw: \['(#[0-9a-fA-F]{6})', '(#[0-9a-fA-F]{6})', '(#[0-9a-fA-F]{6})'\]", html):
        if tid not in css:
            continue
        for key, val in (('bg', bg), ('card', card), ('accent', accent)):
            if css[tid].get(key) != val.lower():
                bad.append(f'{tid}.色卡预览.{key}: {val.lower()} ≠ 主题 {css[tid].get(key)}')

    if bad:
        print('主题色值不一致：')
        for x in bad:
            print('  -', x)
        return 1
    print(f'五套主题 × {len(KEYS)} 个共用键，桌面与网页色值一致')
    return 0

if __name__ == '__main__':
    sys.exit(main())
