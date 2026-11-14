using System.Diagnostics;
using System.Net.Sockets;
using System.Net.Http;
using System.Net.Http.Json;
using PersonalOrderDesk.Client;
namespace PersonalOrderDesk.Desktop;
public static class BackendSupervisor
{
    private static readonly Uri Address=new("http://127.0.0.1:5059/");
    public static async Task<IWorkspaceGateway> Connect(string clientRoot)
    {
        using var http=new HttpClient {BaseAddress=Address,Timeout=TimeSpan.FromMilliseconds(900)};
        try {var info=await http.GetFromJsonAsync<ServerInfo>("api/v1/info",DeskJson.Options);if(info?.ProductId=="PersonalOrderDesk"){var session=await HttpWorkspaceGateway.Login(Address,"");return new HttpWorkspaceGateway(Address,session.Token);}}
        catch(HttpRequestException){}catch(TaskCanceledException){}catch(System.Text.Json.JsonException){}
        try {using var socket=new TcpClient();await socket.ConnectAsync("127.0.0.1",5059);throw new InvalidOperationException("5059端口正在使用。若是旧版工作台，请先在旧版设置页退出应用，再重开新版；程序不会结束其他服务。");}catch(SocketException){}
        var server=System.IO.Path.Combine(AppContext.BaseDirectory,"server","PersonalOrderDesk.exe");
        if(!System.IO.File.Exists(server))throw new InvalidOperationException("缺少电脑服务文件，请完整解压Windows程序包，保留server目录。");
        var start=new ProcessStartInfo(server){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=System.IO.Path.GetDirectoryName(server)!,RedirectStandardOutput=true,RedirectStandardError=true};
        var logs=System.IO.Path.Combine(clientRoot,"logs");System.IO.Directory.CreateDirectory(logs);var gate=new object();
        var process=new Process{StartInfo=start,EnableRaisingEvents=true};
        void Log(string? line){if(line is null)return;try{lock(gate)System.IO.File.AppendAllText(System.IO.Path.Combine(logs,"service-start.log"),line+Environment.NewLine);}catch(System.IO.IOException){}}
        process.OutputDataReceived+=(_,e)=>Log(e.Data);process.ErrorDataReceived+=(_,e)=>Log(e.Data);
        if(!process.Start())throw new InvalidOperationException("电脑服务无法启动。");process.BeginOutputReadLine();process.BeginErrorReadLine();
        for(var attempt=0;attempt<120;attempt++){
            if(process.HasExited)throw new InvalidOperationException("电脑服务启动失败，请查看客户端logs中的service-start.log；现有数据库没有被新建空库替换。");
            try{var info=await http.GetFromJsonAsync<ServerInfo>("api/v1/info",DeskJson.Options);if(info?.ProductId=="PersonalOrderDesk"){var session=await HttpWorkspaceGateway.Login(Address,"");return new HttpWorkspaceGateway(Address,session.Token);}}catch(HttpRequestException){}catch(TaskCanceledException){}
            await Task.Delay(400);
        }
        throw new InvalidOperationException("电脑服务启动较慢，请稍后重新打开本机工作台。");
    }
}
