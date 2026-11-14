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
        var collection=new ServiceCollection();collection.AddLogging(x=>x.AddProvider(new DesktopLog(System.IO.Path.Combine(root,"logs","desktop.log"))));collection.AddWpfBlazorWebView();
        collection.AddSingleton<IWorkspaceSession>(new NativeWorkspaceSession(root,new WindowsSecretStore(root),false,()=>BackendSupervisor.Connect(root)));
        collection.AddSingleton<IClientPlatform,WindowsClientPlatform>();services=collection.BuildServiceProvider();
        WebView.Services=services;WebView.RootComponents.Add(new RootComponent {Selector="#app",ComponentType=typeof(WorkbenchShell)});
        Closed+=async(_,_)=>{await WebView.DisposeAsync();await services.DisposeAsync();};
    }
}

