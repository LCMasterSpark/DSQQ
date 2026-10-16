#!/usr/bin/env python3
"""
构建便携发行包（DSQQ + SnowLuma + qq-bridge）。

产出：
    packaging/dist/DSQQ-Portable-<版本>.zip
    packaging/dist/DSQQ-Portable-<版本>/
        ├─ DSQQ.exe ...              控制台（自包含，免装 .NET）
        ├─ 使用说明.txt
        └─ services/
            ├─ snowluma/             QQ 接入（自带 node.exe，两个组件共用）
            └─ qq-bridge/            AI 适配

目录名 services/ 不是随便起的：DSQQ 的 ServiceLocator 会优先在
<程序目录>/services/<名字> 下找组件，解压到哪都能自动认出来。

═══ 最重要的一条：绝不打包用户数据 ═══

SnowLuma:
    config/   ← 含 QQ 号、各账号 accessToken、consent.json（协议同意记录）
    data/     ← 登录态等运行数据
    logs/     ← 日志
qq-bridge:
    config.json ← 含 ownerQQ、控制台访问令牌
    state/      ← 含会话令牌、聊天记录缓存、bridge.lock

把任何一个打进去，都是把开发者的 QQ 号和凭据发给别人。
下面 EXCLUDE 里逐条列出，新增目录时务必回头检查这一节。
"""

import os
import re
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

# ── 路径 ────────────────────────────────────────────────────────────────
DSQQ_ROOT = Path(r"D:\CPP项目\DSQQ")
SNOWLUMA_SRC = Path(r"D:\SnowLuma")
BRIDGE_SRC = Path(r"D:\qq-bridge")
DIST = DSQQ_ROOT / "packaging" / "dist"


def read_version() -> str:
    """从 Directory.Build.props 读版本号，保证与程序集版本一致。"""
    props = DSQQ_ROOT / "Directory.Build.props"
    text = props.read_text(encoding="utf-8")
    m = re.search(r"<Version>([^<]+)</Version>", text)
    if not m:
        sys.exit("无法从 Directory.Build.props 读出 <Version>")
    return m.group(1).strip()


# ── 排除规则 ────────────────────────────────────────────────────────────
# SnowLuma：程序文件全要，用户数据全不要。
SNOWLUMA_EXCLUDE_DIRS = {"config", "data", "logs"}
SNOWLUMA_EXCLUDE_FILES: set[str] = set()

# qq-bridge：同理。.pnpm-store 是包管理缓存（25 MB），不进发行包。
BRIDGE_EXCLUDE_DIRS = {"state", ".git", ".pnpm-store", "node_modules/.cache"}
BRIDGE_EXCLUDE_FILES = {"config.json"}


def copy_tree(src: Path, dst: Path, exclude_dirs: set[str], exclude_files: set[str]) -> tuple[int, int]:
    """
    复制目录，跳过排除项。

    返回 (文件数, 字节数)。相对路径匹配排除项，因此 'node_modules/.cache'
    这种带层级的规则也能生效。
    """
    if not src.is_dir():
        sys.exit(f"源目录不存在：{src}")

    count = 0
    total = 0
    dst.mkdir(parents=True, exist_ok=True)

    for root, dirs, files in os.walk(src):
        rel_root = Path(root).relative_to(src)

        # 目录级排除：原地裁剪 dirs，os.walk 就不会往下走。
        dirs[:] = [
            d for d in dirs
            if str((rel_root / d).as_posix()) not in exclude_dirs and d not in exclude_dirs
        ]

        for name in files:
            rel = (rel_root / name).as_posix()
            if rel in exclude_files or name in exclude_files:
                continue

            source = Path(root) / name
            target = dst / rel_root / name
            target.parent.mkdir(parents=True, exist_ok=True)

            try:
                shutil.copy2(source, target)
                count += 1
                total += source.stat().st_size
            except (OSError, PermissionError) as exc:
                # 单个文件失败不该让整包失败（例如正在被占用的日志）。
                print(f"    跳过 {rel}：{exc}")

    return count, total


def find_node_and_pnpm() -> tuple[Path, Path]:
    """
    找打包时用的 node 与 pnpm。

    node 优先用 SnowLuma 自带的那份（它最终也会被打进包里，版本已验证满足
    qq-bridge 的 >= 22.13）。pnpm 只在**打包时**用来装依赖，不进发行包，
    所以从 DSH 运行时借。
    """
    candidates = [
        SNOWLUMA_SRC / "node.exe",
        Path(os.environ.get("USERPROFILE", "")) / ".dsh" / "dsh-runtimes" /
        "dsh-primary-runtime" / "dependencies" / "node" / "bin" / "node.exe",
    ]
    node = next((c for c in candidates if c.is_file()), None)
    if node is None:
        sys.exit("找不到 node.exe（试过 SnowLuma 与 DSH 运行时）")

    pnpm = (Path(os.environ.get("USERPROFILE", "")) / ".dsh" / "dsh-runtimes" /
            "dsh-primary-runtime" / "dependencies" / "pnpm" / "bin" / "pnpm.cjs")
    if not pnpm.is_file():
        sys.exit(f"找不到 pnpm：{pnpm}（打包时需要它来安装桥接依赖）")

    return node, pnpm


