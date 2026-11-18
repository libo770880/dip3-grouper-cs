// 用 CDP 驱动 Edge 无头浏览器：导航 → 跑一段 JS（展开行 / 切视图）→ 截图
// 用法：node tools/shot-cdp.mjs <url> <out.png> [evalJs] [waitMs]
import { spawn } from 'node:child_process';
import { setTimeout as sleep } from 'node:timers/promises';
import fs from 'node:fs';

const EDGE = [
  'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
].find((p) => fs.existsSync(p));
if (!EDGE) throw new Error('找不到 msedge.exe');

const url = process.argv[2];
const out = process.argv[3];
const js = process.argv[4] || '';
const waitMs = Number(process.argv[5] || 1500);
const PORT = 9500 + Math.floor(Math.random() * 300);

const child = spawn(EDGE, [
  '--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run',
  '--disable-features=Translate', '--force-device-scale-factor=1', '--no-proxy-server',
  `--remote-debugging-port=${PORT}`,
  `--user-data-dir=${process.env.TEMP || '/tmp'}\\edge-cdp-${PORT}`,
  'about:blank',
], { stdio: 'ignore' });

async function targets() {
  for (let i = 0; i < 60; i++) {
    try {
      const r = await fetch(`http://127.0.0.1:${PORT}/json/list`);
      const list = await r.json();
      const page = list.find((t) => t.type === 'page');
      if (page) return page;
    } catch { /* 还没起 */ }
    await sleep(250);
  }
  throw new Error('CDP 没起来');
}

let id = 0;
function send(ws, method, params = {}) {
  const mid = ++id;
  return new Promise((resolve, reject) => {
    const onMsg = (ev) => {
      const m = JSON.parse(ev.data);
      if (m.id !== mid) return;
      ws.removeEventListener('message', onMsg);
      m.error ? reject(new Error(method + ': ' + m.error.message)) : resolve(m.result);
    };
    ws.addEventListener('message', onMsg);
    ws.send(JSON.stringify({ id: mid, method, params }));
  });
}

const page = await targets();
const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });

await send(ws, 'Page.enable');
await send(ws, 'Runtime.enable');
await send(ws, 'Emulation.setDeviceMetricsOverride', {
  width: 1680, height: 1000, deviceScaleFactor: 1, mobile: false,
});
await send(ws, 'Page.navigate', { url });
await sleep(2600);
if (js) {
  const r = await send(ws, 'Runtime.evaluate', { expression: js, awaitPromise: true, returnByValue: true });
  if (r.exceptionDetails) console.error('JS 异常:', r.exceptionDetails.text, r.exceptionDetails.exception?.description || '');
  else if (r.result && r.result.value !== undefined) console.log('eval =>', JSON.stringify(r.result.value).slice(0, 400));
  await sleep(waitMs);
}
const shot = await send(ws, 'Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
fs.writeFileSync(out, Buffer.from(shot.data, 'base64'));
console.log('saved', out, fs.statSync(out).size, 'B');

ws.close();
child.kill();
process.exit(0);
