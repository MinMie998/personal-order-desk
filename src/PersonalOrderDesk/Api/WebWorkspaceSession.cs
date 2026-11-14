using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using PersonalOrderDesk.Client;
namespace PersonalOrderDesk.Api;
public sealed class WebWorkspaceSession : IWorkspaceSession,IDisposable
{
    private HttpWorkspaceGateway? gateway;
    public IWorkspaceGateway Gateway=>gateway??throw new InvalidOperationException("页面尚未连接电脑服务。");
    public ClientProfile Profile {get;}=new();public bool IsMobile=>false;public bool CanSwitch=>false;public bool ReadOnly=>false;public string SourceLabel=>"网页 · 电脑账本";
    public async Task Initialize(){if(gateway is null){var session=await HttpWorkspaceGateway.Login(new Uri("http://127.0.0.1:5059/"),"");gateway=new(new Uri("http://127.0.0.1:5059/"),session.Token);}}
    public Task UseLocal()=>Initialize();
    public Task Connect(string a,string p)=>Task.FromException(new NotSupportedException());
    public Task SyncPc()=>Task.FromException(new NotSupportedException());
    public Task<SyncReview?> PreviewSync()=>Task.FromException<SyncReview?>(new NotSupportedException());
    public Task CommitSync(Dictionary<string,string> choices)=>Task.FromException(new NotSupportedException());
    public Task<string> ExportSyncProtection()=>Task.FromException<string>(new NotSupportedException());
    public Task UsePcSnapshot()=>Task.FromException(new NotSupportedException());
    public Task<string> Backup()=>Task.FromException<string>(new NotSupportedException());
    public Task RememberMethod(string? method){Profile.PaymentMethod=method;return Task.CompletedTask;}
    public void Dispose()=>gateway?.Dispose();
}
public sealed class WebClientPlatform(IJSRuntime js,NavigationManager navigation) : IClientPlatform
{
    public Task OpenUpdate(string path)=>Task.FromException(new NotSupportedException("请在Windows程序中下载更新。"));
    public async Task CopyText(string text)=>await js.InvokeVoidAsync("desk.copy",text);
    public Task OpenWeb(string path){navigation.NavigateTo("/"+path.TrimStart('/'),true);return Task.CompletedTask;}
    public Task ShareFile(string path)=>Task.FromException(new NotSupportedException());
}
