using PersonalOrderDesk.Client;
namespace PersonalOrderDesk.Android;
public sealed class AndroidSecretStore : IClientSecretStore
{
    public Task<string?> Read(string key)=>SecureStorage.Default.GetAsync(key);
    public Task Save(string key,string value)=>SecureStorage.Default.SetAsync(key,value);
}
public sealed class AndroidClientPlatform : IClientPlatform
{
    public Task OpenUpdate(string path)=>Launcher.Default.OpenAsync(new OpenFileRequest("安装工作台更新",new ReadOnlyFile(path,"application/vnd.android.package-archive")));
    public Task CopyText(string text)=>Clipboard.Default.SetTextAsync(text);
    public Task OpenWeb(string relativePath)=>throw new InvalidOperationException("高级维护请在电脑工作台打开。");
    public async Task ShareFile(string path)
    {
        var files=new List<ShareFile>{new(path)};
        if(File.Exists(path+".manifest.json"))files.Add(new(path+".manifest.json"));
        await Share.Default.RequestAsync(new ShareMultipleFilesRequest {Title="保存独立账本备份",Files=files});
    }
}
