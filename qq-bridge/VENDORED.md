# 关于这个目录

这里的代码来自 **qq-bridge**（MIT 许可，Copyright (c) 2026 Derpyu520 and
qq-bridge / QSH contributors），并入 DSQQ 仓库是为了让使用者只面对一个项目。

- 上游：[Derpyu520/qq-bridge](https://github.com/Derpyu520/qq-bridge)
- 本目录是**修改版**，与原版的差异记录在 `MODIFICATIONS.md`
- 许可原文见同目录的 `LICENSE`

## 与上游的同步方式

并入时保留的是**工作树快照**，没有带上游的提交历史。后续要跟进上游改动，
请对照上游仓库手工合并 —— 这个目录里的 `MODIFICATIONS.md` 就是为此写的。

## 没有带过来的东西

| 项 | 为什么 |
| --- | --- |
| `node_modules/` | 打包时用 `pnpm install --config.node-linker=hoisted` 现装。源目录那份是 pnpm 的链接结构，直接复制会把每个链接解引用成实体，实测 26 MB 膨胀到 210 MB |
| `state/` | 含会话令牌、聊天记录缓存、`bridge.lock`；且该目录的 ACL 被加固过，本身就不可读 |
| `config.json` | 含 `ownerQQ` 与控制台访问令牌 —— 打进去等于把作者的身份与凭据发出去 |
| `.pnpm-store/` | 包管理缓存 |

打包脚本 `packaging/build-portable.py` 会按同样的规则处理它们。
