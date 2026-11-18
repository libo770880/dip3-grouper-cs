#!/usr/bin/env node
/**
 * Node 版 ↔ C# 版 接口对拍脚本
 *   用法: node tools/compare-api.mjs [nodeBase] [csBase]
 *   默认: nodeBase=http://localhost:3210  csBase=http://localhost:3220
 *
 * 覆盖范围: 全部只读接口 + /api/group + /api/batch。
 * 比较语义: JSON 结构深比较（对象键无关顺序，数组顺序敏感）。
 */

const NODE_BASE = process.argv[2] || 'http://localhost:3210';
const CS_BASE = process.argv[3] || 'http://localhost:3220';

import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

const TABS = ['xianqi', 'bingxiang', 'fz_shaoshang', 'fz_zhongliu', 'fz_jiehe', 'jiehe_resist_codes', 'jichu', 'excludedDiags', 'excludedProcs', 'jiceng'];

const GETS = [
  '/api/admin/status',
  '/api/admin/config',
  '/api/admin/diseases-counts',
  ...TABS.map((t) => `/api/admin/diseases?tab=${t}&page=1&pageSize=20`),
  '/api/admin/diseases?tab=jichu&q=伤寒',
  '/api/admin/diseases?tab=bingxiang&q=A09',
  '/api/admin/diseases?tab=bingxiang&page=2&pageSize=7',
  '/api/admin/diseases?tab=jiceng&q=肺炎',
  '/api/admin/mapping?page=1&pageSize=50',
  '/api/admin/mapping?q=伤寒',
  '/api/admin/mapping?q=J18',
  '/api/admin/mapping?gray=1&page=1&pageSize=20',
  '/api/admin/mapping?gray=0&page=1&pageSize=20',
  '/api/admin/zonghe',
  '/api/admin/zonghe?page=1&pageSize=20',
  '/api/lixiang/sections',
  '/api/lixiang/items?page=1&pageSize=20',
  '/api/lixiang/items?q=针灸',
  '/api/lixiang/items?q=针',
  '/api/lixiang/jiedu',
  '/api/lixiang/jiedu?issue=1',
  '/api/lixiang/jiedu?q=价格',
  '/api/suggest-diag?q=伤寒',
  '/api/suggest-diag?q=A09',
  '/api/suggest-diag?q=A09.0',
  '/api/suggest-diag?q=shanghan',
  '/api/suggest-diag?q=肺炎&limit=5',
  '/api/suggest-proc?q=44.13',
  '/api/suggest-proc?q=胃镜',
  '/api/suggest-proc?q=weijing',
  '/api/suggest-proc?q=45',
  '/api/suggest-linked?mainDiag=A09.0',
  '/api/suggest-linked?mainDiag=T31.0',
  '/api/suggest-linked?mainDiag=Z51.1',
  '/api/suggest-linked?mainDiag=C50.9',
  '/api/translate?code=A09.0',
  '/api/translate?code=J18.9',
  '/api/translate?code=C00',
  '/api/translate?code=',
];

