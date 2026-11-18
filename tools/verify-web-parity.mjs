#!/usr/bin/env node
/**
 * 网页端 ↔ 桌面端 / 数据层 一致性校验（一次性体检脚本，不参与构建）
 *
 *   用法: node tools/verify-web-parity.mjs [base]
 *   默认: base = http://localhost:3398（调试实例；正式实例是 3210）
 *
 * 校验六组：
 *   A 数据源对拍      —— 接口总数 = data/*.json 里的原始条数（不是拿接口自己跟自己比）
 *   B 翻页不丢不重    —— 逐页抓完，条数相加必须等于 total，且编码不重复
 *   C 筛选口径        —— 性质 / 医保 / 级别 / 比对范围 / 收藏编码，分项相加必须回总数
 *   D 病种库序号承接  —— 各区段 start 连续、区段 total 与列表 total 一致
 *   E 立项指南        —— 区段列只在首行、解读辅导条数与「答案末尾无游离题号」
 *   F 列定义对齐      —— 网页端表头逐字等于桌面端 Cols / GF_COLS（读源码，不比截图）
 *
 * 退出码：全部通过 0，有失败 1。
 */

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const BASE = (process.argv[2] || 'http://localhost:3398').replace(/\/$/, '');
const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

let pass = 0;
const failures = [];

function ok(label, extra = '') {
  pass++;
  console.log(`  ✓ ${label}${extra ? '  ' + extra : ''}`);
}
function bad(label, detail) {
  failures.push({ label, detail });
  console.log(`  ✗ ${label}`);
  for (const d of [].concat(detail).slice(0, 6)) console.log(`      ${d}`);
}
function check(label, cond, detail) {
  if (cond) ok(label, typeof detail === 'string' && !cond ? detail : '');
  else bad(label, detail || '断言不成立');
  return cond;
}

async function get(p) {
  const r = await fetch(BASE + p);
  const text = await r.text();
  let json = null;
  try { json = JSON.parse(text); } catch { /* 非 JSON */ }
  if (r.status !== 200) throw new Error(`GET ${p} → HTTP ${r.status} ${text.slice(0, 120)}`);
  if (json === null) throw new Error(`GET ${p} → 不是 JSON`);
  return json;
}

const readJson = (f) => JSON.parse(fs.readFileSync(path.join(ROOT, 'data', f), 'utf8'));
const readText = (f) => fs.readFileSync(path.join(ROOT, f), 'utf8');
const n = (x) => Number(x || 0).toLocaleString();

