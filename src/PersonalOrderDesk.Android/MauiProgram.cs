using PersonalOrderDesk.Client;
namespace PersonalOrderDesk.Android;
public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder=MauiApp.CreateBuilder().UseMauiApp<App>();
        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddSingleton<IWorkspaceSession>(new NativeWorkspaceSession(Path.Combine(FileSystem.AppDataDirectory,"workbench"),new AndroidSecretStore(),true));
        builder.Services.AddSingleton<IClientPlatform,AndroidClientPlatform>();
        return builder.Build();
    }
}
