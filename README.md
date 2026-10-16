# DSQQ —— QQ 桥接面板

把 [SnowLuma](https://github.com/SnowLuma/SnowLuma) 与 [qq-bridge](https://github.com/Derpyu520/qq-bridge)
的部署与日常运维收进**一个原生 WPF 桌面程序**：启动停止、状态体检、日志、
以及两边控制台的全部功能。

**不内嵌任何网页。** 界面完全用 WPF 重写，数据直接来自两侧的 HTTP API 与本地日志 ——
所以它不会有"内嵌浏览器"那种转发层坏掉就整页白屏的问题。

---

## 快速开始

### 只想用：下载便携包

到 [Releases](https://github.com/LCMasterSpark/DSQQ/releases) 下载
**`DSQQ-Portable-<版本>.zip`**，解压，双击 `首次运行.bat`。

这一个包里已经有 **DSQQ + SnowLuma + qq-bridge**，按 `services/` 布局摆好，
DSQQ 会自己找到它们 —— **不需要填任何路径**。

| 你需要准备的 | 从哪来 |
| --- | --- |
| QQ 客户端 | 自己装（腾讯的软件，无法打包） |
| DSH（DeepSeek Harness） | 自己装 —— 它是 AI 能力的来源 |
| .NET 运行时 | **不用装**，便携包是自包含的 |
| 其他一切 | **就在这个压缩包里** |

属于本程序这一侧的首次配置只有一件事：**勾选要启用的群**。
OneBot 端口、WebUI 会话、白名单、密码都由「一键配置」自动完成，
「运行总览」的体检卡片会逐条告诉你现在缺什么、下一步做什么。

> **QQ 侧的登录与协议同意不在本程序范畴内。** 那是 SnowLuma 与 QQ 客户端自己的流程，
> 按它们的正常流程走即可 —— DSQQ 只探测结果，不做引导也不代劳。

> 包里**不含**你的 QQ 号、访问令牌与聊天记录。打包脚本有自检，命中任一项就直接失败退出，
> 详见 [`packaging/build-portable.py`](packaging/build-portable.py)。

### 从源码跑

```powershell
git clone https://github.com/LCMasterSpark/DSQQ.git
cd DSQQ
dotnet build DSQQ.slnx     # 首次会还原 NuGet 包
dotnet run --project DSQQ
```

**没有任何外部服务时它照样能编译运行** —— 界面会出现，服务状态显示离线，
可以编辑配置、翻日志。它不会因为"依赖缺失"而启动失败。

### 自己构建便携包

```powershell
# 用本机已装的 SnowLuma（快；推荐本地开发时用）
python packaging/build-portable.py --local-snowluma

# 从 SnowLuma 的 GitHub Release 下载（CI 走这条）
python packaging/build-portable.py
```

产出 `packaging/dist/DSQQ-Portable-<版本>.zip`。

> **本地为什么一定要加 `--local-snowluma`**：实测到 GitHub Release 发 HEAD 请求 2 秒就返回，
> 但大文件下载被严重限速 —— 36 MB 的包 15 分钟只下到 3 MB。
> 下载路径留给 CI：在 GitHub 自己的基础设施上取自己的 Release 没有这个问题。

---

## 界面里有什么

六个顶层页面。括号里是各自的内部分页。

### 运行总览

三个服务的状态卡，加一张**体检卡片**：逐条检查 DSH 数据目录与 API 端口、SnowLuma 目录与
WebUI 端口、OneBot 端口、qq-bridge 目录与控制台端口、管理员 QQ、白名单。
每一条都带「下一步做什么」。六项全过才显示可以说话。

### 服务管理

分别启动 / 停止 / 重启 SnowLuma 与 qq-bridge，一键启动全部。

**只按端口反查 PID，绝不按进程名杀进程** —— SnowLuma 与 qq-bridge 都是 `node.exe`，
按名字杀会误伤你自己跑的其它 Node 程序。停止 qq-bridge 用
`Process.Kill(entireProcessTree: true)`，连带它拉起的子进程一起收掉。

### 桥接控制台（10 个分页）

| 分页 | 能做什么 |
| --- | --- |
| **总览** | 挂起审批/提问列表（只读展示）、运行模式、静默开关、会话与上下文、模型与推理强度、会话映射与重置 |
| **白名单** | 允许的群与私聊、管理员 QQ |
| **她是谁** | ① 人设（语气层）② 角色卡片（身份层）③ 仿真提示词（协议层）—— 三层合并一页，带**一致性检查**与**上下文占用预算** |
| **配额** | 每日 token 配额与图片配额，含预计可用轮数 |
| **活跃度** | 主动发言的活跃度档位与预设 |
| **令牌与花费** | 24 小时消耗走势（半小时一桶）、输入命中/未命中、输出、折算金额、最近活动 |
| **黑话** | 候选词条的确认 / 拒绝 / 删除 / 提取 |
| **二代仿真** | 四个运行开关、会话状态、唤醒配置编辑器、记忆、工具调用日志、AI 反馈 |
| **表情库** | 收藏表情的搜索、强制同步、本地笔记 / 标签 / 用法 |
| **语音库** | 语音文件列表与试发（带宽限、先检查/真发两步） |

### SnowLuma（7 个分页）

| 分页 | 能做什么 |
| --- | --- |
| **概览** | 运行状态、已登录账号、QQ 进程、接入点状态 |
| **QQ 账号** | 进程列表与加载 / 卸载 / 重新探测 |
| **OneBot 配置** | HTTP 与 WebSocket 服务器的读写、一键补全 3000/3001 |
| **日志** | 实时日志、级别切换（排查完记得调回 `info`） |
| **系统** | 运行环境、存储占用与保留策略、更新检查 |
| **安全与通知** | 改密码（带强度检查）、两步验证、通知渠道与最近通知 |
| **调试面板** | **198 个 OneBot 动作**，按分类与关键词筛选，按类型生成参数控件，只读 / 会改数据分色，改数据前二次确认 |

### 日志 / 设置

日志页跟随 qq-bridge 的日志文件。设置页管两个项目的目录与端口、启动行为、
开机自启、托盘、WebUI 密码注入。

> **目录可以留空。** 启动时会自动到常见位置找（程序目录下的 `services/`、各盘根目录、
> 用户目录、桌面、下载），找到就记下来。已填好的有效配置不会被覆盖。

---

## 环境要求

| 组件 | 版本 | 说明 |
| --- | --- | --- |
| .NET SDK | 10.0+ | 目标框架 `net10.0-windows` |
| 操作系统 | Windows 10 / 11 | 依赖 DPAPI（凭据加密）与系统托盘 |
| SnowLuma | 1.14.x | 便携包自带；未安装则相关页面显示离线 |
| qq-bridge | — | **已并入本仓库**（`qq-bridge/`，MIT） |
| DSH | 0.2.0-rc.2 | 可选；用于端口探测与模型设置 |

> 目标是 `net10.0-windows` 且使用 WPF，**只能在 Windows 上构建**。

## 构建与测试

```powershell
dotnet build DSQQ.slnx
dotnet test  DSQQ.slnx          # 341 个测试
dotnet run   --project DSQQ
```

或用 Visual Studio 打开 `DSQQ.slnx` 直接 F5。

发布单文件：

```powershell
dotnet publish DSQQ\DSQQ.csproj -c Release -r win-x64 --self-contained true `
  -o "$env:USERPROFILE\Desktop\DSQQ" `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

> **构建前先关掉正在运行的 DSQQ**。否则会报 `MSB3027 / MSB3021 文件被"DSQQ (PID)"锁定`。

---

## 手动部署顺序（不用便携包时）

> 便携包已经把这些都做好了。这一节只对"从源码构建、且要自己摆组件"的人有意义。

1. **QQ 小号** —— 登录一个**不常用**的账号。SnowLuma 是第三方 QQ 客户端，接入非官方协议
   **存在账号风险**，别用主号。
2. **SnowLuma** —— 从它的[发布页](https://github.com/SnowLuma/SnowLuma/releases)下载
   `SnowLuma-v<版本>-win-x64.zip`（完整版，自带 Node.js），解压到任意目录。
   > 它自带的 `launcher.bat` 写的是裸 `node` 命令，依赖 PATH 上有 node。
   > 机器上没装全局 Node 时它会静默失败 —— 这就是为什么 DSQQ 的一键启动**直接用安装目录里的
   > `node.exe` 跑 `index.mjs`**，绕开 PATH 依赖。
3. **让 SnowLuma 侧就绪**（属于它的范畴）：在它的界面里完成它要求的步骤，
   打开 OneBot 的 HTTP 3000 与 WebSocket 3001。
4. **qq-bridge** —— 本仓库自带的 `qq-bridge/` 就是完整源码，装依赖即可：
   ```powershell
   cd qq-bridge
   pnpm install --config.node-linker=hoisted --prod
   node scripts/patch-snowluma-sdk.mjs     # 必须跑，见下
   ```
   > **`patch-snowluma-sdk.mjs` 不能省。** 上游 `@snowluma/*` 包的 dist 里有几处相对导入
   > 缺 `.js` 扩展名，纯 Node ESM 解析不了，跳过这一步桥接起不来。
5. **DSQQ** —— 打开「设置」页填两个目录，或直接留空让它自己找。然后「一键启动」。

## 这个仓库里有什么

| 组件 | 在本仓库里吗 | 说明 |
| --- | --- | --- |
| **DSQQ** | ✅ 源码 | 本仓库的主体，WPF 控制台 |
| **qq-bridge** | ✅ 源码（`qq-bridge/`） | MIT，已并入；来源与同步方式见 `qq-bridge/VENDORED.md` |
| **SnowLuma** | ❌ 源码不在 | 由**便携包构建时**从它自己的公开 Release 下载 |
| QQ 客户端 | ❌ | 腾讯的软件，无法打包 |
| DSH | ❌ | AI 能力的来源，假设你已有 |

---

## 关于第三方组件

| 组件 | 许可 | 与本项目的关系 |
| --- | --- | --- |
| [SnowLuma](https://github.com/SnowLuma/SnowLuma) | 见其仓库 | 独立的 OneBot 实现端。本仓库不含其源码；便携包从它自己的 Release 下载 |
| [qq-bridge](https://github.com/Derpyu520/qq-bridge) | MIT | 已并入本仓库（含 `LICENSE`）。上游改动需手动同步 |
| [MahApps.Metro](https://github.com/MahApps/MahApps.Metro) | MIT | Metro 风格控件与主题 |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MIT | MVVM 基础设施 |

**风险提示**：SnowLuma 通过非官方协议接入 QQ，**账号存在被封禁的可能**。
请使用不常用的小号，并自行评估风险。

---

## 项目结构

```
DSQQ.slnx                        解决方案
Directory.Build.props            共享构建属性（版本、语言级别、分析器）
.github/workflows/               build（构建 + 测试）与 release（打 tag 发布）
qq-bridge/                       AI 适配（MIT，已并入；node_modules 不入库，打包时现装）
packaging/
├─ build-portable.py             便携包构建（含"绝不打包用户数据"自检）
├─ merge-qq-bridge.py            把上游 qq-bridge 并入本仓库
└─ 使用说明.txt                   放进便携包的说明
DSQQ/
├─ Models/                       配置、两个 API 的数据模型、纯逻辑（审计 / 解码 / 解析）
├─ Services/                     端口探测与 PID 反查、进程启动、编排、日志跟随、
│                                Bridge / SnowLuma API 客户端、DPAPI 加密存储、服务定位
├─ Views/                        桥接控制台 / SnowLuma 控制台 / 服务管理 / 日志 / 设置
├─ Resources/Brushes.xaml        语义色板
├─ AppServices.cs                组合根
├─ TaskObserver.cs               统一的 fire-and-forget 异常观察
└─ TrayIconHost.cs               托盘
tests/DSQQ.Tests/                单元测试（含打真实 HTTP 的桩）
```

---

## 排查

| 症状 | 先看这里 |
| --- | --- |
| 界面全离线 | 「运行总览」的体检卡片，它会指出缺哪一步 |
| 一键启动 SnowLuma 失败 | 设置页的「SnowLuma 安装目录」是否指向解压后的根目录（里面应有 `node.exe` 与 `index.mjs`） |
| 收不到群消息 | SnowLuma 是否已登录、OneBot 3000/3001 是否在监听（体检卡片会报） |
| 某个开关点了没反应 | 桥接控制台的「日志」页；桥接把原因写在响应里，而不是用状态码表达 |
| SnowLuma 页面全部 401 | 「安全与通知」里重新登录一次；会话令牌存在 DPAPI 里，换 Windows 用户会失效 |

---

## 许可证

MIT，见 [`LICENSE.txt`](LICENSE.txt)。
