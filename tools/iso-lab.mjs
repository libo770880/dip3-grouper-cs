#!/usr/bin/env node
/**
 * 对拍隔离实验台 —— 安全地跑写接口对拍，不碰正式数据。
 *
 *   node tools/iso-lab.mjs up      创建/重置隔离数据目录并拉起两个隔离实例
 *   node tools/iso-lab.mjs down    停止隔离实例（按端口精确 kill）
 *   node tools/iso-lab.mjs status  查看端口与隔离目录状态
 *   node tools/iso-lab.mjs run     一键: up → compare-write-api → down
 *
 * 隔离实例:
 *   Node 版  http://localhost:3211   (工程副本 .tmp/iso-node)
 *   C#   版  http://localhost:3231   (同 exe + DIP3_HOME=.tmp/iso-cs)
 *
 * 为什么需要它:
 *   写接口对拍会真实改动 data/ 与 config/。直接对着 3210/3220 跑，一旦服务仍在运行，
 *   其内存态会在还原后再次落盘，导致数据错乱（本项目已踩过一次坑）。
 *   隔离实例用独立端口 + 独立数据目录，跑完即弃，正式数据零风险。
 *
 * 环境变量:
 *   DIP3_LAB_NODE_PORT  默认 3211
 *   DIP3_LAB_CS_PORT    默认 3231
 */

import fs from 'node:fs';
import path from 'node:path';
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';

const execFileAsync = promisify(execFile);

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const TMP = path.join(ROOT, '.tmp');
const ISO_NODE = path.join(TMP, 'iso-node');
const ISO_CS = path.join(TMP, 'iso-cs');
const LOG = path.join(TMP, 'lab-logs');

const NODE_ROOT = path.resolve(ROOT, '..', 'dip3-grouper');
const NODE_PORT = Number(process.env.DIP3_LAB_NODE_PORT) || 3211;
const CS_PORT = Number(process.env.DIP3_LAB_CS_PORT) || 3231;

// Node 可执行文件：优先环境变量；否则用当前进程的 node（随版本目录升级自动跟随，
// 不再硬编码 22.22.2-3 这类会随二进制升级而失效的路径）
const NODE_EXE = process.env.DIP3_LAB_NODE_EXE || process.execPath;
const CS_EXE = path.join(ROOT, 'src', 'Dip3Grouper.Web', 'bin', 'Debug', 'net8.0', 'Dip3Grouper.Web.exe');

// ---------------------------------------------------------------------
// 工具
// ---------------------------------------------------------------------
/**
 * 注意: 全部使用**异步**子进程调用。
 * 某些受限沙箱会拦截同步派生进程（spawnSync 报 EBUSY），
 * 一旦用 spawnSync 取 netstat/taskkill 输出就会静默拿到空结果，
 * 导致"停止实例"实际上什么也没做。
 */
