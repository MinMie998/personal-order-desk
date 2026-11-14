using System.Reflection;
namespace PersonalOrderDesk.Client;
public static class BackendCompatibility
{
    public static string CurrentVersion=>typeof(BackendCompatibility).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]??"unknown";
    public static void Validate(ServerInfo info,string expectedVersion,string? expectedInstance=null)
    {
        if(info.ProductId!="PersonalOrderDesk"||info.ApiVersion!=1)throw new InvalidOperationException("此地址不是兼容的工作台后台。");
        if(info.AppVersion!=expectedVersion)throw new InvalidOperationException($"客户端版本为 {expectedVersion}，电脑后台为 {info.AppVersion??"旧版"}。请在电脑退出旧后台，启动配套版本；原账本无需重新建立。");
        if(expectedInstance is not null&&!string.Equals(info.InstanceId,expectedInstance,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("5059端口正在运行另一本工作台账本，已停止连接。请退出该后台，再启动上次使用的账本；没有切换或新建数据库。");
    }
}
