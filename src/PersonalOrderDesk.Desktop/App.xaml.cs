using System.Windows;
namespace PersonalOrderDesk.Desktop;
public partial class App : Application
{
    private Mutex? mutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        mutex=new Mutex(true,"Local\\PersonalOrderDesk.NativeWindows-v2",out var created);
        if(!created){MessageBox.Show("工作台窗口已经打开，请从任务栏切回。","个人接单工作台");Shutdown();return;}
        var main=new MainWindow();MainWindow=main;main.Show();
    }
    protected override void OnExit(ExitEventArgs e){mutex?.Dispose();base.OnExit(e);}
}
