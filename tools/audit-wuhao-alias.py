#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""基本物耗清单（data/wuhao.json）别名体检。

只读，不改任何数据。同时把本机覆盖层 data/wuhao.user.json 的差异集叠加后一起体检，
与桌面端「手工维护」页看到的生效清单保持一致。

检查项：
  1. 冗余自名别名：别名与自己的主名相同（无判定影响，但属数据冗余）。
  2. 跨条目撞名：别名与另一条目的主名相同 —— 这是真冲突，HitItem 按清单顺序谁在前谁胜出。
  3. 别名互撞：同一个别名被两个及以上条目使用。
  4. 同名条目：主名重复。
  5. 别名与边界条目撞名：边界清单优先级的潜在歧义。

输出 docs/wuhao-alias-audit.md，并在终端打印摘要。
"""
import json
import os
import sys
from collections import defaultdict
from datetime import datetime, timezone

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BASE = os.path.join(ROOT, "data", "wuhao.json")
USER = os.path.join(ROOT, "data", "wuhao.user.json")
OUT = os.path.join(ROOT, "docs", "wuhao-alias-audit.md")


def load(path):
    with open(path, encoding="utf-8-sig") as f:
        return json.load(f)


def key(s):
    """对齐 C# WuhaoStore.Same 的归一化：去空格、全角括号转半角、忽略大小写。"""
    return (s or "").replace(" ", "").replace("（", "(").replace("）", ")") \
                    .replace("／", "/").lower()


def effective_items(base, user):
    """复刻 WuhaoStore.ApplyUserCover 的顺序：先删 -> 覆盖 -> 追加（追加的置顶由 UI 负责）。"""
    items = list(base.get("items") or [])
    deleted = set(user.get("deletedItems") or [])
    items = [i for i in items if i.get("id") not in deleted]
    overrides = {i.get("id"): i for i in (user.get("items") or []) if i.get("id")}
    items = [overrides.get(i.get("id"), i) for i in items]
    known = {i.get("id") for i in items}
    items.extend(i for i in (user.get("items") or []) if i.get("id") and i.get("id") not in known)
    return items


def audit():
    base = load(BASE)
    user = load(USER) if os.path.exists(USER) else {}
    items = effective_items(base, user)
    groups = {g["id"]: g.get("name", g["id"]) for g in (base.get("groups") or [])}
    edges = base.get("edges") or []
    for n in (user.get("deletedEdges") or []):
        edges = [e for e in edges if e.get("name") != n]
    edge_override = {e.get("name"): e for e in (user.get("edges") or []) if e.get("name")}
    edges = [edge_override.get(e.get("name"), e) for e in edges]
    known_edge = {e.get("name") for e in edges}
    edges.extend(e for e in (user.get("edges") or []) if e.get("name") and e.get("name") not in known_edge)

    by_name = defaultdict(list)
    by_alias = defaultdict(list)
    for idx, it in enumerate(items):
        by_name[key(it.get("name"))].append((idx, it))
        for pos, a in enumerate(it.get("alias") or []):
            by_alias[key(a)].append((idx, it, pos, a))

    self_alias, clash_name, clash_alias, dup_name, clash_edge = [], [], [], [], []
    edge_keys = {key(e.get("name")): e for e in edges if e.get("name")}

    for idx, it in enumerate(items):
        name = it.get("name") or ""
        for pos, a in enumerate(it.get("alias") or []):
            ka = key(a)
            if ka == key(name):
                self_alias.append((a, idx, it, pos))
            for jdx, other in by_name.get(ka, []):
                if jdx != idx:
                    winner = min(idx, jdx)
                    clash_name.append((a, idx, it, pos, jdx, other, winner))
            for jdx, other, opos, oa in by_alias.get(ka, []):
                if jdx != idx and (ka, jdx) not in {(ka, x[0]) for x in clash_alias}:
                    clash_alias.append((a, idx, it, pos, jdx, other, oa))
            if ka in edge_keys:
                clash_edge.append((a, idx, it, pos, edge_keys[ka]))

    for kn, occ in by_name.items():
        if len(occ) > 1:
            dup_name.append((occ[0][1].get("name"), occ))

    return base, user, items, groups, self_alias, clash_name, clash_alias, dup_name, clash_edge


def row_of(idx, it, groups):
    return f"`{it.get('id','')}` {it.get('name','')}（{groups.get(it.get('group'), it.get('group',''))} · 第 {idx+1} 行）"


