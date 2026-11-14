using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PersonalOrderDesk.Api;
using PersonalOrderDesk.Client;
using PersonalOrderDesk.Data;
using PersonalOrderDesk.Domain;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Tests;
public sealed class ClientWorkspaceTests : IAsyncLifetime
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"Desk-client-test-"+Guid.NewGuid().ToString("N"));
    private ServiceProvider provider=null!;
    private string Db=>Path.Combine(root,"pc","app.db");
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Db)!);
        provider=new ServiceCollection().AddLocalWorkspace(Db).BuildServiceProvider();
        await using var scope=provider.CreateAsyncScope();await scope.ServiceProvider.GetRequiredService<DeskDbContext>().Database.MigrateAsync();
    }
    public async Task DisposeAsync(){await provider.DisposeAsync();Directory.Delete(root,true);}
    private static WorkItemSaveRequestDto Order(string name)=>new(null,"TEST-001",name,ProjectType.Simulation,null,null,new DateOnly(2026,11,14),DueDateMode.Auto,null,null,null,70000,null,null);
    private async Task<(byte[] Zip,ServerInfo Info,string[] Migrations)> Snapshot()
    {
        await using var scope=provider.CreateAsyncScope();var migrations=scope.ServiceProvider.GetRequiredService<DeskDbContext>().Database.GetMigrations().ToArray();
        var info=new ServerInfo("PersonalOrderDesk",1,Guid.NewGuid().ToString("N"),"测试电脑",migrations.Last());
        return(await PortableSnapshot.Create(Db,info,migrations,Path.Combine(root,"scratch")),info,migrations);
    }
    [Fact]
    public async Task Snapshot_is_read_only_and_preserves_separate_local_ledger_across_restart()
    {
        var pc=provider.GetRequiredService<IWorkspaceGateway>();await pc.SaveOrder(null,Order("电脑订单"));var snapshot=await Snapshot();
        var mobileRoot=Path.Combine(root,"phone");var secrets=new Secrets();
        await using(var mobile=new NativeWorkspaceSession(mobileRoot,secrets,true)){
            await mobile.Initialize();await mobile.Gateway.SaveOrder(null,Order("手机独立订单"));
            var path=Path.Combine(mobileRoot,"pc-snapshots",snapshot.Info.InstanceId,"app.db");
            await PortableSnapshot.ValidateAndWrite(new MemoryStream(snapshot.Zip),path,snapshot.Info.InstanceId,snapshot.Migrations);
            mobile.Profile.SnapshotInstance=snapshot.Info.InstanceId;await mobile.UsePcSnapshot();
            Assert.Equal("电脑订单",Assert.Single((await mobile.Gateway.State()).Orders).ProjectName);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>mobile.Gateway.SaveOrder(null,Order("不可写")));
            await mobile.UseLocal();Assert.Equal("手机独立订单",Assert.Single((await mobile.Gateway.State()).Orders).ProjectName);
        }
        await using var restarted=new NativeWorkspaceSession(mobileRoot,secrets,true);await restarted.Initialize();
        Assert.Equal("手机独立订单",Assert.Single((await restarted.Gateway.State()).Orders).ProjectName);
    }
    [Fact]
    public async Task Invalid_snapshot_does_not_replace_existing_copy()
    {
        var snapshot=await Snapshot();var target=Path.Combine(root,"copy","app.db");
        await PortableSnapshot.ValidateAndWrite(new MemoryStream(snapshot.Zip),target,snapshot.Info.InstanceId,snapshot.Migrations);
        var original=await File.ReadAllBytesAsync(target);
        await Assert.ThrowsAsync<InvalidDataException>(()=>PortableSnapshot.ValidateAndWrite(new MemoryStream(snapshot.Zip),target,Guid.NewGuid().ToString("N"),snapshot.Migrations));
        await Assert.ThrowsAsync<InvalidDataException>(()=>PortableSnapshot.ValidateAndWrite(new MemoryStream(snapshot.Zip),target,snapshot.Info.InstanceId,["unknown-migration"]));
        Assert.Equal(original,await File.ReadAllBytesAsync(target));
    }
    [Fact]
    public async Task Missing_registered_phone_database_fails_without_creating_empty_ledger()
    {
        var mobileRoot=Path.Combine(root,"missing-phone");Directory.CreateDirectory(mobileRoot);await File.WriteAllTextAsync(Path.Combine(mobileRoot,"local-initialized"),"PersonalOrderDesk");
        await using var session=new NativeWorkspaceSession(mobileRoot,new Secrets(),true);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>session.Initialize());Assert.False(File.Exists(Path.Combine(mobileRoot,"local","app.db")));
    }
    [Fact]
    public async Task Api_requires_bearer_even_on_loopback_and_revokes_session_after_password_change()
    {
        var network=new NetworkAccessService(Path.Combine(root,"network"),["192.168.1.10"]);
        var identity=new ClientApiIdentity(Path.GetDirectoryName(Db)!);var protection=new EphemeralDataProtectionProvider();var sessions=new ClientApiSessions(protection,network,identity);
        var reached=false;var middleware=new ClientApiAccessMiddleware(_=>{reached=true;return Task.CompletedTask;});
        DefaultHttpContext Context(string? token=null){var c=new DefaultHttpContext();c.Request.Path="/api/v1/state";c.Connection.RemoteIpAddress=IPAddress.Loopback;c.Response.Body=new MemoryStream();if(token is not null)c.Request.Headers.Authorization="Bearer "+token;return c;}
        var missing=Context();await middleware.InvokeAsync(missing,sessions,network);Assert.Equal(401,missing.Response.StatusCode);Assert.False(reached);
        var token=sessions.Issue().Token;await middleware.InvokeAsync(Context(token),sessions,network);Assert.True(reached);reached=false;
        network.Save(true,"Synthetic-Password-2026!","Synthetic-Password-2026!");network=new NetworkAccessService(Path.Combine(root,"network"),["192.168.1.10"]);sessions=new ClientApiSessions(protection,network,identity);
        var expired=Context(token);await middleware.InvokeAsync(expired,sessions,network);Assert.Equal(401,expired.Response.StatusCode);Assert.False(reached);
        var denied=Context(sessions.Issue().Token);denied.Connection.RemoteIpAddress=IPAddress.Parse("8.8.8.8");await middleware.InvokeAsync(denied,sessions,network);Assert.Equal(403,denied.Response.StatusCode);
    }
    [Theory]
    [InlineData("v2.0.0-preview.2","2.0.0-preview.1",true)]
    [InlineData("v2.0.0-preview.10","2.0.0-preview.2",true)]
    [InlineData("v2.0.0","2.0.0-preview.1",true)]
    [InlineData("v2.0.0-preview.1","2.0.0",false)]
    [InlineData("v1.4.0","2.0.0-preview.1",false)]
    [InlineData("v2.0.0","2.0.0+commit",false)]
    public void Update_version_comparison_prevents_downgrades(string candidate,string current,bool expected)=>Assert.Equal(expected,GitHubUpdates.IsNewer(candidate,current));
    private sealed class Secrets : IClientSecretStore
    {private readonly Dictionary<string,string> values=[];public Task<string?> Read(string key)=>Task.FromResult(values.GetValueOrDefault(key));public Task Save(string key,string value){values[key]=value;return Task.CompletedTask;}}
}
