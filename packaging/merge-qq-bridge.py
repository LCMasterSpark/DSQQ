#!/usr/bin/env python3
"""
把 qq-bridge 的源码并入 DSQQ 仓库。

为什么并入：目标是「用户只看只用我一个 GitHub 项目」。原先 qq-bridge 是另一个目录、
另一份版本控制，用户得从两处取东西。它是 MIT 许可（Copyright (c) 2026 Derpyu520
and qq-bridge / QSH contributors），允许并入，但必须保留许可与署名 ——
LICENSE 原样带过来，另加一份 VENDORED.md 说明来源。

**不复制的东西**（复制它们等于把作者本机的状态发给所有人）：
    .git/          独立版本控制，并入后由外层仓库接管
    node_modules/  打包时用 pnpm hoisted 现装（源里那份是 pnpm 链接结构，复制会膨胀 8 倍）
    .pnpm-store/   包管理缓存
    state/         含会话令牌、聊天记录缓存、bridge.lock（且 ACL 受限）
    config.json    含 ownerQQ 与控制台访问令牌
"""

import io
import os
import shutil
import sys
from pathlib import Path

SOURCE = Path(r"D:\qq-bridge")
TARGET = Path(r"D:\CPP项目\DSQQ\qq-bridge")

# 目录级排除（按相对路径的顶层段匹配）。
EXCLUDE_TOP = {".git", "node_modules", ".pnpm-store", "state"}
EXCLUDE_FILES = {"config.json"}

VENDORED_NOTE = """# 关于这个目录

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
"""


def should_skip(relative: Path) -> bool:
    """按顶层段与文件名判断是否跳过。"""
    if relative.parts and relative.parts[0] in EXCLUDE_TOP:
        return True
    if relative.name in EXCLUDE_FILES:
        return True
    # 排除项也要按任意层级匹配一次，避免嵌套的 node_modules 混进来。
    return any(part in EXCLUDE_TOP for part in relative.parts)


def main() -> None:
    if not SOURCE.is_dir():
        sys.exit(f"源目录不存在：{SOURCE}")

    if TARGET.exists():
        print(f"目标已存在，先删除：{TARGET}")
        shutil.rmtree(TARGET)

    copied = 0
    skipped = 0
    total = 0

    for root, dirs, files in os.walk(SOURCE):
        relative_root = Path(root).relative_to(SOURCE)

        # 原地裁剪 dirs：os.walk 就不会往下走。
        dirs[:] = [
            d for d in dirs
            if d not in EXCLUDE_TOP and not should_skip(relative_root / d)
        ]

        for name in files:
            relative = relative_root / name
            if should_skip(relative):
                skipped += 1
                continue

            source = Path(root) / name
            destination = TARGET / relative
            destination.parent.mkdir(parents=True, exist_ok=True)

            try:
                shutil.copy2(source, destination)
                copied += 1
                total += source.stat().st_size
            except (OSError, PermissionError) as exc:
                print(f"  跳过 {relative}：{exc}")
                skipped += 1

    (TARGET / "VENDORED.md").write_text(VENDORED_NOTE, encoding="utf-8", newline="\n")

    print(f"并入完成：{copied} 个文件，{total / 1048576:.1f} MB（跳过 {skipped} 个）")
    print(f"目标：{TARGET}")

    # 自检：确认敏感项没被带过来。
    leaks = [
        p for p in ("state", "config.json", "node_modules", ".git", ".pnpm-store")
        if (TARGET / p).exists()
    ]
    if leaks:
        print()
        print("!! 自检失败：以下不该出现的东西被复制过来了 !!")
        for item in leaks:
            print(f"   {item}")
        sys.exit(1)

    print("  自检通过：未包含 state / config.json / node_modules / .git")


if __name__ == "__main__":
    main()
