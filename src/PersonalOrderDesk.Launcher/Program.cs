using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using PersonalOrderDesk.Storage;

internal static class Program
{
    private const string AppId = "PersonalOrderDesk";
    private const string Url = "http://127.0.0.1:5059";
    private const string MutexName = "Local\\PersonalOrderDesk-v1";
    private static bool TestMode => Environment.GetEnvironmentVariable("PERSONAL_ORDER_DESK_TEST_MODE") == "1";

    [STAThread]
    private static async Task<int> Main()
    {
        using var mutex = new Mutex(true, MutexName, out var created);
        if (!created)
        {
            if (await IsOurServiceReadyAsync()) { OpenBrowser(); return 0; }
            ShowError("工作台启动器检测到已有实例，但服务尚未就绪。请稍后重试或检查残留进程。");
            return 2;
        }

        if (await IsOurServiceReadyAsync()) { OpenBrowser(); return 0; }
        if (await IsPortOccupiedAsync())
        {
            ShowError("本机端口 5059 已被其他程序占用。工作台未连接该程序，也不会自动更换端口。");
            return 3;
        }

        var appPath = Path.Combine(AppContext.BaseDirectory, "PersonalOrderDesk.exe");
        if (!File.Exists(appPath)) { ShowError("找不到工作台程序 PersonalOrderDesk.exe。请重新解压完整候选包。"); return 4; }
        var configuredDataDirectory = Environment.GetEnvironmentVariable("PERSONAL_ORDER_DESK_DATA_DIR");
        string dataDirectory;
        try { dataDirectory = StorageLocationGuard.ResolveDataDirectory(configuredDataDirectory, AppContext.BaseDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppId)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { ShowError(ex.Message); return 7; }
        var registrationPath = StorageLocationGuard.RegistrationPath(AppContext.BaseDirectory, dataDirectory, configuredDataDirectory is not null);
        try { StorageLocationGuard.Validate(dataDirectory, registrationPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { ShowError(ex.Message); return 7; }
        Directory.CreateDirectory(Path.Combine(dataDirectory, "Logs"));
        var info = new ProcessStartInfo(appPath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
        info.Environment["PERSONAL_ORDER_DESK_DATA_DIR"] = dataDirectory;
        info.Environment["PERSONAL_ORDER_DESK_DEFAULT_STORAGE"] = configuredDataDirectory is null ? "1" : "0";
        info.Environment["ASPNETCORE_URLS"] = Url;
        using var child = Process.Start(info);
        if (child is null) { ShowError("无法启动工作台服务。"); return 5; }
        await LogAsync(dataDirectory, new { action = "started", pid = child.Id, command = appPath, at = DateTimeOffset.UtcNow });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (!timeout.IsCancellationRequested && !child.HasExited)
        {
            if (await IsOurServiceReadyAsync())
            {
                OpenBrowser();
                await child.WaitForExitAsync();
                await LogAsync(dataDirectory, new { action = "exited", pid = child.Id, exitCode = child.ExitCode, at = DateTimeOffset.UtcNow });
                return child.ExitCode;
            }
            await Task.Delay(350, timeout.Token).ContinueWith(_ => { }, TaskScheduler.Default);
        }
        if (!child.HasExited)
        {
            try { child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await child.WaitForExitAsync();
        }
        ShowError("工作台服务未能在 90 秒内完成启动。请查看用户数据目录中的 Logs 文件。");
        return 6;
    }

    private static async Task<bool> IsOurServiceReadyAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
            var response = await client.GetAsync(Url + "/healthz");
            if (!response.IsSuccessStatusCode) return false;
            var ready = await response.Content.ReadFromJsonAsync<ReadyResponse>();
            return ready is { ApplicationId: AppId, Ready: true };
        }
        catch { return false; }
    }

    private static async Task<bool> IsPortOccupiedAsync()
    {
        try { using var client = new TcpClient(); await client.ConnectAsync("127.0.0.1", 5059); return true; }
        catch (SocketException) { return false; }
        catch { return true; }
    }

    private static void OpenBrowser()
    {
        if (TestMode) return;
        try { Process.Start(new ProcessStartInfo(Url + "/Overview") { UseShellExecute = true }); }
        catch { ShowError("工作台已就绪，但浏览器无法自动打开。请访问 http://127.0.0.1:5059"); }
    }

    private static async Task LogAsync(string dataDirectory, object item)
    {
        try { await File.AppendAllTextAsync(Path.Combine(dataDirectory, "Logs", "launcher.jsonl"), JsonSerializer.Serialize(item) + Environment.NewLine); }
        catch { }
    }

    private static void ShowError(string text)
    {
        if (!TestMode) MessageBoxW(IntPtr.Zero, text, "个人接单工作台", 0x10 | 0x0);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);
    private sealed record ReadyResponse(string ApplicationId, bool Ready);
}