def install_bridge_deps(bridge_dst: Path, node: Path, pnpm: Path) -> None:
    """
    在打包目录里安装 qq-bridge 的依赖。

    为什么不用源目录那份 node_modules：
    项目用的是 pnpm，node_modules 里 639 个条目是符号链接、其余大量是硬链接
    （真实文件都在 .pnpm 下）。直接复制会把每个链接解引用成实体副本，
    实测 26 MB 膨胀到 210 MB。改用 hoisted 模式重新安装，得到的是
    npm 风格的扁平结构 —— 无链接、体积正常、用户解压不需要任何特权。
    """
    env = os.environ.copy()
    env["PATH"] = str(node.parent) + os.pathsep + env.get("PATH", "")

    # --ignore-scripts：postinstall 里写的是裸 `node`，依赖 PATH 上有 node。
    # 打包机通常没有全局 node，所以先跳过，下面用绝对路径自己跑。
    cmd = [str(node), str(pnpm), "install",
           "--config.node-linker=hoisted", "--prod", "--ignore-scripts"]
    print("        安装依赖（hoisted 扁平结构）…")
    result = subprocess.run(cmd, cwd=bridge_dst, env=env,
                            capture_output=True, text=True, encoding="utf-8", errors="replace")
    if result.returncode != 0:
        print(result.stdout[-2000:])
        print(result.stderr[-2000:])
        sys.exit("依赖安装失败")

    # 手动跑补丁：修复 @snowluma/* 包 dist 里缺失的 .js 扩展名。
    # 这一步**不能省** —— 纯 Node ESM 解析不了那些相对导入，跳过后桥接起不来。
    patch = bridge_dst / "scripts" / "patch-snowluma-sdk.mjs"
    if not patch.is_file():
        sys.exit(f"缺少补丁脚本：{patch}（桥接的 postinstall 依赖它）")

    print("        运行 @snowluma SDK 补丁 …")
    result = subprocess.run([str(node), str(patch)], cwd=bridge_dst, env=env,
                            capture_output=True, text=True, encoding="utf-8", errors="replace")
    print(f"        {result.stdout.strip()[:200]}")
    if result.returncode != 0:
        print(result.stderr[-1500:])
        sys.exit("补丁脚本执行失败")


def publish_dsqq(target: Path) -> None:
    """构建并发布 DSQQ 到目标目录。"""
    print("  [1/4] 构建 DSQQ（Release, self-contained, 单文件）…")
    cmd = [
        "dotnet", "publish", str(DSQQ_ROOT / "DSQQ" / "DSQQ.csproj"),
        "--configuration", "Release",
        "--runtime", "win-x64",
        "--self-contained", "true",
        "--output", str(target),
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:DebugType=none",
        "--nologo", "-v", "quiet",
    ]
    result = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if result.returncode != 0:
        print(result.stdout[-3000:])
        print(result.stderr[-3000:])
        sys.exit("DSQQ 发布失败")


def write_launcher(target: Path) -> None:
    """
    写「首次运行.bat」。

    编码用 GBK（cp936）：中文 Windows 的 cmd 默认代码页就是它。
    写成 UTF-8 的话，cmd 会把中文读成乱码 —— 而这个文件的作用恰恰是
    让第一次用的人看懂提示，乱码等于白写。
    """
    text = """@echo off
cd /d "%~dp0"
echo.
echo   DSQQ 便携版 —— 首次运行
echo   ================================
echo.
echo   接下来会自动做一次配置：
echo     · 检查 DSH / SnowLuma / qq-bridge 是否就绪
echo     · 补上 OneBot 端口、把管理员与白名单写进桥接
echo.
echo   需要你本人做的只有两件：
echo     1. 在 SnowLuma 控制台同意协议并扫码登录 QQ
echo     2. 在 DSQQ 里勾选要启用的群
echo.
echo   按任意键启动 DSQQ...
pause >nul

start "" "DSQQ.exe" --first-run
"""
    path = target / "首次运行.bat"
    path.write_text(text, encoding="gbk", newline="\r\n")


