using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace PersonalOrderDesk.Client;
public sealed record MobileRestoreReview(Guid Id,string Target,DateTimeOffset CreatedAt,int Orders,int Recycled,long Received,long Cost);
public sealed partial class NativeWorkspaceSession
{
    private sealed record BackupMetadata(string ProductId,int FormatVersion,Guid OperationId,string? PhoneCopyInstance,string? PhoneCopyName,DateTimeOffset? SnapshotAt,DateTimeOffset CreatedAt);
    private sealed record DatabaseManifest(string ProductId,string Schema);
    private sealed record RestorePlan(MobileRestoreReview Review,string Candidate,string Target,ClientProfile Profile);
    private sealed record RestoreIntent(string Candidate,string Target,string Previous,ClientProfile Profile);
    private RestorePlan? restorePlan;
    private string RestoreIntentPath=>Path.Combine(root,"restore-intent.json");
    private async Task WriteBackupMetadata(string folder,Guid operation)
    {
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder,"sync-protection.json"),JsonSerializer.Serialize(new BackupMetadata("PersonalOrderDesk",1,operation,Profile.SnapshotInstance??Profile.ServerInstance,Profile.SnapshotName??Profile.ServerName,Profile.SnapshotAt,DateTimeOffset.UtcNow),DeskJson.Options));
    }
    private async Task ProtectCurrentBook(string folder)
    {
        await WriteBackupMetadata(folder,Guid.NewGuid());
        if(Profile.Mode==WorkspaceMode.Local)await BackupFile(LocalPath,Path.Combine(folder,"phone-independent.db"));
        else if(Profile.Mode==WorkspaceMode.PcSnapshot){await BackupFile(CopyPath(),Path.Combine(folder,"phone-pc-copy.db"));if(HasBaseline())await BackupFile(BaselinePath(),Path.Combine(folder,"common-baseline.db"));}
        else throw new InvalidOperationException("请先打开手机独立账本或电脑离线副本。");
    }
    private string ZipProtection(string folder)
    {
        var output=Path.Combine(root,"exports",Path.GetFileName(folder)+".zip");Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        if(File.Exists(output))File.Delete(output);ZipFile.CreateFromDirectory(folder,output,CompressionLevel.Fastest,false);return output;
    }
    private static async Task Extract(ZipArchive zip,string entryName,string target,long maximum)
    {
        var entry=zip.GetEntry(entryName)??throw new InvalidDataException("保护包缺少 "+entryName+"，请选择完整的ZIP备份。");
        if(entry.Length>maximum)throw new InvalidDataException("备份文件过大。");Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var input=entry.Open();await using var output=File.Create(target);var buffer=new byte[81920];long total=0;int count;
        while((count=await input.ReadAsync(buffer))>0){total+=count;if(total>maximum)throw new InvalidDataException("备份解压后超过限制。");await output.WriteAsync(buffer.AsMemory(0,count));}
    }
    private async Task ValidateRestoreDatabase(string incoming,string manifestPath,string candidate,string instance)
    {
        var manifest=JsonSerializer.Deserialize<DatabaseManifest>(await File.ReadAllTextAsync(manifestPath),DeskJson.Options)??throw new InvalidDataException("备份清单无效。");
        var known=KnownMigrations();if(manifest.ProductId!="PersonalOrderDesk"||manifest.Schema!=known.Last())throw new InvalidDataException("备份格式或数据库版本与当前App不一致，未修改现有账本。");
        var info=new ServerInfo("PersonalOrderDesk",1,instance,"恢复预览",known.Last());
        var bytes=await PortableSnapshot.Create(incoming,info,known,Path.Combine(root,"tmp","restore-validation"));
        using var stream=new MemoryStream(bytes);await PortableSnapshot.ValidateAndWrite(stream,candidate,instance,known);
    }
    public async Task<MobileRestoreReview> PreviewRestore(string archive)
    {
        if(!IsMobile||Profile.Mode==WorkspaceMode.Remote)throw new InvalidOperationException("请先打开要恢复的手机独立账本或电脑离线副本。");
        restorePlan=null;var id=Guid.NewGuid();var temp=Path.Combine(root,"tmp","restore-"+id.ToString("N"));Directory.CreateDirectory(temp);
        if(new FileInfo(archive).Length>PortableSnapshot.MaxBytes*5)throw new InvalidDataException("备份包过大。");
        using var zip=ZipFile.OpenRead(archive);if(zip.Entries.Count>40)throw new InvalidDataException("备份包内容不符合工作台格式。");
        var metadataPath=Path.Combine(temp,"sync-protection.json");await Extract(zip,"sync-protection.json",metadataPath,65536);
        var metadata=JsonSerializer.Deserialize<BackupMetadata>(await File.ReadAllTextAsync(metadataPath),DeskJson.Options)??throw new InvalidDataException("保护包说明无效。");
        if(metadata.ProductId!="PersonalOrderDesk"||metadata.FormatVersion!=1)throw new InvalidDataException("这不是支持的工作台保护包。");
        bool local=Profile.Mode==WorkspaceMode.Local;var source=local?"phone-independent.db":zip.GetEntry("phone-pc-copy.db") is not null?"phone-pc-copy.db":"previous-pc-copy.db";
        var next=JsonSerializer.Deserialize<ClientProfile>(JsonSerializer.Serialize(Profile,DeskJson.Options),DeskJson.Options)!;
        string target,instance;
        if(local){target=Path.GetDirectoryName(LocalPath)!;instance=Guid.NewGuid().ToString("N");next.Mode=WorkspaceMode.Local;}
        else{
            if(!Guid.TryParse(metadata.PhoneCopyInstance,out var copyId))throw new InvalidDataException("该保护包缺少电脑副本身份，请选择新版导出的完整保护包。");
            if(Profile.SnapshotInstance is {} current&&Guid.Parse(current)!=copyId)throw new InvalidOperationException("这份副本属于另一台电脑。为避免混用，先切换回对应电脑账本再恢复。");
            instance=copyId.ToString("N");target=SnapshotFolder(instance);next.SnapshotInstance=instance;next.SnapshotName=metadata.PhoneCopyName;next.SnapshotAt=metadata.SnapshotAt;next.OfflineEditingEnabled=true;next.Mode=WorkspaceMode.PcSnapshot;
        }
        var candidate=Path.Combine(temp,"candidate");Directory.CreateDirectory(candidate);
        async Task ReadDb(string name,string destination){var input=Path.Combine(temp,"incoming-"+Path.GetFileName(name));var manifest=input+".manifest.json";await Extract(zip,name,input,PortableSnapshot.MaxBytes);await Extract(zip,name+".manifest.json",manifest,65536);await ValidateRestoreDatabase(input,manifest,Path.Combine(candidate,destination),instance);}
        await ReadDb(source,"app.db");if(!local)await ReadDb("common-baseline.db","baseline.db");
        await using var provider=new ServiceCollection().AddLocalWorkspace(Path.Combine(candidate,"app.db"),true).BuildServiceProvider();var state=await provider.GetRequiredService<IWorkspaceGateway>().State();
        var review=new MobileRestoreReview(id,local?"手机独立账本":"电脑离线副本",metadata.CreatedAt,state.Orders.Count,state.Recycle.Count,state.Money.Sum(x=>x.Received),state.Money.Sum(x=>x.Cost));
        restorePlan=new(review,candidate,target,next);return review;
    }
    public async Task RestoreBackup(Guid previewId)
    {
        var plan=restorePlan is {} p&&p.Review.Id==previewId?p:throw new InvalidOperationException("恢复预览已失效，请重新选择保护包。");
        var protection=Path.Combine(root,"backups","restore-before-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")+"-"+Guid.NewGuid().ToString("N"));await ProtectCurrentBook(protection);
        plan.Profile.LastRestoreFolder=protection;var intent=new RestoreIntent(plan.Candidate,plan.Target,Path.Combine(protection,"previous-files"),plan.Profile);
        await File.WriteAllTextAsync(RestoreIntentPath,JsonSerializer.Serialize(intent,DeskJson.Options));
        await DisposeDatabase();current=null;await ResumeRestore();restorePlan=null;
        if(Profile.Mode==WorkspaceMode.PcSnapshot)await UsePcSnapshot();else await UseLocal();
    }
    private void CheckInsideRoot(string path)
    {if(!Path.GetFullPath(path).StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("恢复位置不属于当前App，已停止。");}
    private async Task ResumeRestore()
    {
        if(!File.Exists(RestoreIntentPath))return;
        var intent=JsonSerializer.Deserialize<RestoreIntent>(await File.ReadAllTextAsync(RestoreIntentPath),DeskJson.Options)??throw new InvalidDataException("待恢复记录损坏，未创建空库。");
        CheckInsideRoot(intent.Candidate);CheckInsideRoot(intent.Target);CheckInsideRoot(intent.Previous);
        var expected=intent.Profile.Mode==WorkspaceMode.Local?Path.GetDirectoryName(LocalPath)!:SnapshotFolder(intent.Profile.SnapshotInstance);
        if(!string.Equals(Path.GetFullPath(intent.Target),Path.GetFullPath(expected),StringComparison.OrdinalIgnoreCase)||!Path.GetFullPath(intent.Candidate).StartsWith(Path.Combine(root,"tmp")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!Path.GetFullPath(intent.Previous).StartsWith(Path.Combine(root,"backups")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("恢复目标与账本身份不一致，已停止。");
        Directory.CreateDirectory(Path.GetDirectoryName(intent.Target)!);
        if(!Directory.Exists(intent.Previous)&&Directory.Exists(intent.Target)){Directory.CreateDirectory(Path.GetDirectoryName(intent.Previous)!);Directory.Move(intent.Target,intent.Previous);}
        if(!Directory.Exists(intent.Target)){
            if(Directory.Exists(intent.Candidate))Directory.Move(intent.Candidate,intent.Target);
            else{if(Directory.Exists(intent.Previous))Directory.Move(intent.Previous,intent.Target);throw new InvalidDataException("恢复候选文件缺失，原账本已保留，请检查保护备份。");}
        }
        if(!File.Exists(Path.Combine(intent.Target,"app.db")))throw new InvalidDataException("恢复账本缺失，原账本在保护目录中，未创建空库。");
        Profile=intent.Profile;await SaveProfile();if(Profile.Mode==WorkspaceMode.Local)await File.WriteAllTextAsync(Path.Combine(root,"local-initialized"),"PersonalOrderDesk");File.Delete(RestoreIntentPath);
    }
    public Task<string> ExportRestoreProtection()
    {var folder=Profile.LastRestoreFolder??throw new InvalidOperationException("尚未执行过恢复。");CheckInsideRoot(folder);return Task.FromResult(ZipProtection(folder));}
}
