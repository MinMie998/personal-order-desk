using Bunit;
using Microsoft.Extensions.DependencyInjection;
using PersonalOrderDesk.Client;
using PersonalOrderDesk.Domain;
using PersonalOrderDesk.SharedUI;
namespace PersonalOrderDesk.Tests;
public sealed class NativeBackNavigationTests
{
    private sealed class Secrets:IClientSecretStore{public Task<string?> Read(string key)=>Task.FromResult<string?>(null);public Task Save(string key,string value)=>Task.CompletedTask;}
    private sealed class Platform:IClientPlatform
    {
        public Task CopyText(string text)=>Task.CompletedTask;public Task OpenWeb(string path)=>Task.CompletedTask;public Task ShareFile(string path)=>Task.CompletedTask;public Task OpenUpdate(string path)=>Task.CompletedTask;public Task<string?> PickBackupFile()=>Task.FromResult<string?>(null);
    }
    [Fact]public async Task System_back_closes_dialog_then_returns_to_origin_and_overview_before_backgrounding()
    {
        var root=Path.Combine(Path.GetTempPath(),"Desk-back-"+Guid.NewGuid().ToString("N"));
        await using var session=new NativeWorkspaceSession(root,new Secrets(),true);await session.Initialize();
        await session.Gateway.SaveOrder(null,new(null,"BACK-001","返回验收",ProjectType.Simulation,null,null,DateOnly.FromDateTime(DateTime.Today),DueDateMode.Auto,null,null,null,10000,null,null));
        try{
            using var context=new BunitContext();var back=new ClientBackNavigation();context.Services.AddSingleton(back);context.Services.AddSingleton<IWorkspaceSession>(session);context.Services.AddSingleton<IClientPlatform>(new Platform());
            var cut=context.Render<WorkbenchShell>();cut.WaitForAssertion(()=>Assert.Equal("概览",cut.Find("h1").TextContent));
            cut.FindAll("nav button").Single(x=>x.TextContent=="收支结算").Click();
            cut.Find(".money-table .project-link").Click();cut.WaitForAssertion(()=>Assert.Equal("返回验收",cut.Find("h1").TextContent));
            cut.FindAll("button").Single(x=>x.TextContent.Contains("记收款")).Click();Assert.Single(cut.FindAll("[role=dialog]"));
            Assert.True(await back.GoBack());Assert.Empty(cut.FindAll("[role=dialog]"));Assert.Equal("返回验收",cut.Find("h1").TextContent);
            Assert.True(await back.GoBack());Assert.Equal("收支结算",cut.Find("h1").TextContent);
            Assert.True(await back.GoBack());Assert.Equal("概览",cut.Find("h1").TextContent);
            Assert.False(await back.GoBack());
        }finally{await session.DisposeAsync();Directory.Delete(root,true);}
    }
}
