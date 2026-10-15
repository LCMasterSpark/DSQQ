using System.IO;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Process = System.Diagnostics.Process;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using DSQQ.Models;
using DSQQ.Services;
using UserControl = System.Windows.Controls.UserControl;
using PasswordBox = System.Windows.Controls.PasswordBox;
using TextBox = System.Windows.Controls.TextBox;
using TextBlock = System.Windows.Controls.TextBlock;
using DataGrid = System.Windows.Controls.DataGrid;
using Button = System.Windows.Controls.Button;
using Timer = System.Timers.Timer;

namespace DSQQ.Views;

/// <summary>
/// SnowLuma 页：登录态、OneBot 配置、QQ 进程与注入状态。
/// </summary>
/// <remarks>
/// 登录凭据用 DPAPI 加密后写入配置文件；会话 Cookie 同样持久化，实现免重复登录。
/// 协议同意与扫码登录不在此处代做 —— SnowLuma EULA 禁止自动化脚本部署其专有组件。
/// </remarks>
public partial class SnowLumaView : UserControl
{
    private readonly AppServices _services;
    private readonly ObservableCollection<SnowLumaProcess> _processes = [];
    private Timer? _timer;
    private SnowLumaApiClient _client;

    /// <summary>初始化页面。</summary>
    /// <param name="services">应用服务宿主。</param>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> 为 <c>null</c>。</exception>
    public SnowLumaView(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _client = CreateClient();

        InitializeComponent();
        ProcessGrid.ItemsSource = _processes;

        Loaded += async (_, _) =>
        {
            RestoreSavedCredentials();
            StartTimer();
            await RefreshAsync();
        };

        Unloaded += (_, _) => StopTimer();
    }

    /// <summary>按当前配置创建客户端（配置变更后需重建）。</summary>
    private SnowLumaApiClient CreateClient()
    {
        var client = new SnowLumaApiClient(_services.Endpoints.SnowLumaWebUi);
        var cookie = SecureStore.Unprotect(_services.Config.SnowLuma.SessionCookieProtected);
        if (!string.IsNullOrEmpty(cookie))
        {
            client.ImportCookies(cookie);
        }

        return client;
    }

    /// <summary>把保存的密码填回输入框，省去重复输入。</summary>
    private void RestoreSavedCredentials()
    {
        var saved = SecureStore.Unprotect(_services.Config.SnowLuma.WebUiPasswordProtected);
        if (!string.IsNullOrEmpty(saved))
        {
            PasswordBox.Password = saved;
            LoginStatusText.Text = $"已载入保存的凭据，正在尝试恢复登录（{_services.Endpoints.SnowLumaWebUi}）。";
        }
        else
        {
            LoginStatusText.Text = $"尚未保存凭据。WebUI 地址：{_services.Endpoints.SnowLumaWebUi}";
        }
    }

