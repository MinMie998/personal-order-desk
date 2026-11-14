using System.Windows;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PersonalOrderDesk.Client;
using PersonalOrderDesk.SharedUI;
namespace PersonalOrderDesk.Desktop;
public partial class MainWindow : Window
{
    private readonly ServiceProvider services;
    public MainWindow()
    {
        InitializeComponent();
        var root=Environment.GetEnvironmentVariable("PERSONAL_ORDER_DESK_CLIENT_ROOT")??System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PersonalOrderDesk.Client");
        WebView.BlazorWebViewInitialized+=(_,e)=>{
            var log=System.IO.Path.Combine(root,"logs","webview-start.log");System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(log)!);
            e.WebView.CoreWebView2.NavigationCompleted+=(_,n)=>System.IO.File.AppendAllText(log,$"navigation success={n.IsSuccess} error={n.WebErrorStatus}\n");
            e.WebView.CoreWebView2.WebResourceResponseReceived+=(_,r)=>{if(r.Response.StatusCode>=400)System.IO.File.AppendAllText(log,$"resource {r.Response.StatusCode} {r.Request.Uri}\n");};
        };
        var collection=new ServiceCollection();collection.AddLogging(x=>x.AddProvider(new DesktopLog(System.IO.Path.Combine(root,"logs","desktop.log"))));collection.AddWpfBlazorWebView();
        collection.AddSingleton<IWorkspaceSession>(new NativeWorkspaceSession(root,new WindowsSecretStore(root),false,()=>BackendSupervisor.Connect(root)));
        collection.AddSingleton<IClientPlatform,WindowsClientPlatform>();services=collection.BuildServiceProvider();
        WebView.Services=services;WebView.RootComponents.Add(new RootComponent {Selector="#app",ComponentType=typeof(WorkbenchShell)});
        Closed+=async(_,_)=>{await WebView.DisposeAsync();await services.DisposeAsync();};
    }
}
