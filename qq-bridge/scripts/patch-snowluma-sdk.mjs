// 修复 @snowluma 系列 npm 包的发布打包 bug：dist 内的相对导入缺少 .js 扩展名，
// 纯 Node ESM 无法解析（bundler 可以）。postinstall 时对每个包 dist 下所有 .js
// 文件做机械修补：仅当目标文件存在时才补扩展名，否则保持原样。
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, '..');

const TARGETS = ['@snowluma/sdk', '@snowluma/mcp'];

function walk(dir, out = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) walk(full, out);
    else out.push(full);
  }
  return out;
}

let total = 0;
for (const pkg of TARGETS) {
  const distRoot = path.join(ROOT, 'node_modules', pkg, 'dist');
  if (!fs.existsSync(distRoot)) {
    console.log(`[patch-snowluma] ${pkg}: dist 不存在，跳过`);
    continue;
  }
  let patched = 0;
  for (const file of walk(distRoot)) {
    if (!file.endsWith('.js')) continue;
    const src = fs.readFileSync(file, 'utf8');
    const next = src.replace(/from\s+'(\.[^']+)'/g, (m, spec) => {
      if (spec.endsWith('.js') || spec.endsWith('.json')) return m;
      const target = path.resolve(path.dirname(file), spec);
      if (fs.existsSync(target + '.js')) return m.replace(spec, spec + '.js');
      if (fs.existsSync(target + '.json')) return m.replace(spec, spec + '.json');
      return m;
    });
    if (next !== src) {
      fs.writeFileSync(file, next);
      patched += 1;
    }
  }
  console.log(`[patch-snowluma] ${pkg}: 修补了 ${patched} 个文件`);
  total += patched;
}
// ── 补丁二：WebSocket 握手卡死（会造成「桥接活着但永久失聪」） ──────────────────
//
// 症状：QQ 侧一切正常，机器人一声不吭，桥接日志上**一行相关输出都没有**。
// 实测复现（2026-10-16，用户在便携包上撞到）：
//
//   @snowluma/sdk 的 dist/client/websocket-client.js 里，connect() 只在 socket 的
//   `open` 或 `close` 时结算 connectPromise，`error` 仅转发给上层不作结算；
//   而 scheduleReconnect 的重排是靠定时器回调里的 `connect().catch(...)` 完成的。
//   于是只要有一次握手**既不 open 也不 close**，connectPromise 就永远 pending：
//   后续每次 connect() 都在函数开头 `if (this.connectPromise) return this.connectPromise;`
//   直接返回那个死掉的 promise —— 重连再也不会被排上，而且没有任何日志。
//
// 触发条件不是臆想：端口在听、但对 HTTP 升级请求一句话都不回时（SnowLuma 重启到
// 一半正是这样），Node 原生 WebSocket 既不报错也不关闭。用裸 net 服务器复现，
// 40 秒内零事件、reconnectAttempts 停在 0、reconnectTimer 为 null。
//
// 补法：给握手套一个超时。到点就 close 掉那个 socket —— close 会走 SDK 自己的
// close 分支（connectPromise = null → reject → handleClose → scheduleReconnect），
// 所以是顺着它原有的机制恢复，不引入新的竞态。
const HANDSHAKE_ANCHOR = `            const socket = new this.WebSocketCtor(url, this.protocols);
            this.socket = socket;
            const cleanup = [
                addSocketListener(socket, 'open', () => {`;
const HANDSHAKE_PATCHED = `            const socket = new this.WebSocketCtor(url, this.protocols);
            this.socket = socket;
            // [qq-bridge 补丁] 握手超时：见 scripts/patch-snowluma-sdk.mjs 的「补丁二」。
            // 卡住的握手不报错也不关闭，会让 connectPromise 永远 pending 并吃掉所有重连。
            const handshakeTimer = setTimeout(() => {
                if (this.socket !== socket || socket.readyState === WEBSOCKET_OPEN)
                    return;
                this.emitter.emit('error', new SnowLumaConnectionError('SnowLuma WebSocket 握手超时（qq-bridge 补丁）'));
                try {
                    socket.close();
                }
                catch { }
            }, this.handshakeTimeoutMs ?? 15000);
            const cleanup = [
                () => clearTimeout(handshakeTimer),
                addSocketListener(socket, 'open', () => {`;

{
  const wsFile = path.join(ROOT, 'node_modules', '@snowluma', 'sdk', 'dist', 'client', 'websocket-client.js');
  if (!fs.existsSync(wsFile)) {
    console.log('[patch-snowluma] websocket-client.js 不存在，跳过握手超时补丁');
  } else {
    const src = fs.readFileSync(wsFile, 'utf8');
    if (src.includes('[qq-bridge 补丁]')) {
      console.log('[patch-snowluma] websocket-client.js 已打过握手超时补丁，跳过');
    } else if (!src.includes(HANDSHAKE_ANCHOR)) {
      // 上游改了这段代码。补丁失效必须**响**，否则问题是静默的（正是这个 bug 的性质）。
      console.log('[patch-snowluma] ⚠️ websocket-client.js 里找不到握手补丁的锚点 —— 上游可能改了 connect()，'
        + '请人工核对「WebSocket 握手卡死」这个坑是否还在！');
    } else {
      fs.writeFileSync(wsFile, src.replace(HANDSHAKE_ANCHOR, HANDSHAKE_PATCHED));
      console.log('[patch-snowluma] websocket-client.js 已加握手超时（15 秒）');
    }
  }
}

console.log(`[patch-snowluma] 合计 ${total} 个文件`);