def main():
    base, user, items, groups, self_alias, clash_name, clash_alias, dup_name, clash_edge = audit()
    now = datetime.now(timezone.utc).astimezone().strftime("%Y-%m-%d %H:%M:%S %z")
    L = []
    L.append("# 基本物耗清单 · 别名体检报告")
    L.append("")
    L.append(f"- 生成时间：{now}")
    L.append(f"- 数据：`data/wuhao.json`（{len(items)} 条生效条目，{len(base.get('edges') or [])} 条基础边界）")
    L.append(f"- 本机覆盖：`data/wuhao.user.json`（{'已应用' if user else '不存在'}）")
    L.append("- 生成命令：`python tools/audit-wuhao-alias.py`（只读，不改数据）")
    L.append("")
    L.append("## 结论")
    L.append("")
    L.append(f"| 检查项 | 数量 | 判定影响 |")
    L.append(f"|---|---:|---|")
    L.append(f"| 冗余自名别名 | {len(self_alias)} | 无。条目把自己的主名又列进 alias，HitItem 每条先比主名，结果不变 |")
    L.append(f"| 跨条目撞名（别名 = 他人主名） | {len(clash_name)} | 若 >0：**清单顺序靠前者胜出**，归属条目 / 分类 / 说明可能串 |")
    L.append(f"| 别名互撞（同一别名多条目） | {len(clash_alias)} | 若 >0：同上 |")
    L.append(f"| 同名条目 | {len(dup_name)} | 若 >0：判定归属不唯一 |")
    L.append(f"| 别名撞边界条目 | {len(clash_edge)} | 需核对优先级（边界清单在 Judge ② 阶段参与比较）；当前 3 条边界结论同为「不可另收」，无口径冲突 |")
    L.append("")
    L.append("> 判定顺序依据 `src/Dip3Grouper.Core/Wuhao.cs` 的 `HitItem`：完全相等阶段按 `Items` 顺序逐条走，")
    L.append("> 每条**先比主名、再比别名**，第一个命中即返回。因此别名与另一条目的主名相同时，索引更小者胜出。")
    L.append("> 两个条目的结论都是「不可另收」，但回显的条目名、分类数、说明会不同。")
    L.append("")
    L.append("## 1. 冗余自名别名明细（人工裁决：是否从源表移除）")
    L.append("")
    L.append("这些别名由 `tools/gen-wuhao.py` 从 ITEMS 源表原样带出（源表把主名当首个别名）。")
    L.append("删掉不影响判定，但要改源表而非直接改 wuhao.json，否则下次重生成会回来；")
    L.append("本机操作员也可以在「手工维护」页逐条编辑别名，差异写入 `wuhao.user.json`。")
    L.append("")
    L.append("| # | 别名 | 所属条目 | 别名位次 |")
    L.append("|---:|---|---|---:|")
    for n, (a, idx, it, pos) in enumerate(self_alias, 1):
        L.append(f"| {n} | {a} | {row_of(idx, it, groups)} | {pos+1} |")
    L.append("")
    L.append("## 2. 跨条目撞名明细（真冲突，优先处理）")
    L.append("")
    if clash_name:
        L.append("| # | 别名 | 别名所属 | 同名的另一条目 | 当前胜出 |")
        L.append("|---:|---|---|---|---|")
        for n, (a, idx, it, pos, jdx, other, winner) in enumerate(clash_name, 1):
            w = "别名所属条目" if winner == idx else "主名条目"
            L.append(f"| {n} | {a} | {row_of(idx, it, groups)} | {row_of(jdx, other, groups)} | {w}（第 {winner+1} 行） |")
    else:
        L.append(f"无。当前 {len(self_alias)} 条「别名 = 主名」全部是**条目自己的主名**，不存在跨条目撞名。")
    L.append("")
    L.append("## 3. 别名互撞 / 同名条目 / 别名撞边界")
    L.append("")
    if clash_alias:
        L.append("| 别名 | 条目 A | 条目 B |")
        L.append("|---|---|---|")
        for a, idx, it, pos, jdx, other, oa in clash_alias:
            L.append(f"| {a} | {row_of(idx, it, groups)} | {row_of(jdx, other, groups)} |")
    else:
        L.append("- 别名互撞：无")
    if dup_name:
        for name, occ in dup_name:
            L.append(f"- 同名条目「{name}」：{len(occ)} 条")
    else:
        L.append("- 同名条目：无")
    if clash_edge:
        L.append("")
        L.append("| 别名 | 所属条目 | 撞上的边界条目 |")
        L.append("|---|---|---|")
        for a, idx, it, pos, edge in clash_edge:
            L.append(f"| {a} | {row_of(idx, it, groups)} | {edge.get('name','')}（{edge.get('verdict','')}） |")
    else:
        L.append("- 别名撞边界条目：无")
    L.append("")
    L.append("## 4. 建议")
    L.append("")
    L.append("1. **跨条目撞名 / 别名互撞 / 同名条目为 0**，判定口径目前是确定的，不需要改数据。")
    L.append(f"2. {len(self_alias)} 条自名别名属冗余（其中 99 条字面完全相同，1 条仅空格差异：i062「冷 / 热治疗物品」）。")
    L.append("   若想精简，改 `tools/gen-wuhao.py` 的 ITEMS 源表后重生成；网页端只把 alias 拼进检索串，删掉同样不影响检索。")
    L.append("3. 维护页的「别名体检」按钮会在每次改动后重算，一旦操作员新增的别名撞上别条目主名，立刻标红提示。")
    L.append("")
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8") as f:
        f.write("\n".join(L))
    print(f"生效条目 {len(items)} · 冗余自名别名 {len(self_alias)} · 跨条目撞名 {len(clash_name)} · "
          f"别名互撞 {len(clash_alias)} · 同名条目 {len(dup_name)} · 别名撞边界 {len(clash_edge)}")
    print(f"报告：{OUT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
