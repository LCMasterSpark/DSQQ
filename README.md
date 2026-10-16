# DSQQ —— QQ 桥接面板

一个原生 WPF 桌面面板，把 [SnowLuma](https://github.com/SnowLuma/SnowLuma) 与
[qq-bridge](https://github.com/Derpyu520/qq-bridge) 的日常运维集中到一处：
启动停止、运行状态、日志查看、白名单 / 人设 / 角色 / 配额 / 黑话管理。

**不内嵌任何网页** —— 界面完全用 WPF 重写，数据直接来自两侧的 HTTP API 与本地日志文件。

---

## 想直接用？下载便携包

到 [Releases](https://github.com/LCMasterSpark/DSQQ/releases) 下载
**`DSQQ-Portable-<版本>.zip`**，解压后双击 `首次运行.bat`。

这一个包里已经有 DSQQ、SnowLuma、qq-bridge 三样东西，并且按
`services/` 布局摆好 —— DSQQ 会自己找到它们，**不需要你填任何路径**。

| 你需要的 | 从哪来 |
| --- | --- |
| QQ 客户端 | 自己装（腾讯的软件，无法打包） |
| DSH（DeepSeek Harness） | 自己装，假设你已有 —— 它是 AI 能力的来源 |
| 其他一切 | **就在这个压缩包里** |

首次运行只有两件事必须你本人做（程序无法代劳）：

1. 在 SnowLuma 控制台**同意用户协议并扫码登录 QQ**（只需一次）
2. 回 DSQQ 勾选要启用的群

其余（OneBot 端口、管理员、白名单、WebUI 会话）都由「一键配置」自动完成，
界面上的「体检」卡片会逐条告诉你现在缺什么、下一步做什么。

> 包里 **不含** 你的 QQ 号、访问令牌与聊天记录 —— 打包脚本有自检，
> 命中任一项就直接失败退出。详见 `packaging/build-portable.py`。

### 其它两个包

| 包 | 适用 |
| --- | --- |
| `*-self-contained.zip` | 只要 DSQQ 本体（不含 SnowLuma / qq-bridge），免装 .NET 运行时 |
| `*-framework-dependent.zip` | 同上，体积小，但需先装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0) |

## 环境要求

| 组件 | 版本 | 说明 |
| --- | --- | --- |
| .NET SDK | 10.0+ | 目标框架 `net10.0-windows`；运行时需 .NET 10 桌面运行时 |
| 操作系统 | Windows 10 / 11 | 依赖 DPAPI（凭据加密）与系统托盘 |
| SnowLuma | 1.14.x | 便携包自带；自行构建时可选，未安装则相关页面显示离线 |
| qq-bridge | 0.2.x | **已并入本仓库**（`qq-bridge/`，MIT） |
| DSH | 0.2.0-rc.2 | 可选；用于端口探测与模型设置 |

## 构建与运行

```powershell
dotnet build DSQQ.slnx
dotnet test  DSQQ.slnx          # 单元测试
dotnet run   --project DSQQ
```

或用 Visual Studio 打开 `DSQQ.slnx` 直接 F5。

> 注意：项目目标是 `net10.0-windows` 且使用 WPF，**只能在 Windows 上构建**。

## 自己构建便携包

```powershell
# 用本机已装的 SnowLuma（快；推荐本地开发时用）
python packaging/build-portable.py --local-snowluma

# 从 SnowLuma 的 GitHub Release 下载（CI 走这条）
python packaging/build-portable.py
```

产出 `packaging/dist/DSQQ-Portable-<版本>.zip`。

> **为什么本地要用 `--local-snowluma`**：实测到 GitHub Release 的 HEAD 请求 2 秒就返回，
> 但大文件下载被严重限速 —— 36 MB 的包 15 分钟只下到 3 MB。
> 下载路径留给 CI：在 GitHub 自己的基础设施上取自己的 Release 没有这个问题。

## 拉下来怎么用

### 只想用（不编译）

到 [Releases](https://github.com/LCMasterSpark/DSQQ/releases) 下载两个包之一：

| 包 | 适用 |
| --- | --- |
| `*-self-contained.zip` | **推荐**。解压后直接双击 `DSQQ.exe`，不需要装任何运行时 |
| `*-framework-dependent.zip` | 体积小，但需要先装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0) |

### 从源码跑

```powershell
git clone https://github.com/LCMasterSpark/DSQQ.git
cd DSQQ
dotnet build DSQQ.slnx     # 首次会还原 NuGet 包
dotnet run --project DSQQ
```

### 它不包含什么 —— 这一条最重要

**DSQQ 本身不包含 SnowLuma 与 qq-bridge 的任何二进制**，它只是这两者的控制面板（使用方）。
所以：

- **没有任何外部服务时，DSQQ 照样能打开** —— 界面会出现，服务状态显示离线，可以编辑配置、看日志。
  它不会因为"依赖缺失"而启动失败。
- 要真正收发 QQ 消息，你需要自己准备那两者，**它们不在本仓库里，请从各自的项目获取**。

### 完整部署顺序（从零开始）

1. **QQ 小号** —— 登录一个**不常用**的账号。SnowLuma 是第三方 QQ 客户端，接入非官方协议
   **存在账号风险**，别用主号。
2. **SnowLuma** —— 从它自己的发布页获取，解压到任意目录（例如 `D:\SnowLuma`），
   用自带的 `launcher.bat` 或 `node.exe index.mjs` 启动。
   > 官方 `launcher.bat` 里写的是裸 `node` 命令，依赖 PATH 上有 node。
   > 若机器上没装全局 Node，它会静默失败 —— 这就是为什么 DSQQ 的一键启动**直接用安装目录里的
   > `node.exe` 跑 `index.mjs`**，绕开 PATH 依赖。
3. **首次在 SnowLuma 界面里完成**（按它的 EULA §5.4，这些必须你本人操作，DSQQ 不代做）：
   - 阅读并同意用户协议
   - 完成 QQ 扫码登录
   - 开启 OneBot：**HTTP 3000** 与 **WebSocket 3001**
4. **qq-bridge** —— 从它自己的项目获取，放到任意目录（例如 `D:\qq-bridge`），
   按其 README 安装依赖。它的控制台默认监听 **3100**。
5. **DSQQ 里配置路径** —— 打开「设置」页，填 qq-bridge 与 SnowLuma 的实际安装目录、以及各自端口，
   保存后生效。配置存在 `%APPDATA%\DSQQ\config.json`。
6. **一键启动** —— 到「服务管理」页点「一键启动」，它会按顺序拉起 SnowLuma 与 qq-bridge 并等端口就绪。

### 端口一览（默认值）

| 端口 | 用途 |
| --- | --- |
| 5099 | SnowLuma WebUI |
| 3000 | OneBot HTTP |
| 3001 | OneBot WebSocket |
| 3100 | qq-bridge 控制台 |
| 19387 | DSH Web（可选，用于模型与推理强度设置） |

> **OneBot 的 3000 / 3001 由 SnowLuma 在「账号登录后」才启动** —— 没监听通常意味着还没登录，
> 而不是服务坏了。此时「服务管理」页的「打开 SnowLuma 控制台」可以直接把你送到登录页。

### 遇到问题先看这里

| 现象 | 多半是 |
| --- | --- |
| 收不到群消息、群列表为空 | SnowLuma 未登录（OneBot 端口没起来） |
| 界面显示"服务未启动"但桥接在跑 | 端口配错，或桥接还没就绪（界面会自动重试几次） |
| 一键启动对 SnowLuma 无效 | 官方 launcher 依赖 PATH 里的 node（见上面第 2 步） |

## 功能

**运行总览** —— SnowLuma / qq-bridge / DSH 三服务的连通性、桥接模式、账号、白名单规模与最近活动。
其中 SnowLuma 的 `connected`（WebSocket 通）与 `good`（真的收得到 QQ 数据）分开呈现：
后者为假通常意味着上游原生注入退化，此时界面上会显示为「异常」而不是「在线」。

**服务管理** —— 一键启动（校验 QQ → 启动 SnowLuma 并等端口 → 启动桥接并等端口 → 探活）、
分级停止（桥接 / SnowLuma / 全部）、端口与 PID 一览。

> 停止服务时不按进程名批量结束进程：SnowLuma 与 qq-bridge 都使用 `node.exe`，
> 按名字杀会互相误杀。一律从端口反查 PID，只终止那一棵进程树，并清理 `bridge.lock`。

**桥接控制台** —— 六个分页：

| 分页 | 内容 |
| --- | --- |
| 总览 | 挂起审批 / 提问、运行模式与静默开关、会话与上下文（共享会话 / 跨群读取）、模型与推理强度、会话列表（可重置上下文） |
| 白名单 | 群勾选 + 私聊号码 |
| 她是谁 | 三层合一：① 人设（语气，五项字段）② 角色卡片（身份，列表含字数 / 注入量 / 超限与告警标记，可启用·新建·保存·改名·删除）③ 仿真提示词（协议，按 preset 载入与保存）。顶部含一致性检查与上下文占用预算 |
| 配额 | 预警点与上限、豁免名单、上限提示语、预警配图、今日用量 |
| 活跃度 | 唤醒频率与发送节奏共 17 项，含高频 / 标准 / 低频三档预设；时间以「秒」为单位 |
| 令牌与花费 | token 汇总与折算金额、消耗走势（按半小时分桶）、模型与档位切换 |
| 黑话 | 按状态筛选、批量确认 / 拒绝 / 删除、触发提取 |

> **「她是谁」为什么合成一页**：三层会同时进入同一条上下文，是叠加关系而非三选一 ——
> 仿真提示词是底子（系统提示词，规定二代仿真怎么运作）、角色卡紧接着整段注入（实测约 5000 字，
> 每次唤醒都带上）、人设注入在用户消息最前面用于临时微调。
> 三者矛盾时**没有任何自动仲裁**，由模型自行权衡，所以界面顶部会主动检查两类已知冲突：
> 人设的「叫什么」与角色卡里的身份表述重复；角色卡残留一代指令（`[SILENT]`、空格分句等）
> 在二代仿真下与工具协议冲突（最坏是模型照抄 `[SILENT]` 而不调发送工具 —— 看起来回话了，实际没发出去）。

**SnowLuma** —— 登录态（凭据用 DPAPI 加密保存）、QQ 进程与注入状态、OneBot 端点（token 掩码显示）。

**日志** —— 三个来源跟随读取（增量、带去抖与截断保护）、级别与关键字过滤（支持 `/正则`）、
诊断包导出、命中告警关键字时托盘提示。

**设置** —— 路径、端口、启动行为、监控参数，字段级校验，保存即时生效。

## 配置

`%APPDATA%\DSQQ\config.json`，首次保存设置时生成。包含路径、端口与加密后的凭据
（`dpapi:` 前缀，只能由当前 Windows 用户解密，换机器后需重新输入）。

## 发布

打 tag 触发 GitHub Actions 自动构建并创建 Release：

```powershell
git tag v0.1.0
git push origin v0.1.0
```

会产出两个包：

| 包 | 说明 |
| --- | --- |
| `*-framework-dependent.zip` | 体积小，目标机需安装 .NET 10 桌面运行时 |
| `*-self-contained.zip` | 体积较大，免安装运行时，解压即用 |

也可以在 Actions 页面手动触发 `release` 工作流并填入版本号（此时只上传构建产物，不创建 Release）。

## 合规说明

本程序是 SnowLuma 与 qq-bridge 的**使用方**：

- 不包含、不捆绑两者的任何二进制；
- 只负责启动 / 停止进程，**不会**代为接受协议或处理扫码登录
  （SnowLuma EULA §5.4 禁止通过自动化脚本部署其专有组件）；
- SnowLuma 是第三方 QQ 客户端，接入非官方协议**存在账号风险**，请勿使用主号。

## 项目结构

```
DSQQ.slnx
├─ Directory.Build.props        共享构建属性（版本、语言级别、分析器）
├─ .github/workflows/           build（构建+测试）与 release（tag 发布）
├─ DSQQ/                        主程序
│  ├─ Models/                   配置与两个 API 的数据模型
│  ├─ Services/                 端口探测与 PID 反查、进程启动、编排、
│  │                            日志跟随、Bridge/SnowLuma API 客户端、DPAPI 加密存储
│  ├─ Views/                    运行总览 / 服务管理 / 桥接控制台 / SnowLuma / 日志 / 设置
│  ├─ Resources/Brushes.xaml    语义色板
│  ├─ AppServices.cs            组合根
│  ├─ TaskObserver.cs           统一的 fire-and-forget 异常观察
│  └─ TrayIconHost.cs           托盘
└─ tests/DSQQ.Tests/            单元测试
```

## 第三方

- [MahApps.Metro](https://github.com/MahApps/MahApps.Metro)（MIT）—— Metro 风格控件与主题
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)（MIT）

## 许可证

MIT
