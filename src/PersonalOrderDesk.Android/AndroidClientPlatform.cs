using PersonalOrderDesk.Client;
namespace PersonalOrderDesk.Android;
public sealed class AndroidSecretStore : IClientSecretStore
{
    public Task<string?> Read(string key)=>SecureStorage.Default.GetAsync(key);
    public Task Save(string key,string value)=>SecureStorage.Default.SetAsync(key,value);
}
public sealed class AndroidClientPlatform : IClientPlatform
{
    public async Task<string?> PickBackupFile()
    {
        var file=await FilePicker.Default.PickAsync(new PickOptions{PickerTitle="选择工作台ZIP保护包"});if(file is null)return null;
        var path=Path.Combine(FileSystem.CacheDirectory,"restore-"+Guid.NewGuid().ToString("N")+".zip");
        await using var input=await file.OpenReadAsync();await using var output=File.Create(path);var buffer=new byte[81920];long size=0;int count;
        while((count=await input.ReadAsync(buffer))>0){size+=count;if(size>PortableSnapshot.MaxBytes*5)throw new InvalidDataException("备份包过大。");await output.WriteAsync(buffer.AsMemory(0,count));}return path;
    }
    public Task OpenUpdate(string path)=>Launcher.Default.OpenAsync(new OpenFileRequest("安装工作台更新",new ReadOnlyFile(path,"application/vnd.android.package-archive")));
    public Task CopyText(string text)=>Clipboard.Default.SetTextAsync(text);
    public Task OpenWeb(string relativePath)=>throw new InvalidOperationException("高级维护请在电脑工作台打开。");
    public async Task ShareFile(string path)
    {
        var files=new List<ShareFile>{new(path)};
        if(File.Exists(path+".manifest.json"))files.Add(new(path+".manifest.json"));
        await Share.Default.RequestAsync(new ShareMultipleFilesRequest {Title="保存账本备份",Files=files});
    }
}