/** 单条分组样本（覆盖 先期/烧伤/肿瘤/结核/并项/基础/基层/综合/排除 全链路） */
const CASES = [
  { caseId: 'S01', mainDiag: 'A09.0', otherDiags: [] },
  { caseId: 'S02', mainDiag: 'A09.0', mainProc: '44.1300x001' },
  { caseId: 'S03', mainDiag: 'A01.0', otherDiags: ['A09.0'] },
  { caseId: 'S04', mainDiag: 'T20.2', otherDiags: ['T31.0'], mainProc: '86.2200' },
  { caseId: 'S05', mainDiag: 'Z51.1', otherDiags: ['C50.9'], mainProc: '99.2503' },
  { caseId: 'S06', mainDiag: 'A15.0', mainProc: '31.9400' },
  { caseId: 'S07', mainDiag: 'A15.0' },
  { caseId: 'S08', mainDiag: 'A16.0' },
  { caseId: 'S09', mainDiag: 'P07.2', ageDays: 20, birthWeight: 1200 },
  { caseId: 'S10', mainDiag: 'A09.9' },
  { caseId: 'S11', mainDiag: 'C50.9', mainProc: '85.4100' },
  { caseId: 'S12', mainDiag: 'J18.9', mainProc: '93.9000' },
  { caseId: 'S13', mainDiag: 'Z51.1', otherDiags: ['C18.9'], mainProc: '99.2503' },
  { caseId: 'S14', mainDiag: 'A15.0', otherDiags: ['U84.300'], mainProc: '31.9400' },
  { caseId: 'S15', mainDiag: 'S06.0', mainProc: '01.2400x005' },
  { caseId: 'S16', mainDiag: 'I63.9', mainProc: '99.1000' },
  { caseId: 'S17', mainDiag: 'A09.0', otherDiags: 'A09.0|A01.0;J18.9' },
  { caseId: 'S18', mainDiag: 'O80.0', mainProc: '73.5900' },
  { caseId: 'S19', mainDiag: 'K35.9', mainProc: '47.0900' },
  { caseId: 'S20', mainDiag: 'E11.9' },
  { caseId: 'S21', mainDiag: 'M17.9', mainProc: '81.5400' },
  { caseId: 'S22', mainDiag: 'A09.000x001' },
  { caseId: 'S23', mainDiag: '25.2x00' },
  { caseId: 'S24', mainDiag: 'A09.0', mainProc: '25.2x00x001' },
  { caseId: 'S25', mainDiag: 'C00', otherDiags: ['C75'], mainProc: '99.2503' },
  { caseId: 'S26', mainDiag: 'A09.0', mainProc: '44.13' },
  { caseId: 'S27', mainDiag: 'A09.0', mainProc: '96.7100x001' },
  { caseId: 'S28', mainDiag: '' },
  { caseId: 'S29', mainDiag: 'ZZZ.9' },
  { caseId: 'S30', mainDiag: 'A09.0', otherDiags: ['A09.0', 'A01.0', 'A01.0'] },
];

// 结核先期/区间边界
for (const d of ['A15', 'A15.0', 'A16.9', 'A17.0', 'A18.0', 'A19', 'A15-A16']) CASES.push({ caseId: `TB-${d}`, mainDiag: d, mainProc: '31.9400' });
// 烧伤程度/面积组合
for (const d of ['T20.2', 'T21.2', 'T22.2', 'T23.2', 'T24.2', 'T25.2', 'T26.2', 'T27.2', 'T28.2', 'T29.2', 'T30.2', 'T31.0', 'T32.0']) CASES.push({ caseId: `SS-${d}`, mainDiag: d, otherDiags: ['T31.0'], mainProc: '86.2200' });

async function get(base, path) {
  const r = await fetch(base + path);
  const text = await r.text();
  let json = null;
  try { json = JSON.parse(text); } catch { /* 非 JSON */ }
  return { status: r.status, json, text };
}

async function post(base, path, body) {
  const r = await fetch(base + path, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
  });
  const text = await r.text();
  let json = null;
  try { json = JSON.parse(text); } catch { /* 非 JSON */ }
  return { status: r.status, json, text };
}

function diff(a, b, pathStr = '') {
  const out = [];
  if (a === b) return out;
  if (typeof a !== typeof b) { out.push(`${pathStr}: 类型不同 node=${typeof a} cs=${typeof b}`); return out; }
  if (a === null || b === null) { out.push(`${pathStr}: node=${JSON.stringify(a)} cs=${JSON.stringify(b)}`); return out; }
  if (Array.isArray(a) !== Array.isArray(b)) { out.push(`${pathStr}: 数组性不同`); return out; }
  if (Array.isArray(a)) {
    if (a.length !== b.length) out.push(`${pathStr}.length: node=${a.length} cs=${b.length}`);
    const n = Math.min(a.length, b.length);
    for (let i = 0; i < n; i++) out.push(...diff(a[i], b[i], `${pathStr}[${i}]`));
    return out;
  }
  if (typeof a === 'object') {
    const ka = Object.keys(a).sort();
    const kb = Object.keys(b).sort();
    const onlyA = ka.filter((k) => !kb.includes(k));
    const onlyB = kb.filter((k) => !ka.includes(k));
    if (onlyA.length) out.push(`${pathStr}: 仅 node 有键 [${onlyA.join(',')}]`);
    if (onlyB.length) out.push(`${pathStr}: 仅 cs 有键 [${onlyB.join(',')}]`);
    for (const k of ka.filter((x) => kb.includes(x))) out.push(...diff(a[k], b[k], `${pathStr}.${k}`));
    return out;
  }
  out.push(`${pathStr}: node=${JSON.stringify(a)} cs=${JSON.stringify(b)}`);
  return out;
}

