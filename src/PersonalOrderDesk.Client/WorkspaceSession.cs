using System.Text.Json;
using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using PersonalOrderDesk.Data;

namespace PersonalOrderDesk.Client;

public enum WorkspaceMode { Local, Remote, PcSnapshot }
public sealed class ClientProfile
{
    public WorkspaceMode Mode {get;set;}
    public string? ServerAddress {get;set;}
    public string? ServerInstance {get;set;}
    public string? ServerName {get;set;}
    public string? SnapshotInstance {get;set;}
    public string? SnapshotName {get;set;}
    public DateTimeOffset? SnapshotAt {get;set;}
    public string? PaymentMethod {get;set;}
    public string? LastSyncProtection {get;set;}
    public string? LastSyncFolder {get;set;}
}
public interface IClientSecretStore
{
    Task<string?> Read(string key);
    Task Save(string key,string value);
}
public interface IClientPlatform
{
    Task CopyText(string text);
    Task OpenWeb(string relativePath);
    Task ShareFile(string path);
    Task OpenUpdate(string path);
}
public interface IWorkspaceSession
{
    IWorkspaceGateway Gateway {get;}
    ClientProfile Profile {get;}
    bool IsMobile {get;}
    bool CanSwitch {get;}
    bool ReadOnly {get;}
    string SourceLabel {get;}
    Task Initialize();
    Task UseLocal();
    Task Connect(string address,string password);
    Task SyncPc();
    Task<SyncReview?> PreviewSync();
    Task CommitSync(Dictionary<string,string> choices);
    Task<string> ExportSyncProtection();
    Task UsePcSnapshot();
    Task<string> Backup();
    Task RememberMethod(string? method);
}
public sealed class NativeWorkspaceSession : IWorkspaceSession,IAsyncDisposable
{
    private readonly string root;
    private readonly IClientSecretStore secrets;
    private readonly Func<Task<IWorkspaceGateway>>? windowsLocal;
    private ServiceProvider? database;
    private HttpWorkspaceGateway? remote;
    private IWorkspaceGateway? current;
    private (SyncReview Review,string PhoneFingerprint,string Folder)? pendingSync;
    public ClientProfile Profile {get;private set;}=new();
    public bool IsMobile {get;}
    public bool CanSwitch=>true;
    public bool ReadOnly=>Profile.Mode==WorkspaceMode.PcSnapshot&&!HasBaseline();
    public string SourceLabel=>Profile.Mode switch { WorkspaceMode.Remote=>$"连接电脑 · {Profile.ServerName??"未连接"}", WorkspaceMode.PcSnapshot=>$"电脑账本 · 离线副本 · {Profile.SnapshotName??"未同步"}", _=>IsMobile?"手机独立账本":"本机工作台" };
    public IWorkspaceGateway Gateway=>current??throw new InvalidOperationException("数据源尚未就绪，请在“连接与数据”中重新连接或选择账本。");
    public NativeWorkspaceSession(string dataRoot,IClientSecretStore secretStore,bool mobile,Func<Task<IWorkspaceGateway>>? desktopLocal=null)
    {root=Path.GetFullPath(dataRoot);secrets=secretStore;IsMobile=mobile;windowsLocal=desktopLocal;}
    private string ProfilePath=>Path.Combine(root,"client-profile.json");
    private string LocalPath=>Path.Combine(root,"local","app.db");
    public async Task Initialize()
    {
        Directory.CreateDirectory(root);
        if(File.Exists(ProfilePath))Profile=JsonSerializer.Deserialize<ClientProfile>(await File.ReadAllTextAsync(ProfilePath),DeskJson.Options)??throw new InvalidDataException("数据来源设置损坏，请保留数据后检查配置。");
        if(Profile.Mode==WorkspaceMode.PcSnapshot)await UsePcSnapshot();
        else if(Profile.Mode==WorkspaceMode.Remote) {remote=await Remote();current=remote;}
        else await UseLocal();
    }
    private async Task SaveProfile()
    {Directory.CreateDirectory(root);var temporary=ProfilePath+".tmp";await File.WriteAllTextAsync(temporary,JsonSerializer.Serialize(Profile,DeskJson.Options));File.Move(temporary,ProfilePath,true);}
    public async Task UseLocal()
    {
        IWorkspaceGateway gateway;
        ServiceProvider? provider=null;
        if(windowsLocal is not null)gateway=await windowsLocal();
        else {
            var marker=Path.Combine(root,"local-initialized");
            if(File.Exists(marker)&&!File.Exists(LocalPath))throw new InvalidOperationException("手机独立账本文件缺失，未创建空库。请从本地备份恢复。");
            Directory.CreateDirectory(Path.GetDirectoryName(LocalPath)!);
            provider=new ServiceCollection().AddLocalWorkspace(LocalPath).BuildServiceProvider();
            await using(var scope=provider.CreateAsyncScope()) {
                var db=scope.ServiceProvider.GetRequiredService<DeskDbContext>();
                var pending=(await db.Database.GetPendingMigrationsAsync()).Any();
                if(pending&&File.Exists(LocalPath)&&new FileInfo(LocalPath).Length>0)await BackupFile(LocalPath,Path.Combine(root,"backups","before-upgrade-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")+".db"));
                await db.Database.MigrateAsync();
            }
            await File.WriteAllTextAsync(marker,"PersonalOrderDesk");gateway=provider.GetRequiredService<IWorkspaceGateway>();
        }
        await DisposeDatabase();database=provider;current=gateway;Profile.Mode=WorkspaceMode.Local;await SaveProfile();
    }
    private async Task<HttpWorkspaceGateway> Remote()
    {
        if(string.IsNullOrWhiteSpace(Profile.ServerAddress)||!Guid.TryParse(Profile.ServerInstance,out _))throw new InvalidOperationException("请先连接电脑。");
        var token=await secrets.Read("pc-"+Profile.ServerInstance)??throw new InvalidOperationException("电脑连接已过期，请重新输入密码连接。");
        return new HttpWorkspaceGateway(ServerAddressPolicy.Parse(Profile.ServerAddress),token);
    }
    public async Task Connect(string address,string password)
    {
        var uri=ServerAddressPolicy.Parse(address);var session=await HttpWorkspaceGateway.Login(uri,password);
        var next=new HttpWorkspaceGateway(uri,session.Token);await next.State();
        await secrets.Save("pc-"+session.Server.InstanceId,session.Token);
        await DisposeDatabase();remote?.Dispose();remote=next;current=next;
        Profile.ServerAddress=uri.AbsoluteUri;Profile.ServerInstance=session.Server.InstanceId;Profile.ServerName=session.Server.Name;Profile.Mode=WorkspaceMode.Remote;await SaveProfile();
    }
    public async Task SyncPc()
    {
        if(HasBaseline()&&File.Exists(CopyPath())&&ThreeWaySync.Fingerprint(await ThreeWaySync.Read(BaselinePath()))!=ThreeWaySync.Fingerprint(await ThreeWaySync.Read(CopyPath())))throw new InvalidOperationException("手机副本有未同步改动，请使用双向同步预览，不能直接覆盖。");
        using var connection=await Remote();
        var operation=Guid.NewGuid();var protection=Path.Combine(root,"backups","sync-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")+"-"+operation.ToString("N"));Directory.CreateDirectory(protection);
        var log=Path.Combine(protection,"sync-history.json");
        var prior=Guid.TryParse(Profile.SnapshotInstance,out _)?Path.Combine(SnapshotFolder(Profile.SnapshotInstance),"app.db"):null;
        SyncProtectionReceipt? pcBackup=null;
        try {
            if(File.Exists(LocalPath))await BackupFile(LocalPath,Path.Combine(protection,"phone-independent.db"));
            if(prior is not null&&File.Exists(prior))await BackupFile(prior,Path.Combine(protection,"previous-pc-copy.db"));
            await File.WriteAllTextAsync(log,JsonSerializer.Serialize(new{Operation=operation,Status="phone-protected",Pc=Profile.ServerInstance,CreatedAt=DateTimeOffset.UtcNow},DeskJson.Options));
            pcBackup=await connection.ProtectSync(operation);
            if(pcBackup.OperationId!=operation)throw new InvalidDataException("电脑备份编号不一致，同步已停止。");
            await CachePcBackup(connection,operation,protection);
            var bytes=await connection.Snapshot();
        var folder=SnapshotFolder(Profile.ServerInstance);var target=Path.Combine(folder,"app.db");
        var known=KnownMigrations();
        using var stream=new MemoryStream(bytes);var manifest=await PortableSnapshot.ValidateAndWrite(stream,target,Profile.ServerInstance!,known);
        SaveBaseline(target,Path.Combine(folder,"baseline.db"));
        Profile.SnapshotInstance=manifest.InstanceId;Profile.SnapshotName=manifest.Name;Profile.SnapshotAt=manifest.CreatedAt;await SaveProfile();
            Profile.LastSyncFolder=protection;Profile.LastSyncProtection=$"{operation:N} · PC：{pcBackup.BackupFile} · 手机：{protection}";await SaveProfile();
            await File.WriteAllTextAsync(log,JsonSerializer.Serialize(new{Operation=operation,Status="completed",Pc=Profile.ServerInstance,PcBackup=pcBackup,CompletedAt=DateTimeOffset.UtcNow},DeskJson.Options));
            if(Profile.Mode==WorkspaceMode.PcSnapshot)await UsePcSnapshot();
        }catch(Exception ex){await File.WriteAllTextAsync(log,JsonSerializer.Serialize(new{Operation=operation,Status="failed",PcBackup=pcBackup,Error=ex.Message,At=DateTimeOffset.UtcNow},DeskJson.Options));throw;}
    }
    public async Task UsePcSnapshot()
    {
        var path=Path.Combine(SnapshotFolder(Profile.SnapshotInstance),"app.db");
        if(!File.Exists(path))throw new InvalidOperationException("还没有电脑副本，请连接电脑后点击“一键同步PC副本”。");
        var provider=new ServiceCollection().AddLocalWorkspace(path,!File.Exists(Path.Combine(SnapshotFolder(Profile.SnapshotInstance),"baseline.db"))).BuildServiceProvider();
        await provider.GetRequiredService<IWorkspaceGateway>().State();
        await DisposeDatabase();database=provider;current=provider.GetRequiredService<IWorkspaceGateway>();Profile.Mode=WorkspaceMode.PcSnapshot;await SaveProfile();
    }
    private bool HasBaseline()=>Guid.TryParse(Profile.SnapshotInstance,out _)&&File.Exists(BaselinePath());
    private string CopyPath()=>Path.Combine(SnapshotFolder(Profile.SnapshotInstance),"app.db");
    private string BaselinePath()=>Path.Combine(SnapshotFolder(Profile.SnapshotInstance),"baseline.db");
    private static void SaveBaseline(string source,string target){var temporary=target+".new";File.Copy(source,temporary,true);File.Move(temporary,target,true);}
    public async Task<SyncReview?> PreviewSync()
    {
        pendingSync=null;
        if(!HasBaseline()||Profile.SnapshotInstance!=Profile.ServerInstance){
            if(HasBaseline()&&ThreeWaySync.Fingerprint(await ThreeWaySync.Read(BaselinePath()))!=ThreeWaySync.Fingerprint(await ThreeWaySync.Read(CopyPath())))throw new InvalidOperationException("旧电脑副本还有未合并改动。请先连接原电脑同步，或保留备份后再切换电脑。");
            await SyncPc();return null;
        }
        using var connection=await Remote();var operation=Guid.NewGuid();
        var folder=Path.Combine(root,"backups","sync-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")+"-"+operation.ToString("N"));Directory.CreateDirectory(folder);
        if(File.Exists(LocalPath))await BackupFile(LocalPath,Path.Combine(folder,"phone-independent.db"));
        await BackupFile(CopyPath(),Path.Combine(folder,"phone-pc-copy.db"));await BackupFile(BaselinePath(),Path.Combine(folder,"common-baseline.db"));
        var phone=await ThreeWaySync.Read(CopyPath());var fingerprint=ThreeWaySync.Fingerprint(phone);
        var pcBackup=await connection.ProtectSync(operation);
        if(pcBackup.OperationId!=operation)throw new InvalidDataException("电脑备份编号不一致，未进入合并。");
        await CachePcBackup(connection,operation,folder);
        if(ThreeWaySync.Fingerprint(await ThreeWaySync.Read(Path.Combine(folder,"phone-pc-copy.db")))!=fingerprint)throw new InvalidOperationException("手机备份与当前副本不一致，请重新发起同步。");
        await File.WriteAllTextAsync(Path.Combine(folder,"sync-history.json"),JsonSerializer.Serialize(new{Operation=operation,Status="both-protected",PcBackup=pcBackup,PhoneFingerprint=fingerprint,At=DateTimeOffset.UtcNow},DeskJson.Options));
        var review=await connection.PreviewSync(new(operation,Profile.ServerInstance!,await ThreeWaySync.Read(BaselinePath()),phone));
        pendingSync=(review,fingerprint,folder);Profile.LastSyncFolder=folder;Profile.LastSyncProtection=$"{operation:N} · PC：{pcBackup.BackupFile} · 手机：{folder}";await SaveProfile();return review;
    }
    public async Task CommitSync(Dictionary<string,string> choices)
    {
        var plan=pendingSync??throw new InvalidOperationException("请先完成同步预览。");
        if(ThreeWaySync.Fingerprint(await ThreeWaySync.Read(CopyPath()))!=plan.PhoneFingerprint)throw new InvalidOperationException("预览后手机副本又有改动，请重新预览。");
        using var connection=await Remote();await connection.CommitSync(new(plan.Review.OperationId,choices,true));
        await File.WriteAllTextAsync(Path.Combine(plan.Folder,"sync-history.json"),JsonSerializer.Serialize(new{Operation=plan.Review.OperationId,Status="pc-merged-phone-refresh-pending",Choices=choices,At=DateTimeOffset.UtcNow},DeskJson.Options));
        var bytes=await connection.Snapshot();var target=CopyPath();
        using var stream=new MemoryStream(bytes);var manifest=await PortableSnapshot.ValidateAndWrite(stream,target,Profile.ServerInstance!,KnownMigrations());
        SaveBaseline(target,BaselinePath());Profile.SnapshotAt=manifest.CreatedAt;await SaveProfile();
        await File.WriteAllTextAsync(Path.Combine(plan.Folder,"sync-history.json"),JsonSerializer.Serialize(new{Operation=plan.Review.OperationId,Status="completed",Choices=choices,At=DateTimeOffset.UtcNow},DeskJson.Options));
        pendingSync=null;if(Profile.Mode==WorkspaceMode.PcSnapshot)await UsePcSnapshot();
    }
    private async Task CachePcBackup(HttpWorkspaceGateway connection,Guid operation,string folder)
    {
        var bytes=await connection.SyncBackup(operation);using var archive=new MemoryStream(bytes);var path=Path.Combine(folder,"pc-before.db");
        var manifest=await PortableSnapshot.ValidateAndWrite(archive,path,Profile.ServerInstance!,KnownMigrations());
        await File.WriteAllTextAsync(path+".manifest.json",JsonSerializer.Serialize(new{manifest.ProductId,Schema=manifest.Migrations.Last(),manifest.CreatedAt,SyncOperation=operation},DeskJson.Options));
    }
    public Task<string> ExportSyncProtection()
    {
        var folder=Profile.LastSyncFolder??throw new InvalidOperationException("尚无同步保护备份。");
        var absolute=Path.GetFullPath(folder);if(!absolute.StartsWith(Path.GetFullPath(Path.Combine(root,"backups"))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!Directory.Exists(absolute))throw new InvalidOperationException("同步备份目录不可用。");
        var output=Path.Combine(root,"exports",Path.GetFileName(absolute)+".zip");Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        if(File.Exists(output))File.Delete(output);ZipFile.CreateFromDirectory(absolute,output,CompressionLevel.Fastest,false);return Task.FromResult(output);
    }
    private string SnapshotFolder(string? instance)
    {
        if(!Guid.TryParse(instance,out var id))throw new InvalidOperationException("还没有有效的电脑副本，请先连接并同步。");
        return Path.Combine(root,"pc-snapshots",id.ToString("N"));
    }
    private static string[] KnownMigrations()
    {
        using var db=new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite("Data Source=:memory:").Options);
        return db.Database.GetMigrations().ToArray();
    }
    public async Task<string> Backup()
    {
        if(!IsMobile||Profile.Mode!=WorkspaceMode.Local)throw new InvalidOperationException("电脑账本请使用电脑上的设置与备份；电脑副本可随时重新同步。");
        var path=Path.Combine(root,"backups","local-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")+".db");await BackupFile(LocalPath,path);return path;
    }
    private static async Task BackupFile(string sourcePath,string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await using var source=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=sourcePath,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
        await using var target=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=targetPath,Pooling=false}.ToString());
        await source.OpenAsync();await target.OpenAsync();source.BackupDatabase(target);
        await File.WriteAllTextAsync(targetPath+".manifest.json",JsonSerializer.Serialize(new{ProductId="PersonalOrderDesk",Schema=KnownMigrations().LastOrDefault(),CreatedAt=DateTimeOffset.UtcNow}));
    }
    public async Task RememberMethod(string? method){Profile.PaymentMethod=new[]{"微信","支付宝","银行卡","现金"}.Contains(method)?method:null;await SaveProfile();}
    private async Task DisposeDatabase(){if(database is not null){await database.DisposeAsync();database=null;}}
    public async ValueTask DisposeAsync(){await DisposeDatabase();remote?.Dispose();}
}