    /// <summary>每 5 秒刷新一次状态（进程与账号会随扫码登录变化）。</summary>
    private void StartTimer()
    {
        _timer = new Timer(5000) { AutoReset = true };
        _timer.Elapsed += async (_, _) => await Dispatcher.InvokeAsync(async () => await RefreshAsync());
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>登录。</summary>
    private async void OnLoginClick(object sender, RoutedEventArgs e)
    {
        LoginButton.IsEnabled = false;
        try
        {
            var password = PasswordBox.Password;
            var totp = TotpBox.Text.Trim();

            var result = await _client.LoginAsync(password, string.IsNullOrEmpty(totp) ? null : totp);

            switch (result.Outcome)
            {
                case LoginOutcome.Success:
                    SaveCredentials(password);
                    LoginStatusText.Text = "登录成功，会话已保存。";

                    // 登录后立刻刷新一次，避免用户等待下一个轮询周期。
                    await RefreshAsync();
                    break;

                case LoginOutcome.NeedsTotp:
                    LoginStatusText.Text = "该账号启用了两步验证：请在「验证码」框填写动态码后重试。";
                    break;

                case LoginOutcome.RateLimited:
                    LoginStatusText.Text = $"登录被限流：{result.Message}";
                    break;

                default:
                    LoginStatusText.Text = $"登录失败：{result.Message}";
                    break;
            }
        }
        finally
        {
            LoginButton.IsEnabled = true;
        }
    }

    /// <summary>退出登录并清除本地会话。</summary>
    private async void OnLogoutClick(object sender, RoutedEventArgs e)
    {
        await _client.LogoutAsync();

        var config = _services.Config;
        config.SnowLuma.SessionCookieProtected = null;
        config.SnowLuma.WebUiPasswordProtected = null;
        TrySaveConfig();

        _client = CreateClient();
        LoginStatusText.Text = "已退出登录，并清除本地保存的凭据。";
        _processes.Clear();
        OverviewText.Text = "尚未采集。";
    }

    /// <summary>保存凭据（DPAPI 加密）。</summary>
    private void SaveCredentials(string password)
    {
        var config = _services.Config;
        config.SnowLuma.WebUiPasswordProtected = SecureStore.Protect(password);

        var cookie = _client.ExportCookies();
        config.SnowLuma.SessionCookieProtected = string.IsNullOrEmpty(cookie) ? null : SecureStore.Protect(cookie);

        TrySaveConfig();
    }

    /// <summary>保存配置；失败时提示但不打断主流程。</summary>
    private void TrySaveConfig()
    {
        try
        {
            _services.ApplyConfig(_services.Config);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoginStatusText.Text = $"凭据保存失败（本次登录仍然有效）：{ex.Message}";
        }
    }

    /// <summary>手动刷新。</summary>
    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    /// <summary>采集状态、进程与 OneBot 配置。</summary>
    private async Task RefreshAsync()
    {
        var status = await _client.GetStatusAsync();
        var processes = await _client.GetProcessesAsync();

        _processes.Clear();
        foreach (var process in processes)
        {
            _processes.Add(process);
        }

        if (status is null)
        {
            OverviewText.Text = _client.HasSession
                ? $"无法读取状态：{_services.Endpoints.SnowLumaWebUi} 未响应或会话已失效。"
                : "未登录。请在下方输入 WebUI 密码后登录。";
        }
        else
        {
            var accounts = status.Accounts.Count == 0
                ? "（无已登录账号）"
                : string.Join("、", status.Accounts.Select(a => $"{a.Nickname ?? "?"}({a.Uin ?? "?"})"));

            var injected = processes.Count(p => p.Loaded);
            OverviewText.Text =
                $"地址        : {_services.Endpoints.SnowLumaWebUi}{Environment.NewLine}"
                + $"版本        : {status.Version ?? "未知"}{Environment.NewLine}"
                + $"已登录账号  : {accounts}{Environment.NewLine}"
                + $"QQ 进程     : {processes.Count} 个，其中已注入 {injected} 个{Environment.NewLine}"
                + $"{Environment.NewLine}"
                + "提示：若「已注入 0 个」但 QQ 正在运行，通常是 SnowLuma 的原生组件未能挂上 QQ 进程；"
                + "请查看 SnowLuma 日志中的 [Hook] 行。";
        }

        await RefreshOneBotAsync(processes);
    }

    /// <summary>读取并展示 OneBot 端点（端口与 token 掩码）。</summary>
    private async Task RefreshOneBotAsync(IReadOnlyList<SnowLumaProcess> processes)
    {
        var uin = processes.Select(p => p.Uin).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u))
            ?? _services.Config.Owner.Qq?.ToString();

        if (string.IsNullOrWhiteSpace(uin))
        {
            HintText.Text = "未发现已登录的 QQ 号，OneBot 配置暂不可读。";
            return;
        }

        var oneBot = await _client.GetOneBotConfigAsync(uin);
        var http = oneBot?.Networks?.HttpServers.FirstOrDefault();
        var ws = oneBot?.Networks?.WsServers.FirstOrDefault();

        HintText.Text = oneBot is null
            ? $"未能读取账号 {uin} 的 OneBot 配置。"
            : $"OneBot（{uin}）：HTTP {(http is null ? "未配置" : $"{http.Host}:{http.Port} token={Mask(http.AccessToken)}")}    "
              + $"WS {(ws is null ? "未配置" : $"{ws.Host}:{ws.Port} token={Mask(ws.AccessToken)}")}";
    }

    /// <summary>token 只显示首尾，避免明文出现在界面上。</summary>
    private static string Mask(string? token)
        => string.IsNullOrEmpty(token)
            ? "(空)"
            : token.Length <= 8 ? new string('*', token.Length) : $"{token[..4]}…{token[^4..]}";

    /// <summary>打开 SnowLuma 日志目录（排查注入问题时常用）。</summary>
    private void OnOpenLogsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = System.IO.Path.Combine(_services.Config.SnowLuma.InstallDirectory, "logs");
            if (System.IO.Directory.Exists(directory))
            {
                Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
                return;
            }

            LoginStatusText.Text = $"日志目录不存在：{directory}（SnowLuma 尚未启动过？）";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LoginStatusText.Text = $"打开日志目录失败：{ex.Message}";
        }
    }
}
