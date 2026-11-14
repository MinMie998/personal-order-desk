using PersonalOrderDesk.Client;
using PersonalOrderDesk.Domain;
using System.IO.Compression;
namespace PersonalOrderDesk.Tests;
public sealed class MobileRestoreTests : IAsyncLifetime
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"Desk-restore-"+Guid.NewGuid().ToString("N"));
    private NativeWorkspaceSession session=null!;
    private sealed class Secrets:IClientSecretStore{public Task<string?> Read(string key)=>Task.FromResult<string?>(null);public Task Save(string key,string value)=>Task.CompletedTask;}
    public async Task InitializeAsync(){session=new(root,new Secrets(),true);await session.Initialize();}
    public async Task DisposeAsync(){await session.DisposeAsync();Directory.Delete(root,true);}
    private async Task Add(string number)=>await session.Gateway.SaveOrder(null,new(null,number,number,ProjectType.Simulation,null,null,DateOnly.FromDateTime(DateTime.Today),DueDateMode.Auto,null,null,null,10000,null,null));
    [Fact]public async Task Restore_preview_is_read_only_and_replacement_preserves_recovery_book()
    {
        await Add("A");var backup=await session.Backup();await Add("B");
        var preview=await session.PreviewRestore(backup);Assert.Equal(1,preview.Orders);Assert.Equal(2,(await session.Gateway.State()).Orders.Count);
        await session.RestoreBackup(preview.Id);Assert.Single((await session.Gateway.State()).Orders);
        var protection=await session.ExportRestoreProtection();var undo=await session.PreviewRestore(protection);Assert.Equal(2,undo.Orders);
        await session.RestoreBackup(undo.Id);Assert.Equal(2,(await session.Gateway.State()).Orders.Count);
        await session.DisposeAsync();session=new(root,new Secrets(),true);await session.Initialize();Assert.Equal(2,(await session.Gateway.State()).Orders.Count);
    }
    [Fact]public async Task Damaged_backup_cannot_replace_current_book()
    {
        await Add("KEEP");var archive=Path.Combine(root,"bad.zip");using(var zip=ZipFile.Open(archive,ZipArchiveMode.Create)){var entry=zip.CreateEntry("sync-protection.json");using var writer=new StreamWriter(entry.Open());writer.Write("{}");}
        await Assert.ThrowsAsync<InvalidDataException>(()=>session.PreviewRestore(archive));Assert.Single((await session.Gateway.State()).Orders);
    }
}