let pass = 0, fail = 0;
const failures = [];

function report(label, d) {
  if (d.length === 0) { pass++; console.log(`  ✓ ${label}`); }
  else {
    fail++;
    failures.push({ label, d });
    console.log(`  ✗ ${label}  (${d.length} 处差异)`);
    d.slice(0, 6).forEach((x) => console.log(`      ${x}`));
    if (d.length > 6) console.log(`      ... 另有 ${d.length - 6} 处`);
  }
}

(async () => {
  console.log(`\n=== GET 接口对拍 (${GETS.length} 条) ===`);
  for (const p of GETS) {
    const [a, b] = await Promise.all([get(NODE_BASE, p), get(CS_BASE, p)]);
    const label = `GET ${p}`;
    if (a.status !== b.status) { report(label, [`HTTP 状态: node=${a.status} cs=${b.status}`]); continue; }
    if (a.json === null || b.json === null) {
      // 非 JSON（如 404 文本）：两边原文相同即视为一致
      report(label, a.text === b.text ? [] : [`非 JSON 响应 node=${a.text.slice(0, 80)} cs=${b.text.slice(0, 80)}`]);
      continue;
    }
    report(label, diff(a.json, b.json));
  }

  console.log(`\n=== POST /api/group 单条对拍 (${CASES.length} 条) ===`);
  for (const c of CASES) {
    const [a, b] = await Promise.all([post(NODE_BASE, '/api/group', c), post(CS_BASE, '/api/group', c)]);
    report(`group ${c.caseId} (${c.mainDiag || '-'}${c.mainProc ? ' + ' + c.mainProc : ''})`, diff(a.json, b.json));
  }

  console.log(`\n=== POST /api/group (useMapping=true) 对拍 ===`);
  for (const c of CASES.filter((x) => x.mainDiag)) {
    const [a, b] = await Promise.all([
      post(NODE_BASE, '/api/group', { case: c, useMapping: true }),
      post(CS_BASE, '/api/group', { case: c, useMapping: true }),
    ]);
    report(`group+map ${c.caseId}`, diff(a.json, b.json));
  }

  console.log(`\n=== POST /api/batch 批量对拍 ===`);
  for (const useMapping of [false, true]) {
    const [a, b] = await Promise.all([
      post(NODE_BASE, '/api/batch', { rows: CASES, useMapping }),
      post(CS_BASE, '/api/batch', { rows: CASES, useMapping }),
    ]);
    const strip = (o) => { if (o && typeof o === 'object') delete o.token; return o; };
    report(`batch (${CASES.length} 条, useMapping=${useMapping})`, diff(strip(a.json), strip(b.json)));
  }

  console.log(`\n=== 模板导出语义对拍 ===`);
  console.log('  模板 xlsx 需解压比对，由 tools/compare-templates.py 单独执行:');
  console.log('      python tools/compare-templates.py ' + NODE_BASE + ' ' + CS_BASE);

  console.log(`\n=== 汇总 ===`);
  console.log(`通过 ${pass} / 失败 ${fail}`);
  if (fail) {
    console.log('\n失败清单:');
    failures.forEach((f) => console.log(`  - ${f.label}`));
    process.exitCode = 1;
  } else {
    console.log('全部一致 ✔');
  }
})();
