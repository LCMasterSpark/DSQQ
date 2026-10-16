// 共享会话模式的注册期回归。
//
// 为什么单独写这个文件：共享模式曾被停用，原因是**回收会话时误删共享映射**——
// retireSession() 读用 sessionKeyFor(key)（拿到共享键），删却用原始业务 key，
// 于是任何会话被回收都会把共享映射删掉，所有会话集体失效 → 重建 → 再被删，
// 形成每 3 秒一轮的死循环，日志被刷爆。
//
// 重做方向（见 MODIFICATIONS.md）：引入独立注册表键，让"注册账本"与"策略账本"分离。
// 本文件在**真实的 bridge.js** 上验证这件事（bridgeHarness 把 bridge.js 放进 vm 执行），
// 而不是验证某个替身实现。
import assert from 'node:assert/strict';
import test from 'node:test';
import { bridgeHarness } from './audit-bridge-harness.mjs';

/** 共享模式下的注册表键（与 bridge.js 中的常量保持一致）。 */
const SHARED_KEY = '__shared__';

/** 构造一个开启共享会话模式的 harness。 */
async function sharedHarness() {
  return bridgeHarness({
    config: {
      allow: { private: ['123'], groups: ['456', '789'] },
      socialV2: { enabled: true, sessionMode: 'shared' },
    },
  });
}

test('共享模式下：多个会话共用同一个 DSH 会话，且登记在独立注册表键上', async () => {
  const bridge = await sharedHarness();
  try {
    const first = await bridge.ensureSession('group:456');
    const second = await bridge.ensureSession('group:789');
    const third = await bridge.ensureSession('private:123');

    assert.equal(first, second, '共享模式下不同会话应复用同一个 DSH 会话');
    assert.equal(second, third);

    const sessions = bridge.state.sessions;
    assert.equal(sessions[SHARED_KEY], first, `共享会话应登记在 ${SHARED_KEY} 上`);
    // 关键：不能再借用某个真实群号当所有者键（旧实现的根因之一）。
    assert.ok(!sessions['group:456'], '不应把共享会话登记在某个业务 key 上');

    // 策略账本仍按业务 key 各记一条 —— 这正是"两套账本"的设计。
    assert.equal(Object.keys(bridge.state.sessionPolicies).length, 3);
  } finally {
    await bridge.close();
  }
});

test('共享模式下：还有别的会话在用，回收一个不得销毁共享映射', async () => {
  const bridge = await sharedHarness();
  try {
    const sessionId = await bridge.ensureSession('group:456');
    await bridge.ensureSession('group:789');

    // 回收其中一个。旧实现会在这里把共享映射删掉，
    // 导致其它会话集体失效 → 重建 → 再被删（每 3 秒一轮）。
    bridge.retireSession('group:456');

    assert.equal(
      bridge.state.sessions[SHARED_KEY],
      sessionId,
      '仍有一个会话在共用这个共享会话时，映射必须保留',
    );
    assert.equal(bridge.state.sessionPolicies['group:456'], undefined, '被回收会话的策略应清除');

    // 剩下的会话应直接复用，而不是重建。
    const again = await bridge.ensureSession('group:789');
    assert.equal(again, sessionId, '剩余会话应复用同一个共享会话');
  } finally {
    await bridge.close();
  }
});

test('共享模式下：最后一个会话离开时才清理共享映射', async () => {
  const bridge = await sharedHarness();
  try {
    const sessionId = await bridge.ensureSession('group:456');
    await bridge.ensureSession('group:789');

    bridge.retireSession('group:456');
    assert.equal(bridge.state.sessions[SHARED_KEY], sessionId, '还有用户时应保留');

    bridge.retireSession('group:789');
    assert.equal(
      bridge.state.sessions[SHARED_KEY],
      undefined,
      '没有任何会话再使用它时，应清理共享映射，避免留下无人认领的会话',
    );

    // 清理之后重新发言应能正常重建，而不是永久卡在无映射状态。
    const rebuilt = await bridge.ensureSession('group:456');
    assert.ok(rebuilt, '清理后应能重建共享会话');
  } finally {
    await bridge.close();
  }
});

test('共享模式下：反复回收同一批会话不会进入死循环', async () => {
  const bridge = await sharedHarness();
  try {
    await bridge.ensureSession('group:456');
    await bridge.ensureSession('group:789');
    await bridge.ensureSession('private:123');

    // 复刻死循环的触发条件：反复回收、反复重建。
    for (let round = 0; round < 3; round++) {
      for (const key of ['group:456', 'group:789', 'private:123']) {
        bridge.retireSession(key);
      }
      // 每轮结束后重新建会话，全部应复用同一个共享会话。
      const id = await bridge.ensureSession('group:456');
      assert.ok(id, `第 ${round + 1} 轮重建应成功`);

      const other = await bridge.ensureSession('group:789');
      assert.equal(other, id, `第 ${round + 1} 轮内所有会话应共享同一个 DSH 会话`);
    }
  } finally {
    await bridge.close();
  }
});

test('按会话分开时：每个会话各有独立映射（回归：共享改动不得影响默认模式）', async () => {
  const bridge = await bridgeHarness({
    config: {
      allow: { private: ['123'], groups: ['456', '789'] },
      socialV2: { enabled: true, sessionMode: 'per-conversation' },
    },
  });

  try {
    const a = await bridge.ensureSession('group:456');
    const b = await bridge.ensureSession('group:789');

    assert.notEqual(a, b, '分开模式下不同会话必须是不同的 DSH 会话');
    assert.equal(bridge.state.sessions['group:456'], a);
    assert.equal(bridge.state.sessions['group:789'], b);
    assert.equal(bridge.state.sessions[SHARED_KEY], undefined, '分开模式不应产生共享注册键');

    // 回收其中一个，另一个必须不受影响。
    bridge.retireSession('group:456');
    assert.equal(bridge.state.sessions['group:456'], undefined);
    assert.equal(bridge.state.sessions['group:789'], b, '回收一个会话不应影响另一个');

    // 被回收的那个重建后应是**新的**会话，不复用另一个。
    const rebuilt = await bridge.ensureSession('group:456');
    assert.notEqual(rebuilt, b, '分开模式下重建不应复用别的会话');
  } finally {
    await bridge.close();
  }
});
