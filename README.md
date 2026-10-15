# DSQQ —— QQ 桥接面板

一个原生 WPF 桌面面板，把 [SnowLuma](https://github.com/SnowLuma/SnowLuma) 与
[qq-bridge](https://github.com/Derpyu520/qq-bridge) 的日常运维集中到一处：
启动停止、运行状态、日志查看、白名单 / 人设 / 角色 / 配额 / 黑话管理。

**不内嵌任何网页** —— 界面完全用 WPF 重写，数据直接来自两侧的 HTTP API 与本地日志文件。

## 环境要求

| 组件 | 版本 | 说明 |
| --- | --- | --- |
| .NET SDK | 10.0+ | 目标框架 `net10.0-windows`；运行时需 .NET 10 桌面运行时 |
| 操作系统 | Windows 10 / 11 | 依赖 DPAPI（凭据加密）与系统托盘 |
| SnowLuma | 1.14.x | 可选；未安装时相关页面显示离线 |
| qq-bridge | 0.2.x | 可选；未运行时桥接控制台页不可用 |
| DSH | 0.2.0-rc.2 | 可选；用于端口探测与模型设置 |

## 构建与运行

```powershell
dotnet build DSQQ.slnx
dotnet test  DSQQ.slnx          # 单元测试
dotnet run   --project DSQQ
```

或用 Visual Studio 打开 `DSQQ.slnx` 直接 F5。

> 注意：项目目标是 `net10.0-windows` 且使用 WPF，**只能在 Windows 上构建**。

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
| 总览 | 运行模式切换、模型与推理强度、会话列表 |
| 白名单 | 群勾选 + 私聊号码 |
| 人设 | 五项人设字段编辑 |
| 角色 | 角色列表（字数 / 注入量 / 超限与告警标记）、启用·新建·保存·改名·删除 |
| 黑话 | 按状态筛选、批量确认 / 拒绝 / 删除、触发提取 |
| 配额 | 预警点与上限、豁免名单、今日用量 |
| 令牌与花费 | token 汇总与折算金额 |
| 仿真提示词 | 按 preset 载入与保存（保存后需重启 DSH） |

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