async function run(cmd, args) {
  try {
    const { stdout } = await execFileAsync(cmd, args, { encoding: 'utf8', windowsHide: true, maxBuffer: 16 * 1024 * 1024 });
    return { ok: true, stdout: stdout || '' };
  } catch (e) {
    return { ok: false, stdout: e.stdout || '', error: e.message };
  }
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/** 按端口找到监听进程 PID（Windows；IPv4/IPv6 行去重） */
async function pidOnPort(port) {
  const r = await run('netstat', ['-ano']);
  if (!r.stdout) return [];
  const re = new RegExp(`[:.]${port}\\s+\\S+\\s+LISTENING\\s+(\\d+)`, 'i');
  const pids = new Set();
  for (const line of r.stdout.split(/\r?\n/)) {
    if (!/LISTENING/i.test(line)) continue;
    const m = line.match(re);
    if (m) pids.add(m[1]);
  }
  return [...pids];
}

async function killPort(port) {
  let killed = 0;
  for (let round = 0; round < 10; round++) {
    const pids = await pidOnPort(port);
    if (!pids.length) break;
    for (const pid of pids) {
      await run('taskkill', ['/PID', pid, '/F']);
      killed++;
    }
    await sleep(300);
  }
  return killed;
}

/**
 * 递归复制（**逐个文件覆盖，不做任何删除**）。
 *
 * 为什么不用 fs.cpSync / fs.rmSync:
 *   受限环境下 WorkBuddy CLI 注入了 node-safe-delete-shim，对**脚本内的删除操作**
 *   按"轮次"累计限流（阈值 50，scope:"turn"）。一次 syncCode 的 rmSync 就可能
 *   报 [safe-delete][SAFE_DELETE_BULK_CONFIRM_REQUIRED] 并直接抛错，
 *   更糟的是同一轮内跑第二次必然失败（配额已被上一轮耗尽）。
 *   覆盖式复制不产生任何删除操作，因此不受限流影响。
 *   残留的旧文件对隔离实例无害（会被同名新文件覆盖，多余的不会被引用）。
 */
function copyTree(src, dst) {
  if (!fs.existsSync(src)) return 0;
  fs.mkdirSync(dst, { recursive: true });
  let n = 0;
  for (const e of fs.readdirSync(src, { withFileTypes: true })) {
    const s = path.join(src, e.name);
    const d = path.join(dst, e.name);
    if (e.isDirectory()) n += copyTree(s, d);
    else { fs.copyFileSync(s, d); n++; }
  }
  return n;
}

async function waitReady(port, timeoutMs = 60000) {
  const t0 = Date.now();
  while (Date.now() - t0 < timeoutMs) {
    try {
      const r = await fetch(`http://localhost:${port}/api/admin/status`);
      if (r.ok) return true;
    } catch { /* 未就绪 */ }
    await new Promise((r) => setTimeout(r, 500));
  }
  return false;
}

function spawnDetached(cmd, args, cwd, env, logName) {
  fs.mkdirSync(LOG, { recursive: true });
  const out = fs.openSync(path.join(LOG, logName + '.log'), 'a');
  const child = spawn(cmd, args, {
    cwd,
    env: { ...process.env, ...env },
    detached: true,
    stdio: ['ignore', out, out],
    windowsHide: true,
  });
  child.unref();
  return child.pid;
}

// ---------------------------------------------------------------------
// 子命令
// ---------------------------------------------------------------------
async function down() {
  let n = 0;
  for (const p of [NODE_PORT, CS_PORT]) {
    const k = await killPort(p);
    console.log(`  ${p}: 已停止 ${k} 个进程`);
    n += k;
  }
  return n;
}

function resetData() {
  // 只重建数据/配置，保留 bin/src/node_modules 等可复用部分。
  // 注意: 用「覆盖式复制」而不是「先删目录再复制」—— 见 copyTree 的说明，
  // 脚本内的删除在受限环境会被按轮次限流并抛错。
  for (const dir of [ISO_CS, ISO_NODE]) {
    fs.mkdirSync(path.join(dir, 'data'), { recursive: true });
    fs.mkdirSync(path.join(dir, 'config'), { recursive: true });
  }

  fs.copyFileSync(path.join(ROOT, 'config', 'dip3-config.json'), path.join(ISO_CS, 'config', 'dip3-config.json'));
  for (const f of fs.readdirSync(path.join(ROOT, 'data'))) {
    const src = path.join(ROOT, 'data', f);
    if (fs.statSync(src).isFile()) fs.copyFileSync(src, path.join(ISO_CS, 'data', f));
  }
  fs.copyFileSync(path.join(NODE_ROOT, 'config', 'dip3-config.json'), path.join(ISO_NODE, 'config', 'dip3-config.json'));
  for (const f of fs.readdirSync(path.join(NODE_ROOT, 'data'))) {
    const src = path.join(NODE_ROOT, 'data', f);
    if (fs.statSync(src).isFile()) fs.copyFileSync(src, path.join(ISO_NODE, 'data', f));
  }

  // 对拍台不应继承正式实例的登录口令：写接口对拍不带认证，而 settings.json 会随
  // data/ 一起被复制进来（loginEnabled=true → C# 隔离实例所有写接口回 401）。
  // 这里把隔离副本的登录校验关掉 —— 只影响隔离目录，正式数据零改动。
  const csSettings = path.join(ISO_CS, 'data', 'settings.json');
  if (fs.existsSync(csSettings)) {
    const s = JSON.parse(fs.readFileSync(csSettings, 'utf8'));
    s.loginEnabled = false;
    fs.writeFileSync(csSettings, JSON.stringify(s, null, 2));
  }

  console.log('  隔离数据已重置（自正式实例覆盖副本）');
}

function syncCode() {
  // Node 隔离副本的代码每次对齐正式工程（避免改完代码忘了同步）
  // 同样只覆盖、不删除。
  for (const d of ['bin', 'src', 'web', 'tools']) {
    copyTree(path.join(NODE_ROOT, d), path.join(ISO_NODE, d));
  }
  fs.copyFileSync(path.join(NODE_ROOT, 'package.json'), path.join(ISO_NODE, 'package.json'));
  if (!fs.existsSync(path.join(ISO_NODE, 'node_modules'))) {
    copyTree(path.join(NODE_ROOT, 'node_modules'), path.join(ISO_NODE, 'node_modules'));
  }
  for (const f of ['index.html', 'admin.html']) {
    if (fs.existsSync(path.join(ROOT, 'web', f))) fs.copyFileSync(path.join(ROOT, 'web', f), path.join(ISO_NODE, 'web', f));
  }
  fs.mkdirSync(path.join(ISO_CS, 'web'), { recursive: true });
  for (const f of ['index.html', 'admin.html']) {
    if (fs.existsSync(path.join(ROOT, 'web', f))) fs.copyFileSync(path.join(ROOT, 'web', f), path.join(ISO_CS, 'web', f));
  }
  console.log('  Node 隔离副本代码已同步');
}

async function up({ keepData = false } = {}) {
  await down();
  fs.mkdirSync(TMP, { recursive: true });
  if (!fs.existsSync(CS_EXE)) {
    console.error(`[错误] 未找到 C# 可执行文件: ${CS_EXE}`);
    console.error('       请先执行: dotnet build Dip3Grouper.sln');
    process.exit(1);
  }
  syncCode();
  if (!keepData) resetData();

  const nodePid = spawnDetached(NODE_EXE, ['bin/web.js', '--port', String(NODE_PORT), '--jiceng'],
    ISO_NODE, {}, `iso-node-${NODE_PORT}`);
  console.log(`  Node 隔离实例已启动 pid=${nodePid} port=${NODE_PORT}`);

  const csPid = spawnDetached(CS_EXE, ['--port', String(CS_PORT), '--jiceng'],
    ROOT, { DIP3_HOME: ISO_CS }, `iso-cs-${CS_PORT}`);
  console.log(`  C#   隔离实例已启动 pid=${csPid} port=${CS_PORT}`);

  const [a, b] = await Promise.all([waitReady(NODE_PORT), waitReady(CS_PORT)]);
  if (!a || !b) {
    console.error(`[错误] 实例未就绪 (node=${a} cs=${b})，请查看 .tmp/lab-logs/`);
    process.exit(1);
  }
  console.log(`  两个隔离实例均已就绪`);
}

async function status() {
  for (const [tag, port, dir] of [
    ['Node', NODE_PORT, ISO_NODE],
    ['C#  ', CS_PORT, ISO_CS],
    ['Node(正式)', 3210, NODE_ROOT],
    ['C#  (正式)', 3220, ROOT],
  ]) {
    const pids = await pidOnPort(port);
    console.log(`  ${tag} port=${port} pid=${pids.join(',') || '-'} dir=${dir}`);
  }
  for (const [tag, dir] of [['iso-node', ISO_NODE], ['iso-cs', ISO_CS]]) {
    if (fs.existsSync(path.join(dir, 'data'))) console.log(`  ${tag}/data: ${fs.readdirSync(path.join(dir, 'data')).join(', ')}`);
  }
}

/** 前台运行子命令并透传退出码（异步 spawn: 受限沙箱下 spawnSync 会 EBUSY） */
function runForeground(cmd, args, cwd, env) {
  return new Promise((resolve) => {
    const child = spawn(cmd, args, { cwd, env: { ...process.env, ...env }, stdio: 'inherit', windowsHide: true });
    child.on('close', (code) => resolve(code ?? 1));
    child.on('error', (e) => { console.error('[错误] 无法启动子进程:', e.message); resolve(1); });
  });
}

// ---------------------------------------------------------------------
(async () => {
  const cmd = process.argv[2] || 'help';
  switch (cmd) {
    case 'up':
      await up();
      break;
    case 'down':
      console.log('停止隔离实例…');
      down();
      break;
    case 'status':
      status();
      break;
    case 'run': {
      console.log('=== 拉起隔离实验台 ===');
      await up();
      const extra = process.argv.slice(3);
      console.log('\n=== 执行写接口对拍 ===');
      const code = await runForeground(NODE_EXE, [
        path.join(ROOT, 'tools', 'compare-write-api.mjs'),
        `http://localhost:${NODE_PORT}`, `http://localhost:${CS_PORT}`, ...extra,
      ], ROOT, {
        DIP3_NODE_ROOT: ISO_NODE,
        DIP3_CS_ROOT: ISO_CS,
      });
      console.log('\n=== 收尾: 停止隔离实例 ===');
      await down();
      process.exit(code);
      break;
    }
    default:
      console.log(fs.readFileSync(fileURLToPath(import.meta.url), 'utf8').split('\n').slice(1, 26).map((l) => l.replace(/^ \* ?/, '')).join('\n'));
  }
})();
