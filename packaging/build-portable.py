#!/usr/bin/env python3
"""
构建便携发行包（DSQQ + SnowLuma + qq-bridge）。

产出：
    packaging/dist/DSQQ-Portable-<版本>.zip
    packaging/dist/DSQQ-Portable-<版本>/
        ├─ DSQQ.exe ...              控制台（自包含，免装 .NET）
        ├─ 首次运行.bat               首次部署入口
        ├─ 使用说明.txt
        └─ services/
            ├─ snowluma/             QQ 接入（自带 node.exe，两个组件共用）
            └─ qq-bridge/            AI 适配

目录名 services/ 不是随便起的：DSQQ 的 ServiceLocator 会优先在
<程序目录>/services/<名字> 下找组件，解压到哪都能自动认出来。

═══ 两个组件的来源 ═══

qq-bridge：直接来自本仓库的 qq-bridge/ 目录（已并入，MIT）。
           node_modules 现装，不复制源目录那份 —— 见 install_bridge_deps 的说明。

SnowLuma：**从它自己的 GitHub Release 下载**。
          优先用本机已装的那份（--local-snowluma），没有则从网上取。
          这样 CI 不需要任何预置文件就能构建完整包，
          「用户只看一个项目」这个目标才成立。
          下载物缓存在 packaging/cache/，重复构建不重复下载。

          ⚠️ 实测（本机 2026-10）：到 GitHub Release 的 HEAD 请求 2 秒返回，
          但大文件下载被严重限速 —— 36 MB 的包 15 分钟只下到 3 MB。
          所以**本地打包请加 --local-snowluma**（用本机已装的那份，秒级完成）；
          下载路径留给 CI —— 在 GitHub 自己的基础设施上取自己的 Release 没有这个问题。

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

import argparse
import io
import os
import re
import shutil
import subprocess
import sys
import urllib.request
import zipfile
from pathlib import Path

# ── 让中文输出在英文 Windows 上也能打印 ──
#
# Python 在 Windows 上把 stdout 绑到控制台的本地代码页：中文 Windows 是 cp936
# （能编码中文），而 **英文 Windows 是 cp1252**（不能）。本脚本满屏都是中文，
# 于是在 CI 上第一行 `print(f"构建 {name}")` 就抛
#     UnicodeEncodeError: 'charmap' codec can't encode characters in position 0-1
# —— 而且它发生在做任何事之前，从外部看只表现为"退出码 1"，极难猜到。
#
# 这不是排版问题，是让脚本能在非中文 Windows 上跑起来的前提。
# 顺带在 workflow 里也设了 PYTHONIOENCODING=utf-8，两处一起兜。
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        # 极老的 Python 或被重定向到不支持 reconfigure 的对象时忽略。
        pass

# ── 路径 ────────────────────────────────────────────────────────────────
DSQQ_ROOT = Path(__file__).resolve().parent.parent
DIST = DSQQ_ROOT / "packaging" / "dist"
CACHE = DSQQ_ROOT / "packaging" / "cache"

# qq-bridge 来自本仓库（已并入）。
BRIDGE_SRC = DSQQ_ROOT / "qq-bridge"

# SnowLuma 的默认下载版本；可用 --snowluma-version 覆盖。
DEFAULT_SNOWLUMA_VERSION = "1.14.22"

# 本机已装 SnowLuma 时的常见位置（--local-snowluma 用它兜底）。
LOCAL_SNOWLUMA_CANDIDATES = [
    Path(r"D:\SnowLuma"),
    Path(os.environ.get("SNOWLUMA_HOME", "")),
]

# ── 排除规则 ────────────────────────────────────────────────────────────
# SnowLuma：程序文件全要，用户数据全不要。
SNOWLUMA_EXCLUDE_DIRS = {"config", "data", "logs"}
SNOWLUMA_EXCLUDE_FILES: set[str] = set()

# qq-bridge：同理。.pnpm-store 是包管理缓存，不进发行包。
BRIDGE_EXCLUDE_DIRS = {"state", ".git", ".pnpm-store", "node_modules", "node_modules/.cache"}
BRIDGE_EXCLUDE_FILES = {"config.json"}


def read_version() -> str:
    """从 Directory.Build.props 读版本号，保证与程序集版本一致。"""
    props = DSQQ_ROOT / "Directory.Build.props"
    text = props.read_text(encoding="utf-8")
    m = re.search(r"<Version>([^<]+)</Version>", text)
    if not m:
        sys.exit("无法从 Directory.Build.props 读出 <Version>")
    return m.group(1).strip()


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


def resolve_snowluma(version: str, prefer_local: bool) -> Path:
    """
    拿到一份可用的 SnowLuma 程序目录。

    顺序：本机已装的那份（--local-snowluma）→ 缓存里已下载的 → 从 GitHub 下载。
    返回的目录只含程序文件；用户数据由 copy_tree 的排除规则再挡一道。
    """
    if prefer_local:
        for candidate in LOCAL_SNOWLUMA_CANDIDATES:
            if candidate and (candidate / "index.mjs").is_file():
                print(f"        用本机已装的：{candidate}")
                return candidate
        print("        本机没找到可用的 SnowLuma，改为下载")

    CACHE.mkdir(parents=True, exist_ok=True)
    extracted = CACHE / f"snowluma-{version}-win-x64"

    if (extracted / "index.mjs").is_file():
        print(f"        用缓存：{extracted}")
        return extracted

    # 优先用完整版（内置 Node.js，用户不必另装）；lite 版需要自备 Node。
    archive = CACHE / f"SnowLuma-v{version}-win-x64.zip"
    url = (
        f"https://github.com/SnowLuma/SnowLuma/releases/download/"
        f"v{version}/SnowLuma-v{version}-win-x64.zip"
    )

    if not archive.is_file():
        print(f"        下载 {url}")
        try:
            with urllib.request.urlopen(url, timeout=180) as response, \
                    open(archive, "wb") as out:
                shutil.copyfileobj(response, out)
        except Exception as exc:  # noqa: BLE001 - 网络异常种类多，统一报错退出
            sys.exit(f"下载 SnowLuma 失败：{exc}\n"
                     f"可以手动下载后放到 {CACHE}，或用 --local-snowluma 指定本机已装的那份。")

    print(f"        解压到 {extracted}")
    if extracted.exists():
        shutil.rmtree(extracted)
    extracted.mkdir(parents=True)

    with zipfile.ZipFile(archive) as zf:
        zf.extractall(extracted)

    # 有些包会多套一层同名目录，往下探一层。
    if not (extracted / "index.mjs").is_file():
        for child in extracted.iterdir():
            if child.is_dir() and (child / "index.mjs").is_file():
                return child

    if not (extracted / "index.mjs").is_file():
        sys.exit(f"解压后没找到 index.mjs，包结构可能变了：{extracted}")

    return extracted


def find_node_and_pnpm(snowluma_dir: Path) -> tuple[Path, Path, Path | None]:
    """
    找打包时用的 node 与 pnpm。

    node 优先用 SnowLuma 自带的那份（它最终也会被打进包里，版本已验证满足
    qq-bridge 的 >= 22.13）。

    pnpm 只在**打包时**用来装依赖，不进发行包。查找顺序：
        1) DSH 运行时自带的那份（开发机上的常见位置）
        2) PATH 上的 pnpm（CI 用 pnpm/action-setup 装的走这条）
    两条都没有才报错 —— 因为这是在 CI 上构建完整包的前提。
    """
    node_candidates = [
        snowluma_dir / "node.exe",
        Path(os.environ.get("USERPROFILE", "")) / ".dsh" / "dsh-runtimes" /
        "dsh-primary-runtime" / "dependencies" / "node" / "bin" / "node.exe",
    ]
    # CI 上 SnowLuma 自带 node.exe，所以通常第一条就命中；
    # PATH 上的 node 作为最后兜底。
    on_path_node = shutil.which("node")
    if on_path_node:
        node_candidates.append(Path(on_path_node))

    node = next((c for c in node_candidates if c.is_file()), None)
    if node is None:
        sys.exit("找不到 node.exe（试过 SnowLuma 自带、DSH 运行时与 PATH）")

    pnpm_candidates: list[Path] = []

    # PATH 上的优先：CI 用 pnpm/action-setup 装的走这条。
    for name in ("pnpm", "pnpm.cjs", "pnpm.js"):
        found = shutil.which(name)
        if found:
            pnpm_candidates.append(Path(found))

    pnpm_candidates.append(
        Path(os.environ.get("USERPROFILE", "")) / ".dsh" / "dsh-runtimes" /
        "dsh-primary-runtime" / "dependencies" / "pnpm" / "bin" / "pnpm.cjs")

    pnpm = next((c for c in pnpm_candidates if c.is_file()), None)
    if pnpm is None:
        sys.exit("找不到 pnpm（试过 PATH 与 DSH 运行时）。"
                 "打包时需要它安装桥接依赖；CI 上请先 actions/setup-node + pnpm/action-setup。")

    # npm 作为兜底安装器：它随 setup-node 的 Node 一起装，CI 上一定有。
    # SnowLuma 自带的 node.exe 里**没有** npm，所以这里可能拿不到 —— 那也没关系，
    # 只要能走 pnpm 就行。
    npm = None
    for name in ("npm.cmd", "npm.exe", "npm"):
        found = shutil.which(name)
        if found:
            npm = Path(found)
            break

    return node, pnpm, npm


def install_bridge_deps(bridge_dst: Path, node: Path, pnpm: Path, npm: Path | None) -> str:
    """
    在打包目录里安装 qq-bridge 的依赖，返回实际用的工具名。

    为什么不直接复制仓库里那份 node_modules：
    项目用的是 pnpm，node_modules 里几百个条目是符号链接、其余大量是硬链接
    （真实文件都在 .pnpm 下）。直接复制会把每个链接解引用成实体副本，
    实测 26 MB 膨胀到 210 MB。重新安装能得到 npm 风格的扁平结构 ——
    无链接、体积正常、用户解压不需要任何特权。

    ⚠️ **多种调用方式依次尝试**。CI 上这一步失败过两次，而本机怎么试都是好的，
    说明差异在"pnpm 长什么样"而不是逻辑：它可能是 .cjs（JS 脚本）、.cmd（批处理）、
    .exe（独立二进制），甚至是**没有扩展名的 shell 脚本**（pnpm/action-setup
    在非 Windows 上装的就是这种）。与其猜是哪一种，不如全试一遍，谁能跑通用谁；
    最后再兜底到 npm（Node 自带，只要有 setup-node 就一定有）。
    """
    env = os.environ.copy()
    # 让 pnpm 自己能找到 node（它内部会 spawn node），也顺带修好 postinstall。
    env["PATH"] = str(node.parent) + os.pathsep + env.get("PATH", "")

    pnpm_args = ["install", "--config.node-linker=hoisted", "--prod", "--ignore-scripts"]

    # 候选调用方式：JS 脚本交给 node；其余直接执行，再退一步交给 cmd.exe。
    strategies: list[tuple[str, list[str]]] = []

    if pnpm.suffix.lower() in (".cjs", ".js", ".mjs"):
        strategies.append((f"node {pnpm.name}", [str(node), str(pnpm)]))
    else:
        strategies.append((pnpm.name, [str(pnpm)]))
        # 无扩展名的脚本或 .ps1 之类，CreateProcess 直接执行会失败，交给 cmd 解析。
        strategies.append((f"cmd /c {pnpm.name}", ["cmd.exe", "/c", str(pnpm)]))

    # 再挂上 PATH 上的其它形态（同一个 pnpm 可能有多种包装）。
    for name in ("pnpm.cmd", "pnpm.exe", "pnpm"):
        found = shutil.which(name)
        if found and Path(found) != pnpm:
            strategies.append((name, [found]))

    # 最后兜底：npm。它随 setup-node 的 Node 一起装，一定有；
    # --omit=dev 等价于 pnpm 的 --prod。仓库里有 package-lock.json，解析是确定的。
    if npm is not None:
        strategies.append((f"{npm.name}（兜底）",
                           [str(npm), "install", "--omit=dev", "--ignore-scripts"]))

    errors: list[str] = []

    for label, base in strategies:
        args = base if label.endswith("（兜底）") else base + pnpm_args
        print(f"        尝试用 {label} 安装依赖 …")

        try:
            result = subprocess.run(args, cwd=bridge_dst, env=env, capture_output=True,
                                    text=True, encoding="utf-8", errors="replace", timeout=900)
        except (OSError, subprocess.SubprocessError) as exc:
            errors.append(f"{label}: {type(exc).__name__}: {exc}")
            print(f"          ✗ {type(exc).__name__}: {exc}")
            continue

        if result.returncode == 0:
            print(f"          ✓ 成功（{label}）")
            break

        tail = (result.stderr or result.stdout or "").strip()[-400:]
        errors.append(f"{label}: exit {result.returncode} — {tail}")
        print(f"          ✗ exit {result.returncode}")
        for line in tail.splitlines()[-6:]:
            print(f"            {line}")
    else:
        print()
        print("!! 所有安装方式都失败了，逐条如下 !!")
        for item in errors:
            print(f"   {item}")
        sys.exit("依赖安装失败（已尝试 pnpm 的多种形态与 npm 兜底）")

    # 校验依赖真的落地了：只靠退出码不够 —— 工具可能"成功"但什么都没装。
    if not (bridge_dst / "node_modules").is_dir():
        sys.exit("安装命令返回成功，但 node_modules 不存在")

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

    return strategies[len(errors)][0] if errors else strategies[0][0]


def publish_dsqq(target: Path, reuse_from: Path | None = None) -> None:
    """
    把 DSQQ 放进目标目录。

    参数 reuse_from：若给出且是有效目录，直接复制它而不重新 publish ——
    CI 里 release 工作流已经发布过一次 self-contained，没必要再来一遍
    （单文件包 170 MB，重复发布很费时间）。
    """
    if reuse_from is not None and (reuse_from / "DSQQ.exe").is_file():
        print(f"  [1/4] 复用已发布的 DSQQ：{reuse_from}")
        for item in reuse_from.iterdir():
            destination = target / item.name
            if item.is_dir():
                shutil.copytree(item, destination, dirs_exist_ok=True)
            else:
                shutil.copy2(item, destination)
        return

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
    parser = argparse.ArgumentParser(description="构建 DSQQ 便携发行包")
    parser.add_argument("--local-snowluma", action="store_true",
                        help="用本机已装的 SnowLuma，而不是从 GitHub 下载")
    parser.add_argument("--snowluma-version", default=DEFAULT_SNOWLUMA_VERSION,
                        help=f"要下载的 SnowLuma 版本（默认 {DEFAULT_SNOWLUMA_VERSION}）")
    parser.add_argument("--dsqq-from", type=Path, default=None,
                        help="复用这个目录里已发布的 DSQQ，而不是重新 publish（CI 用，省一次 170 MB 的发布）")
    args = parser.parse_args()

    version = read_version()
    name = f"DSQQ-Portable-{version}"
    target = DIST / name

    print(f"构建 {name}")
    print(f"  DSQQ      : {DSQQ_ROOT / 'DSQQ'}")
    print(f"  qq-bridge : {BRIDGE_SRC}")
    print()

    if not BRIDGE_SRC.is_dir():
        sys.exit(f"找不到 qq-bridge 源码：{BRIDGE_SRC}\n"
                 f"它应当已并入本仓库；若是全新检出，确认 qq-bridge/ 目录在。")

    if target.exists():
        print("  清理旧的输出…")
        shutil.rmtree(target)
    DIST.mkdir(parents=True, exist_ok=True)

    # 1) DSQQ 本体
    publish_dsqq(target, args.dsqq_from)

    # 2) SnowLuma（下载或本机，再排除用户数据）
    print("  [2/4] 准备 SnowLuma …")
    snowluma_src = resolve_snowluma(args.snowluma_version, args.local_snowluma)
    sl_dst = target / "services" / "snowluma"
    sl_count, sl_bytes = copy_tree(snowluma_src, sl_dst, SNOWLUMA_EXCLUDE_DIRS, SNOWLUMA_EXCLUDE_FILES)
    print(f"        {sl_count} 个文件，{sl_bytes / 1048576:.1f} MB")

    # 3) qq-bridge：复制仓库源码，依赖现场安装
    print("  [3/4] 复制 qq-bridge 源码 …")
    node, pnpm, npm = find_node_and_pnpm(sl_dst)
    br_dst = target / "services" / "qq-bridge"
    br_count, br_bytes = copy_tree(BRIDGE_SRC, br_dst, BRIDGE_EXCLUDE_DIRS, BRIDGE_EXCLUDE_FILES)
    print(f"        {br_count} 个文件，{br_bytes / 1048576:.1f} MB")
    used = install_bridge_deps(br_dst, node, pnpm, npm)
    print(f"        依赖安装工具：{used}")

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
        if (target / pattern).exists():
            leaks.append(pattern)

    if leaks:
        print()
        print("!! 自检失败：以下用户数据被误打包，请检查排除规则 !!")
        for item in leaks:
            print(f"   {item}")
        sys.exit(1)

    print("  自检通过：未包含 QQ 号、令牌或聊天记录。")


if __name__ == "__main__":
    # 兜底打印完整 traceback。
    # sys.exit("...") 只给一行消息，而 CI 上那一行常常不足以定位；
    # 未预期的异常更应该带上栈 —— 反正打出来不影响正常路径。
    try:
        main()
    except SystemExit:
        raise
    except Exception:  # noqa: BLE001 - 顶层兜底，就是要抓全部
        import traceback
        traceback.print_exc()
        sys.exit(1)
