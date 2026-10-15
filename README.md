# DSQQ —— QQ 桥接面板

一个原生 WPF 桌面面板，把 [SnowLuma](https://github.com/SnowLuma/SnowLuma) 与
[qq-bridge](https://github.com/Derpyu520/qq-bridge) 的日常运维集中到一处：
启动停止、运行状态、日志查看、白名单/人设/配额管理。

**不内嵌任何网页** —— 界面完全用 WPF 重写，数据直接来自两侧的 HTTP API 与本地日志文件。

## 环境要求

| 组件 | 版本 | 说明 |
| --- | --- | --- |
| .NET SDK | 10.0+ | 目标框架 `net10.0-windows` |
| 操作系统 | Windows 10 / 11 | 依赖 DPAPI 与托盘 |
| SnowLuma | 1.14.x | 可选；不装则相关页面显示离线 |
| qq-bridge | 0.2.x | 可选；不装则桥接控制台页不可用 |
| DSH | 0.2.0-rc.2 | 可选；用于端口探测与模型设置 |

## 构建与运行

```powershell
dotnet build DSQQ.slnx
dotnet test  DSQQ.slnx          # 44 项单元测试
dotnet run   --project DSQQ
```

或用 Visual Studio 打开 `DSQQ.slnx` 直接 F5。

## 功能

**运行总览** — SnowLuma / qq-bridge / DSH 三服务的连通性，桥接模式、账号、白名单规模。
其中 SnowLuma 的 `connected`（WebSocket 通）与 `good`（真的收得到 QQ 数据）分开呈现，
后者为假通常意味着上游原生注入退化。

**服务管理** — 一键启动（校验 QQ → 启动 SnowLuma 并等端口 → 启动桥接并等端口 → 探活）、
分级停止、端口与 PID 一览。

> 停止服务时不按进程名批量结束进程：SnowLuma 与 qq-bridge 都使用 `node.exe`，
> 按名字杀会互相误杀。一律从端口反查 PID，只终止那一棵进程树，并清理 `bridge.lock`。

**桥接控制台** — 模式切换、白名单（群勾选 + 私聊号码）、人设五字段、每日配额与豁免、
模型与推理强度、令牌与花费、会话列表。

**SnowLuma** — 登录态（凭据用 DPAPI 加密保存）、QQ 进程与注入状态、OneBot 端点（token 掩码显示）。

**日志** — 三个来源跟随读取（增量、带去抖与截断保护）、级别与关键字过滤（支持 `/正则`）、
诊断包导出。

**设置** — 路径、端口、启动行为、监控参数，字段级校验，保存即时生效。

## 配置

`%APPDATA%\DSQQ\config.json`，首次保存设置时生成。包含路径、端口与加密后的凭据
（`dpapi:` 前缀，只能由当前 Windows 用户解密）。

## 合规说明

本程序是 SnowLuma 与 qq-bridge 的**使用方**：

- 不包含、不捆绑两者的任何二进制；
- 只负责启动/停止进程，**不会**代为接受协议或处理扫码登录
  （SnowLuma EULA §5.4 禁止通过自动化脚本部署其专有组件）；
- SnowLuma 是第三方 QQ 客户端，接入非官方协议**存在账号风险**，请勿使用主号。

## 项目结构

```
DSQQ.slnx
├─ Directory.Build.props        共享构建属性（版本、语言级别、分析器）
├─ DSQQ/                        主程序
│  ├─ Models/                   配置与 API 数据模型
│  ├─ Services/                 端口、进程、编排、日志跟随、两个 API 客户端、加密存储
│  ├─ Views/                    五个功能页
│  ├─ Resources/Brushes.xaml    语义色板
│  ├─ AppServices.cs            组合根
│  └─ TrayIconHost.cs           托盘
└─ tests/DSQQ.Tests/            单元测试
```

## 第三方

- [MahApps.Metro](https://github.com/MahApps/MahApps.Metro)（MIT）—— Metro 风格控件与主题
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)（MIT）

## 许可证

MIT