def write_readme(target: Path, version: str) -> None:
    """写使用说明。用 UTF-8 无 BOM 并强制 CRLF —— 记事本打开不乱码、换行正常。"""
    text = f"""DSQQ 便携版 {version}
{'=' * 46}

这是一个 QQ AI 机器人控制台，已经打包好需要的东西（除 DSH）。
解压到任意目录都能用，不需要安装。

────────────────────────────────────────
第一次使用
────────────────────────────────────────

1. 前提：这台机器上已经装好 DSH（DeepSeek Harness）并登录过。
   DSQQ 会自动找到它（默认在 %USERPROFILE%\\.dsh）。

2. 双击「首次运行.bat」。
   它会启动 DSQQ 并自动做一次配置：
     · 检查 DSH / SnowLuma / qq-bridge 是否就绪
     · 补上 OneBot 端口（3000 / 3001）
     · 把管理员 QQ 与白名单写进桥接
   界面上的「体检」卡片会逐条告诉你现在缺什么、下一步做什么。

3. 需要你本人做的只有两件（程序无法代劳）：
     · QQ 登录：到「服务管理」页点「打开 SnowLuma 控制台」，
       在那边同意用户协议并用 QQ 扫码登录（只需一次）。
     · 勾群：登录后回到 DSQQ，「桥接控制台 → 白名单」页
       填上你自己的 QQ 号并勾选要启用的群，保存。

完成。之后在群里 @ 她 就会回话。

────────────────────────────────────────
目录说明
────────────────────────────────────────

DSQQ.exe            控制台本体
首次运行.bat        首次部署入口（之后直接开 DSQQ.exe 即可）
services\\snowluma   负责连 QQ（自带 node.exe，qq-bridge 也复用它）
services\\qq-bridge  负责接 AI

不要把 DSQQ.exe 单独挪出来 —— 它靠 services\\ 目录找组件。
整个文件夹一起移动没问题。

────────────────────────────────────────
常见问题
────────────────────────────────────────

· 收不到群消息 / 群列表是空的
  → 多半是 QQ 没登录。到「服务管理」页点「打开 SnowLuma 控制台」检查登录状态。

· 体检卡片说"OneBot 接口没在监听"
  → 同上，OneBot 端口是在 QQ 登录成功之后才开的。

· 提示找不到 DSH
  → 先安装并运行一次 DSH。它是 AI 能力的来源，不包含在这个包里。

· 端口被占用
  → 到「设置」页改端口，或在「服务管理」页看看是不是已经有一个实例在跑。

· 想换 QQ 号
  → 在 SnowLuma 控制台里换号登录，DSQQ 会自动跟上（token 会自动重新发现）。
"""
    path = target / "使用说明.txt"
    path.write_text(text, encoding="utf-8", newline="\r\n")


def make_zip(source_dir: Path, zip_path: Path) -> None:
    """打包成 zip（保留顶层目录名）。"""
    print("  [4/4] 压缩…")
    if zip_path.exists():
        zip_path.unlink()

    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as zf:
        for root, _dirs, files in os.walk(source_dir):
            for name in files:
                full = Path(root) / name
                zf.write(full, full.relative_to(source_dir.parent))


def main() -> None:
    version = read_version()
    name = f"DSQQ-Portable-{version}"
    target = DIST / name

    print(f"构建 {name}")
    print(f"  DSQQ      : {DSQQ_ROOT}")
    print(f"  SnowLuma  : {SNOWLUMA_SRC}")
    print(f"  qq-bridge : {BRIDGE_SRC}")
    print()

    if target.exists():
        print("  清理旧的输出…")
        shutil.rmtree(target)
    DIST.mkdir(parents=True, exist_ok=True)

    # 1) DSQQ 本体
    publish_dsqq(target)

    # 2) SnowLuma（排除用户数据）
    print("  [2/4] 复制 SnowLuma …")
    sl_dst = target / "services" / "snowluma"
    sl_count, sl_bytes = copy_tree(SNOWLUMA_SRC, sl_dst, SNOWLUMA_EXCLUDE_DIRS, SNOWLUMA_EXCLUDE_FILES)
    print(f"        {sl_count} 个文件，{sl_bytes / 1048576:.1f} MB")

    # 3) qq-bridge：复制源码，依赖现场安装
    #    node_modules 不走复制 —— 见 install_bridge_deps 里的说明。
    print("  [3/4] 复制 qq-bridge 源码 …")
    node, pnpm = find_node_and_pnpm()
    br_dst = target / "services" / "qq-bridge"
    src_exclude = BRIDGE_EXCLUDE_DIRS | {"node_modules"}
    br_count, br_bytes = copy_tree(BRIDGE_SRC, br_dst, src_exclude, BRIDGE_EXCLUDE_FILES)
    print(f"        {br_count} 个文件，{br_bytes / 1048576:.1f} MB")
    install_bridge_deps(br_dst, node, pnpm)

    write_launcher(target)
    write_readme(target, version)

    # 4) 压缩
    zip_path = DIST / f"{name}.zip"
    make_zip(target, zip_path)

    total = sum(f.stat().st_size for f in target.rglob("*") if f.is_file())
    print()
    print(f"完成：{zip_path}")
    print(f"  解压后 {total / 1048576:.1f} MB，压缩后 {zip_path.stat().st_size / 1048576:.1f} MB")

    # 打包后自检：确认用户数据没有被带进去。
    # 这是最后一次拦截机会 —— 一旦发出去就收不回来了。
    leaks = []
    for pattern in ("services/snowluma/config", "services/snowluma/data", "services/snowluma/logs",
                    "services/qq-bridge/state", "services/qq-bridge/config.json"):
        hit = target / pattern
        if hit.exists():
            leaks.append(pattern)

    if leaks:
        print()
        print("!! 自检失败：以下用户数据被误打包，请检查排除规则 !!")
        for item in leaks:
            print(f"   {item}")
        sys.exit(1)

    print("  自检通过：未包含 QQ 号、令牌或聊天记录。")


if __name__ == "__main__":
    main()