(async () => {
  console.log(`\n目标实例 ${BASE}\n`);

  // ---------------------------------------------------------------- A
  console.log('=== A. 数据源对拍（接口 total vs data/*.json 原始条数）===');
  const dzData = readJson('dezhou-price.json');
  const gfData = readJson('guifan.json');

  const meta = await get('/api/dezhou-meta');
  check(`现行项目 ${n(meta.stat.total)} 条`, meta.stat.total === dzData.items.length,
    `data/dezhou-price.json items=${dzData.items.length}`);
  const live = await get('/api/dezhou?view=live&pageSize=1');
  check(`/api/dezhou?view=live total=${n(live.total)}`, live.total === dzData.items.length);
  check('meta.stat.total 与 live 列表 total 一致', live.total === meta.stat.total,
    `${live.total} vs ${meta.stat.total}`);

  const cancel = await get('/api/dezhou?view=cancel&pageSize=1');
  check(`作废对照 ${n(cancel.total)} 行`, cancel.total === dzData.cancels.length,
    `cancels=${dzData.cancels.length}`);
  check('meta.cancelStat.total 与列表一致', cancel.total === meta.cancelStat.total);

  const legacy = await get('/api/dezhou?view=legacy&pageSize=1');
  check(`旧版物价 ${n(legacy.total)} 条`, legacy.total === dzData.legacy.length,
    `legacy=${dzData.legacy.length}`);
  check('meta.legacyStat.total 与列表一致', legacy.total === meta.legacyStat.total);

  const cmp = await get('/api/dezhou?view=cmp&pageSize=1');
  check(`新旧比对 ${n(cmp.total)} 行`, cmp.total === meta.cmpStat.total,
    `cmp 列表 ${cmp.total} vs meta ${meta.cmpStat.total}`);

  const gf = await get('/api/guifan?page=1&pageSize=1');
  const gfTotal = gf.total ?? gf.totalCount;
  check(`技术规范 ${n(gfTotal)} 项`, gfTotal === gfData.items.length, `guifan.json items=${gfData.items.length}`);
  const gfMeta = await get('/api/guifan-meta');
  check('guifan-meta.stat.total 与列表一致', gfMeta.stat.total === gfTotal);

  // ---------------------------------------------------------------- B
  console.log('\n=== B. 翻页不丢不重（每页 200，逐页抓完）===');
  // 作废对照 / 旧版物价里「同一编码出现多次」是真实业务（一个编码在多个批次被作废、
  // 旧版对码表里同码多行），所以不能拿「编码唯一」判丢重 —— 用源数据的行分布做多重集比对。
  const freq = (arr, keyOf) => {
    const m = new Map();
    for (const x of arr) m.set(keyOf(x), (m.get(keyOf(x)) || 0) + 1);
    return m;
  };
  const expectFreq = {
    live: freq(dzData.items, (x) => x.code),
    cancel: freq(dzData.cancels, (x) => x.oldCode),
    legacy: freq(dzData.legacy, (x) => x.code),
  };

  const views = [
    ['live', (r) => r.code, dzData.items.length],
    ['cancel', (r) => r.oldCode, dzData.cancels.length],
    ['legacy', (r) => r.code, dzData.legacy.length],
    // 比对行一个旧码可能对多条新码（或反之），单一编码不唯一 —— 用「新码|旧码」做行身份
    ['cmp', (r) => (r.newCode || '') + '|' + (r.oldCode || ''), meta.cmpStat.total],
  ];
  for (const [view, keyOf, expect] of views) {
    const size = 200;
    const pages = Math.ceil(expect / size);
    let seen = 0;
    const got = new Map();
    let lastPageRows = 0;
    for (let p = 1; p <= pages; p++) {
      const r = await get(`/api/dezhou?view=${view}&page=${p}&pageSize=${size}`);
      if (r.pageSize !== size) { bad(`${view} 第 ${p} 页 pageSize 被改成 ${r.pageSize}`, '接口没有按请求的每页条数返回'); break; }
      lastPageRows = r.rows.length;
      seen += r.rows.length;
      for (const row of r.rows) {
        const k = keyOf(row);
        got.set(k, (got.get(k) || 0) + 1);
      }
    }
    const expectLast = expect - size * (pages - 1);
    check(`${view}：${pages} 页共 ${n(seen)} 行 = total`, seen === expect, `累计 ${seen}，应为 ${expect}`);
    check(`${view}：末页 ${lastPageRows} 行 = 余数 ${expectLast}`, lastPageRows === expectLast);
    if (view === 'cmp') {
      const dup = [...got.values()].filter((v) => v > 1).length;
      check('cmp：行身份（新码|旧码）跨页不重复', dup === 0, `重复 ${dup} 个`);
    } else {
      // 与源数据逐键比对出现次数：既不许丢行，也不许把一个批次的行算到另一个批次上
      const exp = expectFreq[view];
      const missing = [...exp.keys()].filter((k) => !got.has(k));
      const extra = [...got.keys()].filter((k) => !exp.has(k));
      let countDiff = 0;
      for (const [k, v] of exp) if ((got.get(k) || 0) !== v) countDiff++;
      check(`${view}：逐键出现次数与 data/*.json 一致`,
        missing.length === 0 && extra.length === 0 && countDiff === 0,
        [`缺 ${missing.length} 个键`, `多 ${extra.length} 个键`, `${countDiff} 个键次数不符`]);
    }
  }

  // ---------------------------------------------------------------- C
  console.log('\n=== C. 筛选口径（分项相加回总数）===');
  const kinds = {};
  for (const k of ['main', 'add', 'ext']) kinds[k] = (await get(`/api/dezhou?view=live&kind=${k}&pageSize=1`)).total;
  check(`性质 主 ${n(kinds.main)} + 加收 ${n(kinds.add)} + 扩展 ${n(kinds.ext)} = ${n(meta.stat.total)}`,
    kinds.main + kinds.add + kinds.ext === meta.stat.total,
    `${kinds.main}+${kinds.add}+${kinds.ext}=${kinds.main + kinds.add + kinds.ext}`);
  check('性质分项与 meta.stat 的 main/add/ext 一致',
    kinds.main === meta.stat.main && kinds.add === meta.stat.add && kinds.ext === meta.stat.ext,
    JSON.stringify({ kinds, stat: meta.stat }));

  const insY = (await get('/api/dezhou?view=live&insurance=' + encodeURIComponent('是') + '&pageSize=1')).total;
  const insN = (await get('/api/dezhou?view=live&insurance=' + encodeURIComponent('否') + '&pageSize=1')).total;
  check(`医保支付 ${n(insY)}（meta ${n(meta.stat.insured)}）`, insY === meta.stat.insured);
  check(`医保 是 ${n(insY)} + 否 ${n(insN)} = ${n(meta.stat.total)}`, insY + insN === meta.stat.total,
    `${insY}+${insN}=${insY + insN}`);

  for (const lv of ['3', '2', '1']) {
    const t = (await get(`/api/dezhou?view=live&level=${lv}&pageSize=1`)).total;
    check(`级别 ${lv} 有价 ${n(t)} 条（≤ 总数且 > 0）`, t > 0 && t <= meta.stat.total);
  }

  const sc = {};
  for (const s of ['priced', 'cancel', 'none']) sc[s] = (await get(`/api/dezhou?view=cmp&scope=${s}&pageSize=1`)).total;
  check(`比对范围 有旧价 ${n(sc.priced)} + 仅作废 ${n(sc.cancel)} + 纯新增 ${n(sc.none)} = ${n(meta.cmpStat.total)}`,
    sc.priced + sc.cancel + sc.none === meta.cmpStat.total,
    `${sc.priced}+${sc.cancel}+${sc.none}=${sc.priced + sc.cancel + sc.none}`);
  check('比对分项与 meta.cmpStat 一致',
    sc.priced === meta.cmpStat.priced && sc.cancel === meta.cmpStat.cancelOnly && sc.none === meta.cmpStat.brandNew,
    JSON.stringify({ sc, cmpStat: meta.cmpStat }));

  // 降 / 涨 / 平 三档：差额为空的行（三档都没价，如『自主定价』文字价）不计入任何一档，
  // 所以三者之和 ≤ 有旧价可比，差额部分就是「无数值可比」的行 —— 只要求不超、不少太多。
  const trip = meta.cmpStat.down + meta.cmpStat.up + meta.cmpStat.flat;
  check(`降 ${n(meta.cmpStat.down)} + 涨 ${n(meta.cmpStat.up)} + 平 ${n(meta.cmpStat.flat)} ≤ 有旧价 ${n(meta.cmpStat.priced)}`,
    trip <= meta.cmpStat.priced && trip > 0,
    `合计 ${trip}，有旧价 ${meta.cmpStat.priced}（差 ${meta.cmpStat.priced - trip} 行为无三级价可比）`);

  // 仅看收藏：取 3 个真实编码，接口必须只回这些编码的行（同一编码跨批次各占一行，故按源数据计数）
  const sample = (await get('/api/dezhou?view=live&pageSize=3')).rows.map((r) => r.code);
  const fav = await get('/api/dezhou?view=live&codes=' + sample.map(encodeURIComponent).join(','));
  const expectFav = dzData.items.filter((i) => sample.includes(i.code)).length;
  check(`仅看收藏（codes=${sample.length} 个编码）只回这些编码的 ${expectFav} 行`,
    fav.total === expectFav && fav.rows.every((r) => sample.includes(r.code)),
    `total=${fav.total}，源数据 ${expectFav} 行`);

  // 关键词检索：拿第一条名称的前 4 个字去搜，命中行必须都含这 4 个字
  const first = (await get('/api/dezhou?view=live&pageSize=1')).rows[0];
  const needle = (first.name || '').replace(/[（）()]/g, '').slice(0, 4);
  if (needle.length >= 2) {
    const sr = await get('/api/dezhou?view=live&q=' + encodeURIComponent(needle) + '&pageSize=200');
    const all = sr.rows.every((r) => [r.code, r.name, r.category, r.doc, r.unit, r.note, r.usage, r.serviceOutput]
      .some((v) => (v || '').toLowerCase().includes(needle.toLowerCase())) || (r.code || '').startsWith(needle));
    check(`关键词「${needle}」命中 ${n(sr.total)} 条且逐行可解释`, sr.total > 0 && all, '有命中行不含关键词（拼音检索命中属正常，此处只报不拦）');
  }

  // ---------------------------------------------------------------- D
  console.log('\n=== D. 病种库序号承接 ===');
  const dc = await get('/api/diseases-counts');
  let cum = 0;
  let startOk = true;
  for (const t of dc.tabs) {
    if (t.start !== cum + 1) { startOk = false; bad(`区段 ${t.tab} start=${t.start}，应为 ${cum + 1}`); break; }
    cum += t.total;
  }
  check(`序号承接连续（末区段止于 ${n(cum)}）`, startOk);
  const tabsBad = [];
  for (const t of dc.tabs) {
    const r = await get(`/api/diseases?tab=${t.tab}&page=1&pageSize=1`);
    if (r.total !== t.total) tabsBad.push(`${t.tab}: 列表 ${r.total} vs counts ${t.total}`);
  }
  check(`${dc.tabs.length} 个区段列表总数 = counts 总数`, tabsBad.length === 0, tabsBad);

  // ---------------------------------------------------------------- E
  console.log('\n=== E. 立项指南 / 解读辅导 ===');
  const lx = await get('/api/lixiang/sections');
  const lxTabs = Object.entries(lx.tabs || {});
  const lxBad = [];
  let lxSum = 0;
  for (const [tab, info] of lxTabs) {
    const r = await get(`/api/lixiang/items?tab=${tab}&page=1&pageSize=1`);
    if (r.total !== info.total) lxBad.push(`${tab}(${info.label}): 列表 ${r.total} vs sections ${info.total}`);
    lxSum += info.total;
  }
  check(`${lxTabs.length} 个区段列表总数 = sections 总数（${n(lxSum)} 条）`, lxBad.length === 0, lxBad);

  const lxr = await get('/api/lixiang/items?page=1&pageSize=200');
  check('立项指南行都带 section 字段（网页端「区段」列靠它）',
    (lxr.rows || []).every((r) => typeof r.section === 'string' && r.section.length > 0),
    '有行缺 section');
  const secFirst = {};
  for (const r of lxr.rows || []) secFirst[r.section] = (secFirst[r.section] || 0) + 1;

  const jd = await get('/api/lixiang/jiedu?page=1&pageSize=500');
  const jdItems = jd.items || jd.rows || [];
  check(`解读辅导 ${n(jd.total)} 条`, jd.total === 224, `实际 ${jd.total}`);
  const dangling = jdItems.filter((x) => /\n\s*\d{1,3}\s*[.、．]\s*$/.test(x.a || ''));
  check('答案末尾无游离题号（「多出一行 2.」那个 bug）', dangling.length === 0,
    dangling.slice(0, 3).map((x) => `#${x.issue}-${x.no} 末尾=${JSON.stringify((x.a || '').slice(-12))}`));
  const emptyA = jdItems.filter((x) => !(x.a || '').trim());
  check('答案不为空', emptyA.length === 0, emptyA.slice(0, 3).map((x) => `#${x.issue}-${x.no}`));

  // ---------------------------------------------------------------- F
  console.log('\n=== F. 列定义：网页端表头 == 桌面端 Cols ===');
  const html = readText('web/index.html');
  const dzCs = readText('src/Dip3Grouper.Desktop/Views/DezhouPriceView.xaml.cs');
  const gfCs = readText('src/Dip3Grouper.Desktop/Views/GuifanView.xaml.cs');

  function csHeaders(src, name) {
    const i = src.indexOf(`] ${name} =`);
    if (i < 0) throw new Error(`源码里找不到 ${name}`);
    const seg = src.slice(i, src.indexOf('};', i));
    const out = [];
    const re = /\("([^"]*)",\s*"([^"]*)",/g;
    let m;
    while ((m = re.exec(seg)) !== null) out.push(m[2]);
    return out;
  }
  // 只在 const DZ_COLS = { ... }; 这一段里找，避免撞上别处的 "live: ["
  const dzSegStart = html.indexOf('const DZ_COLS = {');
  const dzSeg = html.slice(dzSegStart, html.indexOf('};', dzSegStart));
  function webCols(view) {
    const start = dzSeg.indexOf(view + ': [');
    if (start < 0) throw new Error(`网页端找不到 DZ_COLS.${view}`);
    const seg = dzSeg.slice(start, dzSeg.indexOf('],', start));
    const out = [];
    const re = /\{\s*k:\s*'([^']*)',\s*t:\s*'([^']*)'/g;
    let m;
    while ((m = re.exec(seg)) !== null) out.push({ k: m[1], t: m[2] });
    return out;
  }

  const pairs = [
    ['live', 'Cols'],
    ['cancel', 'CancelCols'],
    ['legacy', 'LegacyCols'],
    ['cmp', 'CmpCols'],
  ];
  for (const [view, csName] of pairs) {
    const desk = csHeaders(dzCs, csName);
    const web = webCols(view).filter((c) => c.k !== '_fav').map((c) => c.t);
    const same = desk.length === web.length && desk.every((h, i) => h === web[i]);
    check(`物价 ${view}：${web.length} 列逐字一致`, same,
      `桌面端 [${desk.join(' | ')}]\n      网页端 [${web.join(' | ')}]`);
  }

  const gfDesk = csHeaders(gfCs, 'Cols');
  const gfStart = html.indexOf('const GF_COLS = [');
  const gfSeg = html.slice(gfStart, html.indexOf('];', gfStart));
  const gfWeb = [...gfSeg.matchAll(/\{\s*k:\s*'([^']*)',\s*t:\s*'([^']*)'/g)].map((m) => m[2]);
  check(`技术规范：${gfWeb.length} 列逐字一致`, gfDesk.length === gfWeb.length && gfDesk.every((h, i) => h === gfWeb[i]),
    `桌面端 [${gfDesk.join(' | ')}]\n      网页端 [${gfWeb.join(' | ')}]`);

  // 长文本列必须给足宽度（曾经 300px 被 table-layout:fixed + 100% 压成 85px）
  const liveSeg = dzSeg.slice(dzSeg.indexOf('live: ['), dzSeg.indexOf('cancel: ['));
  const wM = /k: 'name'[^}]*w: (\d+)/.exec(liveSeg);
  check('现行项目「项目名称」列宽 ≥ 280px', !!wM && Number(wM[1]) >= 280,
    `宽度 ${wM ? wM[1] : '未设置'}（0/未设 = 自适应，会被压扁）`);

  // 「扣减物耗」条件列（2026-11-01）：25 号文只有一般治疗类 11 条有值，两端都做成
  // 「筛到一般治疗或当前页带值行才插入列」，静态列数组保持一致，动态插入逻辑必须两端同在。
  check('桌面端有「扣减物耗」条件列（SyncDeductCol + Price1 后插入）',
    /SyncDeductCol/.test(dzCs) && /prop == "Price1"/.test(dzCs) && /"扣减物耗"/.test(dzCs));
  check('网页端有「扣减物耗」条件列（dzCols 动态插入 price1 之后）',
    /function dzCols\(/.test(html) && /\{ k: 'deduct', t: '扣减物耗', w: 100, num: 1 \}/.test(html));
  {
    const ded = await get('/api/dezhou?category=' + encodeURIComponent('一般治疗') + '&page=1&pageSize=200');
    const rows = ded.rows || [];
    const withDeduct = rows.filter((r) => r.deduct);
    // 一般治疗整类 133 条，其中 25 号文 11 条带扣减额（注射 / 清创缝合 / 换药 / 导尿 / 膀胱冲洗）
    check(`一般治疗 ${rows.length} 条里带扣减额的恰为 11 条`, withDeduct.length === 11 && rows.length >= 11,
      `rows=${rows.length} · 带扣减额 ${withDeduct.length}`);
    const sample = withDeduct.find((r) => r.name === '注射费（静脉输液）');
    check('注射费（静脉输液）扣减 1.5 元', !!sample && sample.deduct === '1.5',
      sample ? `实际 ${sample.deduct}` : '未找到该行');
  }

  // 每页条数上限：Core 内部夹 200，网页端给 500 会丢数据
  const psM = /const PAGE_SIZES = \[([^\]]*)\]/.exec(html);
  const ps = psM ? psM[1].split(',').map((x) => Number(x.trim())) : [];
  check(`每页条数 ${ps.join('/')} 均 ≤ 200`, ps.length > 0 && Math.max(...ps) <= 200,
    '超过 200 时 Core 只回 200 行，页码会跳条');

  // 公开接口不得再借管理接口（后台一开登录前台就 401）
  const adminCalls = [...html.matchAll(/fetch\('\/api\/admin\/[^']*'/g)].map((m) => m[0]);
  const frontAdmin = adminCalls.filter((c) => /diseases|lixiang|dezhou|guifan/.test(c));
  check('前台页面不再调用 /api/admin/* 查询接口', frontAdmin.length === 0, frontAdmin.slice(0, 4));

  // ---------------------------------------------------------------- G. 版式回归
  console.log('\n=== G. 版式：展开卡 / 表宽 / 启动器 ===');

  // 2026-10-30：物价页展开行曾把标签和值竖着堆成一列（.dgrid 只在 .lx-detail 下定义了 display:grid）
  check('详情卡 .dgrid 是通用 grid（不再挂在 .lx-detail 下）',
    /\.dgrid\s*\{[\s\S]{0,120}?display:\s*grid/.test(html),
    '物价页展开行会退化成「标签一行、值一行」的堆叠');
  // 2026-10-30 第二版：用户要求「网页版参照 .NET 版」，卡片从两列网格回到单列 132px 标签
  check('.dgrid 单列 132px 标签列（与桌面端 Block.Tpl 同宽）',
    /grid-template-columns:\s*132px minmax\(0,\s*1fr\)/.test(html));
  check('没有 .dz-detail .dgrid 覆盖规则', !/\.dz-detail\s+\.dgrid/.test(html));
  check('长值 .dv.wide 只占值列（不再跨两列）',
    /\.dv\.wide\s*\{\s*grid-column:\s*2\s*;?\s*\}/.test(html));
  check('详情卡外观同桌面端（浅灰底 + 10 圆角 + 12/14 内边距）',
    /\.dcard\s*\{[\s\S]{0,180}?background:\s*var\(--soft-bg\)[\s\S]{0,120}?border-radius:\s*10px/.test(html)
    && /\.dcard\s*\{[\s\S]{0,140}?padding:\s*12px 14px/.test(html));
  check('详情卡标题行带「收起」（同桌面端 Btn.Link）',
    /class="dclose"/.test(html));
  check('技术规范详情有量表（技术难度 / 风险程度 / 资源消耗）',
    /\.dmeters/.test(html) && /技术难度/.test(html) && /资源消耗/.test(html));
  check('表宽 100% + min-width 兜底（右侧不留白、列不被压扁）',
    /t\.style\.width = '100%'/.test(html) && /t\.style\.minWidth/.test(html));
  check('colgroup 挂在 <table> 下（不是 thead 里）',
    /t\.insertBefore\(cg,\s*t\.firstChild\)/.test(html));

  const shellSrc = fs.readFileSync(path.join(ROOT, 'src/Dip3Grouper.WebShell/Program.cs'), 'utf8');
  const formSrc = fs.readFileSync(path.join(ROOT, 'src/Dip3Grouper.WebShell/ServerForm.cs'), 'utf8');
  check('启动器局域网地址排除虚拟网卡',
    /VirtualHints/.test(shellSrc) && /vmware/i.test(shellSrc) && /hyper-v/i.test(shellSrc));
  check('启动器按默认网关优先打分', /hasGateway\)\s*score\s*\+=\s*60/.test(shellSrc));
  check('启动器面板列出后台地址与数据目录',
    /后台管理/.test(formSrc) && /数据目录/.test(formSrc));
  check('启动器可重新探测地址（刷新按钮）', /刷新地址/.test(formSrc));

  // ---------------------------------------------------------------- H. 详情卡字段口径
  // 2026-10-30 晚：用户拿桌面端 / 网页端同一版本的截图对比，网页版把表格里已有的列
  // （编码 / 类别 / 性质 / 单位）和 8 个空字段全平铺出来，卡片比桌面端高两倍。
  console.log('\n=== H. 详情卡字段口径（对齐桌面端 BuildBlocks） ===');
  check('现行项目字段收敛为 dzLiveFields（不再平铺表格列）',
    /function dzLiveFields\(row\)/.test(html) && !/\['项目性质', row\.kind\]/.test(html));
  check('落地文号与执行日期合并为「落地文件」一行',
    /add\('落地文件'/.test(html) && /执行日期 \$\{row\.exec/.test(html));
  check('医保支付与自付比例合并为一行',
    /自付比例 \$\{dash\(row\.selfPay\)\}/.test(html));
  check('空值字段不渲染（不再打一排 —）',
    /\.filter\(\(\[, v\]\) => v && v !== '—'\)/.test(html));
  // 格子改用 class 定位：同一页可以同时展开多张卡，固定 id 会互相串
  check('详情卡带「类别 · 使用说明」（前 2 条 + 共 N 条）',
    /· 使用说明<\/div><div class="dv dzuse">/.test(html) && /本类使用说明共 \$\{lines\.length\} 条/.test(html));

  const routerSrc = fs.readFileSync(path.join(ROOT, 'src/Dip3Grouper.Web/WebRouter.cs'), 'utf8');
  check('后端提供 /api/dezhou-usage 且走前台只读白名单',
    /url == "\/api\/dezhou-usage"/.test(routerSrc)
    && /IsPublicDataPath[\s\S]{0,220}?\/api\/dezhou-usage/.test(routerSrc));
  check('/api/dezhou-usage 按 (文号, 类别) 取说明（与桌面端 UsageOf 同源）',
    /S\.Dezhou\.UsageOf\(Q\(req, "doc"\), Q\(req, "category"\)\)/.test(routerSrc));

  // ---------------------------------------------------------------- I. 互相关联（三库）
  // 2026-10-30 晚：详情卡字段对齐后，用户指出「网页版项目缺互相关联」——
  // 桌面端每张详情卡末尾都有 CrossMap 生成的「关联·立项指南 / 关联·技术规范」，
  // 网页版一条都没有。算法已下沉到 Core 的 CrossLinker，两端共用。
  console.log('\n=== I. 互相关联（立项指南 / 德州物价 / 技术规范） ===');
  const coreCross = fs.readFileSync(path.join(ROOT, 'src/Dip3Grouper.Core/CrossLinker.cs'), 'utf8');
  const deskCross = fs.readFileSync(path.join(ROOT, 'src/Dip3Grouper.Desktop/CrossMap.cs'), 'utf8');
  check('Core 有 CrossLinker（三个方向 + For 分派）',
    /class CrossLinker/.test(coreCross)
    && /ForLixiang\(AppState state/.test(coreCross)
    && /ForDezhou\(AppState state/.test(coreCross)
    && /ForGuifan\(AppState state/.test(coreCross));
  check('相似度阈值与 top3 只有一份（Dice 0.62 / Take(3)）',
    /s >= 0\.62/.test(coreCross) && /Take\(3\)/.test(coreCross));
  check('立项指南用 Gid 当跳转键（区段内 Id 会串号）', /Add\(lx, disp, info, r\.Gid\)/.test(coreCross));
  check('桌面端 CrossMap 改为委托 Core（不再自己建索引）',
    /CrossLinker\.ForDezhou\(App\.State, name\)/.test(deskCross)
    && !/new Index\s*\{/.test(deskCross));

  const dzCross = await get('/api/cross?lib=dezhou&name=' + encodeURIComponent('心脏移植术'));
  check('物价 → 立项指南：同名直连命中', (dzCross.groups || []).some(
    (g) => g.label === '关联·立项指南' && g.hits.some((h) => h.chip === '同名')));
  check('物价 → 技术规范：相似候选命中', (dzCross.groups || []).some(
    (g) => g.label === '关联·技术规范' && g.hits.some((h) => h.chip === '相似')));
  const lxCross = await get('/api/cross?lib=lixiang&name=' + encodeURIComponent('心脏移植术'));
  check('立项指南 → 物价 / 技术规范', (lxCross.groups || []).length >= 2);
  const gfCross = await get('/api/cross?lib=guifan&name=' + encodeURIComponent('心脏移植术'));
  check('技术规范 → 立项指南 / 物价', (gfCross.groups || []).length >= 2);
  check('关联条目都带 lib + id（可跳转）', (dzCross.groups || []).every(
    (g) => g.hits.every((h) => ['lixiang', 'dezhou', 'guifan'].includes(h.lib) && Number.isInteger(h.id))));

  check('网页端三张详情卡都挂关联位（物价 / 规范 / 立项）',
    (/dxFill\(card\.querySelector\('\.dxref'\), 'dezhou'/.test(html))
    && (/dxFill\(card\.querySelector\('\.dxref'\), 'guifan'/.test(html))
    && (/dxFill\(card\.querySelector\('\.dxref'\), 'lixiang'/.test(html)));
  check('关联条目可点击跳转到目标库检索', /function crossJump\(el\)/.test(html)
    && /\['lx', 'lxQ'/.test(html) && /\['gf', 'gfQ'/.test(html) && /\['dz', 'dzQ'/.test(html));
  check('/api/cross 走前台只读白名单', /url == "\/api\/cross"/.test(routerSrc)
    && /IsPublicDataPath[\s\S]{0,260}?\/api\/cross/.test(routerSrc));

  // ---------------------------------------------------------------- J 病理开具指引
  console.log('\nJ. 病理开具指引（档位 / 蜡块速算 / 二级三级收费）');

  const coreZj = fs.readFileSync(path.join(ROOT, 'src/Dip3Grouper.Core/Zhijian.cs'), 'utf8');
  const zjFile = JSON.parse(readText('data/zhijian.json').replace(/^\uFEFF/, ''));
  const zjFlat = (zjFile.systems || []).flatMap((x) => x.items || []);

  const zj4 = await get('/api/zhijian?wax=4&q=' + encodeURIComponent('宫颈'));
  check('接口条数与 data/zhijian.json 一致', zj4.total === zjFlat.length && zj4.meta.count === zjFlat.length,
    `${zj4.total} / ${zjFlat.length}`);
  check('15 个疾病系统全部下发', (zj4.systems || []).length === (zjFile.systems || []).length
    && (zj4.systems || []).length >= 15);
  check('关键词命中宫颈相关条目', (zj4.rows || []).some((r) => /宫颈CIN锥切/.test(r.sample)),
    (zj4.rows || []).length + ' 条');

  // 报价逐行对《德州市病理一二三级物价》：常规 1/2/4/12 蜡块、复杂 15 蜡块、细胞 1 张、TCT
  const zjCases = [
    [1, '常规', '2', 168], [2, '常规', '2', 237], [4, '常规', '2', 380], [12, '常规', '2', 982.4],
    [1, '常规', '3', 198], [4, '常规', '3', 446], [12, '常规', '3', 1146.2],
    [15, '复杂', '2', 1445], [15, '复杂', '3', 1692.5],
    [1, '细胞', '2', 235], [1, 'TCT', '2', 201],
  ];
  const zjBad = [];
  for (const [w, mode, lv, want] of zjCases) {
    const got = (await get(`/api/zhijian?wax=${w}&mode=${encodeURIComponent(mode)}&level=${lv}`)).quote.total;
    if (got !== want) zjBad.push(`${mode} ${w} 张 ${lv} 级应 ${want} 实 ${got}`);
  }
  check('二级/三级 × 常规/复杂/细胞/TCT 逐档对得上物价表', zjBad.length === 0,
    zjBad.length ? zjBad[0] : `${zjCases.length} 档全对`);

  check('11 蜡块含超 10 张加收（二级 5.2/块）', (await get('/api/zhijian?wax=11&level=2')).quote.total === 903.2);
  check('诊断费第 31 块起不再加收',
    (await get('/api/zhijian?wax=31&level=2')).quote.diag === (await get('/api/zhijian?wax=30&level=2')).quote.diag);

  const zjCx = await get('/api/zhijian?wax=8&complex=1&level=2');
  check('复杂病理走独立处理费档', zjCx.quote.complex === true
    && zjCx.quote.lines.some((l) => /复杂/.test(l.name)));

  check('蜡块速算：1 袋 5 块 → 2 个蜡块', (await get('/api/zhijian?bags=1&pieces=5&size=2')).quick.wax === 2);
  check('蜡块速算：9cm 肿物 → 8 个蜡块', (await get('/api/zhijian?bags=1&pieces=1&size=9')).quick.wax === 8);

  check('专项价目按价格档取（二级/三级各一套）',
    JSON.stringify((await get('/api/zhijian?level=2')).special) !== JSON.stringify((await get('/api/zhijian?level=3')).special)
    && (await get('/api/zhijian?level=2')).special.every((x) => Array.isArray(x.lines) && x.lines.length));
  check('价格档两档全部下发', ((await get('/api/zhijian')).levels || []).length === 2);

  check('网页端抽屉结构齐全（竖排标签 / 搜索 / 报价 / 速算 / 价格档）',
    /class="zg-tab"/.test(html) && /id="zgBox"/.test(html) && /function zgOpen\(/.test(html)
    && /\/api\/zhijian/.test(html) && /id="zgQuickTxt"/.test(html) && /function zgCopy\(/.test(html)
    && /function zgLevel\(/.test(html) && /id="zgMode"/.test(html));
  check('导航里不再有「送检指引」入口，改成右侧竖排标签',
    !/id="zgBtn"/.test(html) && !/送检指引/.test(html) && /病理开具指引/.test(html));
  check('两端共用 Core 的档位与报价（不各写一套）',
    /class ZhijianStore/.test(coreZj) && /public static ZhijianQuote Quote\(ZhijianBase/.test(coreZj)
    && /ZhijianStore.WaxCount\(bags/.test(routerSrc));
  const guideCs = fs.readFileSync(path.join(ROOT, 'src/Dip3Grouper.Desktop/GuidePanel.xaml.cs'), 'utf8');
  check('桌面端抽屉走同一份数据',
    /class GuidePanel/.test(guideCs)
    && /_st\.Zhijian\.Search/.test(guideCs) && /_st\.Zhijian\.Quote/.test(guideCs)
    && /ToolHost/.test(fs.readFileSync(path.join(ROOT, 'src/Dip3Grouper.Desktop/MainWindow.xaml.cs'), 'utf8')));

  // 工具栏：竖排标签改名 + 抽屉两层（工具列表 / 病理）+ 预留位 + 返回
  check('竖排标签改叫「工具栏」', /id="zgTab"[^>]*>工具栏</.test(html));
  check('抽屉分两层：工具列表 + 病理，可互相切换',
    /class="zg-view" id="zgHome"/.test(html) && /class="zg-view hidden" id="zgGuide"/.test(html)
    && /function zgShowTool\(/.test(html) && /onclick="zgShowTool\('pathology'\)"/.test(html)
    && /onclick="zgShowTool\(''\)"/.test(html));
  check('工具列表：病理 + 基本物耗 + 2 个预留位（以后加工具不改导航）',
    (html.match(/class="zg-slot"/g) || []).length === 2 && /预留工具位/.test(html)
    && /onclick="zgShowTool\('wuhao'\)"/.test(html) && /基本物耗指引/.test(html));
  check('Ctrl+G 仍直达病理，点标签才停在工具列表',
    /else zgOpen\('pathology'\)/.test(html) && /id="zgTab" onclick="zgOpen\(\)"/.test(html));

  const toolXaml = fs.readFileSync(path.join(ROOT, 'src/Dip3Grouper.Desktop/ToolPanel.xaml'), 'utf8');
  const toolCs = fs.readFileSync(path.join(ROOT, 'src/Dip3Grouper.Desktop/ToolPanel.xaml.cs'), 'utf8');
  check('桌面端同为「工具栏」壳：ToolPanel 包 GuidePanel + 预留位',
    /x:Name="HomeView"/.test(toolXaml) && /x:Name="GuideView"/.test(toolXaml)
    && /local:GuidePanel x:Name="Guide"/.test(toolXaml)
    && (toolXaml.match(/StrokeDashArray="3 3"/g) || []).length === 2
    && /local:WuhaoPanel x:Name="Wuhao"/.test(toolXaml)
    && /public void OpenTool\(string tool\)/.test(toolCs)
    && /ToolPathology = "pathology"/.test(toolCs));
  check('主窗口挂 ToolPanel，竖排标签文案与网页端一致',
    /<local:ToolPanel x:Name="ToolHost" \/>/.test(
      fs.readFileSync(path.join(ROOT, 'src/Dip3Grouper.Desktop/MainWindow.xaml'), 'utf8'))
    && /Text="工具栏"/.test(fs.readFileSync(path.join(ROOT, 'src/Dip3Grouper.Desktop/MainWindow.xaml'), 'utf8')));
  check('/api/zhijian 走前台只读白名单', /url == "\/api\/zhijian"/.test(routerSrc)
    && /IsPublicDataPath[\s\S]{0,320}?\/api\/zhijian/.test(routerSrc));


  // ---------------------------------------------------------------- K -------------------------------------------------
  console.log('\n=== K. 基本物耗（清单 / 边界 / 判定） ===');
  const whFile = JSON.parse(fs.readFileSync(path.join(ROOT, 'data/wuhao.json'), 'utf8').replace(/^\uFEFF/, ''));
  const wh = await get('/api/wuhao');
  const coreWh = fs.readFileSync(path.join(ROOT, 'src/Dip3Grouper.Core/Wuhao.cs'), 'utf8');

  check('接口条数 = data/wuhao.json（不是拿接口自己比自己）',
    wh.total === (whFile.items || []).length && wh.meta.count === whFile.items.length,
    `接口 ${wh.total} / 文件 ${(whFile.items || []).length}`);
  check(`36 类条款全部下发（${(wh.sources || []).length} 类）`,
    (wh.sources || []).length === (whFile.sources || []).length && wh.sources.length >= 30);
  check('病理条款含玻片 / 包埋介质 / 试剂（病理最易漏收的三样）',
    ['玻片', '包埋介质', '试剂', '活检器具'].every((k) =>
      ((wh.sources || []).find((x) => x.cat === '病理') || { clause: '' }).clause.includes(k)));
  check('每条物耗都挂到了指南类别（无孤儿条目）',
    (wh.items || []).every((i) => Array.isArray(i.cats) && i.cats.length > 0 && i.note));
  check('分组计数 = 条目数合计',
    (wh.groups || []).reduce((a, g) => a + g.count, 0) === wh.total);

  const tone = async (q) => (await get('/api/wuhao?q=' + encodeURIComponent(q))).verdict.tone;
  const noCases = ['纱布', '一次性输液器', '耦合剂', '玻片', '免疫组化抗体', '棉球'];
  const badNo = [];
  for (const q of noCases) if ((await tone(q)) !== 'no') badNo.push(q);
  check('判定「不可另收」：纱布 / 输液器 / 耦合剂 / 玻片 / 免疫组化抗体 / 棉球', badNo.length === 0, badNo);
  check('判定「可另收」：钢板（内固定器械）', (await tone('钢板')) === 'yes');
  check('判定「需核对」：留置针（看可另收费材料目录）', (await tone('留置针')) === 'check');
  check('判定「不可另收」：导尿包（整套组件都在清单里）', (await tone('导尿包')) === 'no');
  check('判定「可另收（减收）」：三腔导尿管（特殊功能导尿管）', (await tone('三腔导尿管')) === 'deduct');
  check('判定「不可另收」：双腔 / 普通导尿管（常规型就是清单里的「导尿管」）',
    (await tone('双腔导尿管')) === 'no' && (await tone('导尿管')) === 'no');
  check('判定「需核对」：胃管 / 吸痰管（清单一律没列，得先过三关）',
    (await tone('胃管')) === 'check' && (await tone('吸痰管')) === 'check');
  check('判定「不可另收」：固定带 / 换药包 / 雾化面罩（组件都在清单里）',
    (await tone('固定带')) === 'no' && (await tone('换药包')) === 'no' && (await tone('雾化面罩')) === 'no');
  check('判定「需核对」：三通 / 真空采血管（管路附件与采血耗材看目录）',
    (await tone('三通')) === 'check' && (await tone('真空采血管')) === 'check');
  const wuhaoXaml = fs.readFileSync(path.join(ROOT, 'src/Dip3Grouper.Desktop/WuhaoPanel.xaml'), 'utf8');
  const wuhaoCs = fs.readFileSync(path.join(ROOT, 'src/Dip3Grouper.Desktop/WuhaoPanel.xaml.cs'), 'utf8');
  const webHtml = fs.readFileSync(path.join(ROOT, 'web/index.html'), 'utf8');
  check('清单条目双击能跳到判定页（两个端都要真的接事件，不能只写「双击判定」文案）',
    /MouseDoubleClick="OnItemDoubleClick"/.test(wuhaoXaml) && /private void OnItemDoubleClick/.test(wuhaoCs)
    && /private void GotoJudge/.test(wuhaoCs)
    && /ondblclick="zwJudgeItem\(this\)"/.test(webHtml) && /function zwJudgeItem/.test(webHtml));
  check('清单详情区直接带出条款原文（不用再去「口径依据」翻）',
    /【\{s\.Cat\}】/.test(wuhaoCs) && /【\$\{zwEsc\(x\.cat\)\}】/.test(webHtml));
  check('分组条数与 items 实算一致（界面左侧数字不能空）',
    (wh.groups || []).every((g) => g.count === wh.items.filter((i) => i.group === g.id).length));
  check('桌面端分组列表存 id 而不是显示名（早先拿 Name 比 id，点开全是空白）',
    /new GroupRow\(g\.Id, g\.Name,/.test(fs.readFileSync(path.join(ROOT, 'src/Dip3Grouper.Desktop/WuhaoPanel.xaml.cs'), 'utf8'))
    && /data-g="\$\{zwEsc\(g\.id\)\}"/.test(fs.readFileSync(path.join(ROOT, 'web/index.html'), 'utf8')));
  const badDed = [];
  for (const q of ['避光输液器', '精密输液器', '精密过滤输液器'])
    if ((await tone(q)) !== 'deduct') badDed.push(`${q}=${await tone(q)}`);
  check('判定「可另收（减收）」：避光 / 精密 / 精密过滤输液器（特殊功能耗材）', badDed.length === 0, badDed);
  check('减收口径写到依据里（不只说「能收」）',
    /减收/.test((await get('/api/wuhao?q=' + encodeURIComponent('精密输液器'))).verdict.basis)
    || /减收/.test((await get('/api/wuhao?q=' + encodeURIComponent('精密输液器'))).verdict.note));
  check('判断口径含「特殊功能耗材同步减收」（用户贴的原文最后一句）',
    (wh.rules || []).length >= 7 && wh.rules.some((r) => /减收政策/.test(r.text)));
  check('半截名称不落到「可收」：精密 / 避光 / 引流 都不判 yes',
    (await tone('精密')) !== 'yes' && (await tone('避光')) !== 'yes' && (await tone('引流')) !== 'yes');
  check('半截名称只提示「名称不全」，写全称后给「可另收（减收）」',
    /名称不全/.test((await get('/api/wuhao?q=' + encodeURIComponent('精密'))).verdict.head)
    && (await tone('精密输液器')) === 'deduct');
  // 「某某专用导丝」现在能被边界清单的「导丝」捞住（收录更全），清单外的用例换一个真没收录的
  check('专用导丝落到边界清单判「可另收」（不再是清单外）', (await tone('某某专用导丝')) === 'yes');
  check('清单外提示改为「先过三关」，不再写「原则上可另收」',
    /三关/.test((await get('/api/wuhao?q=' + encodeURIComponent('一次性使用黏膜切开刀'))).verdict.head));
  check('清单外默认「可零差率另收」并提示过三关',
    (await tone('一次性使用黏膜切开刀')) === 'miss'
    && /三关/.test((await get('/api/wuhao?q=' + encodeURIComponent('一次性使用黏膜切开刀'))).verdict.note));
  check('边界清单三种结论都有（可另收 / 不可另收 / 需核对）',
    ['可另收', '不可另收', '需核对'].every((v) => (wh.edges || []).filter((e) => e.verdict === v).length >= 2));
  check('第四种结论「可另收（减收）」在清单与页面上都有位置',
    (wh.edges || []).filter((e) => e.verdict === '可另收（减收）').length >= 2
    && /\.zgw-verdict\.ded/.test(html) && /tone === 'deduct'/.test(html));
  check('判断口径 6 条齐全（25 号文落点 / 定义 / 零差率 / 三步 / 坑 / 外购）',
    (wh.rules || []).length >= 6 && /25/.test(wh.rules[0].text));

  check('网页端四页签 + 判定 / 清单 / 边界 三块都在',
    /id="zgWuhao"/.test(html) && /function zwTab\(/.test(html) && /id="zwJudge"/.test(html)
    && /id="zwBasic"/.test(html) && /id="zwCharge"/.test(html) && /id="zwRule"/.test(html)
    && /function zwAsk\(/.test(html) && /id="zwGroups"/.test(html) && /id="zwEdges"/.test(html));
  check('网页端判定走后端 WuhaoStore.Judge（不各写一套规则）',
    /fetch\('\/api\/wuhao\?q='/.test(html) && /class WuhaoStore/.test(coreWh)
    && /public WuhaoVerdict Judge\(/.test(coreWh) && /w\.Judge\(q\)/.test(routerSrc));
  check('桌面端 WuhaoPanel 挂进工具栏第三层',
    /local:WuhaoPanel/.test(toolXaml) && /ToolWuhao = "wuhao"/.test(toolCs)
    && /x:Name="WuhaoView"/.test(toolXaml));
  check('/api/wuhao 与 /api/zhijian 同走前台只读白名单',
    /url == "\/api\/wuhao"/.test(routerSrc) && /IsPublicDataPath[\s\S]{0,400}?\/api\/wuhao/.test(routerSrc));

  // ---------------------------------------------------------------- 汇总
  console.log('\n=== 汇总 ===');
  console.log(`通过 ${pass} / 失败 ${failures.length}`);
  if (failures.length) {
    console.log('\n失败清单:');
    failures.forEach((f) => console.log(`  - ${f.label}`));
    process.exitCode = 1;
  } else {
    console.log('全部一致 ✔');
  }
})().catch((e) => {
  console.error('\n校验中断：', e.message);
  process.exitCode = 1;
});
