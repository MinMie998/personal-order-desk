using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PersonalOrderDesk.Client;
using PersonalOrderDesk.Data;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Api;

public sealed class ClientApiIdentity
{
    public ServerInfo Info { get; }
    public ClientApiIdentity(string dataDirectory)
    {
        var path=Path.Combine(dataDirectory,"workstation-id.json");
        var id=File.Exists(path)?JsonSerializer.Deserialize<string>(File.ReadAllText(path)):Guid.NewGuid().ToString("N");
        if(!Guid.TryParse(id,out _))throw new InvalidDataException("工作台身份登记无效，请检查数据目录。");
        if(!File.Exists(path))File.WriteAllText(path,JsonSerializer.Serialize(id));
        Info=new("PersonalOrderDesk",1,id!,Environment.MachineName,"20261114150000_WorkItemRecycleBin");
    }
}
public sealed class ClientApiSessions(IDataProtectionProvider protection,NetworkAccessService network,ClientApiIdentity identity)
{
    private readonly IDataProtector protector=protection.CreateProtector("PersonalOrderDesk.DeviceSession.v1");
    private sealed record Ticket(string InstanceId,string? Revision,DateTimeOffset ExpiresAt);
    public SessionResponse Issue() { var expires=DateTimeOffset.UtcNow.AddHours(8);return new(protector.Protect(JsonSerializer.Serialize(new Ticket(identity.Info.InstanceId,network.Current.Revision,expires))),expires,identity.Info); }
    public bool Valid(string token)
    {
        try {var t=JsonSerializer.Deserialize<Ticket>(protector.Unprotect(token));return t is not null&&t.InstanceId==identity.Info.InstanceId&&t.Revision==network.Current.Revision&&t.ExpiresAt>DateTimeOffset.UtcNow;}
        catch(Exception ex) when(ex is System.Security.Cryptography.CryptographicException or JsonException or FormatException or ArgumentException){return false;}
    }
}
public sealed class ClientApiAccessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context,ClientApiSessions sessions,NetworkAccessService network)
    {
        if(!context.Request.Path.StartsWithSegments("/api/v1")){await next(context);return;}
        var local=NetworkAccessService.IsLocal(context);
        if(!local&&(!network.Current.Enabled||context.Connection.RemoteIpAddress is not{}ip||!NetworkAccessService.IsPrivateAddress(ip)))
        {context.Response.StatusCode=403;await context.Response.WriteAsJsonAsync(new ClientApiError("电脑尚未允许此局域网连接。"));return;}
        if((HttpMethods.IsGet(context.Request.Method)&&context.Request.Path=="/api/v1/info")||
            (HttpMethods.IsPost(context.Request.Method)&&context.Request.Path=="/api/v1/session")) {await next(context);return;}
        var header=context.Request.Headers.Authorization.ToString();
        if(!header.StartsWith("Bearer ",StringComparison.Ordinal)||!sessions.Valid(header[7..]))
        {context.Response.StatusCode=401;await context.Response.WriteAsJsonAsync(new ClientApiError("连接已过期，请重新连接电脑。"));return;}
        var claims=new[]{new Claim(ClaimTypes.Name,"个人工作台设备"),new Claim("network-revision",network.Current.Revision??"")};
        context.User=new ClaimsPrincipal(new ClaimsIdentity(claims,"Device")); await next(context);
    }
}
public sealed class ClientApiErrors : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context,EndpointFilterDelegate next)
    {
        try{return await next(context);}
        catch(DbUpdateConcurrencyException){return Results.Json(new ClientApiError("记录已在其他端更新，请刷新核对后再保存。"),statusCode:409);}
        catch(KeyNotFoundException ex){return Results.Json(new ClientApiError(ex.Message),statusCode:404);}
        catch(Exception ex)when(ex is ArgumentException or InvalidOperationException or InvalidDataException){return Results.Json(new ClientApiError(ex.Message),statusCode:400);}
    }
}
public static class ClientApiEndpoints
{
    public static void MapClientApi(this WebApplication app,string databasePath,string dataDirectory)
    {
        var api=app.MapGroup("/api/v1").AddEndpointFilter<ClientApiErrors>();
        api.MapGet("/info",(ClientApiIdentity identity)=>identity.Info);
        api.MapPost("/sync/backup",async(SyncProtectionRequest request,IWorkbenchService service,DeskDbContext db,CancellationToken ct)=>{
            if(request.OperationId==Guid.Empty)throw new ArgumentException("同步编号无效。");
            var path=Path.Combine(dataDirectory,"Backups",$"sync-before-{request.OperationId:N}.db");
            if(!File.Exists(path)){
                var result=await service.CreateBackup(path,ct);
                if(!result.Succeeded)throw new InvalidOperationException(result.Error??"电脑备份未完成，同步已停止。");
                await File.WriteAllTextAsync(path+".manifest.json",JsonSerializer.Serialize(new{ProductId="PersonalOrderDesk",Schema=(await db.Database.GetAppliedMigrationsAsync(ct)).Last(),CreatedAt=DateTimeOffset.UtcNow,SyncOperation=request.OperationId}),ct);
            }
            if(!File.Exists(path+".manifest.json"))throw new InvalidOperationException("电脑同步备份清单缺失，同步已停止。");
            return new SyncProtectionReceipt(request.OperationId,Path.GetFileName(path),File.GetCreationTimeUtc(path));
        });
        api.MapPost("/session",(SessionRequest request,HttpContext context,NetworkAccessService network,ClientApiSessions sessions)=>{
            if(!NetworkAccessService.IsLocal(context)&&!network.VerifyPassword(context.Connection.RemoteIpAddress?.ToString()??"unknown",request.Password??""))
                return Results.Json(new ClientApiError("密码错误或尝试次数过多，请稍后重试。"),statusCode:401);
            return Results.Ok(sessions.Issue());
        });
        api.MapGet("/state",(IWorkspaceGateway g,CancellationToken ct)=>g.State(ct));
        api.MapGet("/editor",(IWorkspaceGateway g,CancellationToken ct)=>g.Editor(null,ct));
        api.MapGet("/orders/{id:guid}",(Guid id,IWorkspaceGateway g,CancellationToken ct)=>g.Order(id,ct));
        api.MapGet("/orders/{id:guid}/editor",(Guid id,IWorkspaceGateway g,CancellationToken ct)=>g.Editor(id,ct));
        api.MapPost("/orders",(WorkItemSaveRequestDto r,IWorkspaceGateway g,CancellationToken ct)=>g.SaveOrder(null,r,ct));
        api.MapPost("/orders/{id:guid}",(Guid id,WorkItemSaveRequestDto r,IWorkspaceGateway g,CancellationToken ct)=>g.SaveOrder(id,r,ct));
        api.MapPost("/orders/{id:guid}/additional",(Guid id,WorkItemSaveRequestDto r,IWorkspaceGateway g,CancellationToken ct)=>g.AddWork(id,r,ct));
        api.MapPost("/orders/{id:guid}/progress",(Guid id,UpdateProgressRequestDto r,IWorkspaceGateway g,CancellationToken ct)=>g.Progress(id,r,ct));
        api.MapPost("/orders/{id:guid}/status",(Guid id,WorkStatusChangeRequestDto r,IWorkspaceGateway g,CancellationToken ct)=>g.Status(id,r,ct));
        api.MapPost("/orders/{id:guid}/archive",(Guid id,ArchiveRequestDto r,IWorkspaceGateway g,CancellationToken ct)=>g.Archive(id,r,ct));
        api.MapPost("/orders/{id:guid}/delete",async(Guid id,VersionRequest r,IWorkspaceGateway g,CancellationToken ct)=>{await g.Delete(id,r.ExpectedVersion,ct);return Results.Ok(true);});
        api.MapPost("/recycle/{id:guid}/restore",async(Guid id,VersionRequest r,IWorkspaceGateway g,CancellationToken ct)=>{await g.Restore(id,r.ExpectedVersion,ct);return Results.Ok(true);});
        api.MapPost("/payments",(CreatePaymentRequestDto r,IWorkspaceGateway g,CancellationToken ct)=>g.Payment(r,ct));
        api.MapPost("/payments/{id:guid}/reverse",(Guid id,ReverseTransactionRequestDto r,IWorkspaceGateway g,CancellationToken ct)=>g.ReversePayment(id,r,ct));
        api.MapPost("/orders/{id:guid}/expenses",(Guid id,QuickExpenseRequest r,IWorkspaceGateway g,CancellationToken ct)=>g.Expense(id,r,ct));
        api.MapPost("/expenses/{id:guid}/reverse",(Guid id,ReverseExpenseRequestDto r,IWorkspaceGateway g,CancellationToken ct)=>g.ReverseExpense(id,r,ct));
        api.MapPost("/orders/{id:guid}/ship",(Guid id,MarkShippedRequestDto r,IWorkspaceGateway g,CancellationToken ct)=>g.Ship(id,r,ct));
        api.MapPost("/orders/{id:guid}/deliver",(Guid id,MarkDeliveredRequestDto r,IWorkspaceGateway g,CancellationToken ct)=>g.Deliver(id,r,ct));
        api.MapGet("/snapshot",async(ClientApiIdentity identity,DeskDbContext db,CancellationToken ct)=>{
            var migrations=(await db.Database.GetAppliedMigrationsAsync(ct)).ToArray();
            var bytes=await PortableSnapshot.Create(databasePath,identity.Info,migrations,Path.Combine(dataDirectory,"Temp","ClientSnapshots"),ct);
            return Results.File(bytes,"application/octet-stream","workbench.pcsnapshot");
        });
        api.MapGet("/sync/{id:guid}/backup",async(Guid id,ClientApiIdentity identity,DeskDbContext db,CancellationToken ct)=>{
            var path=Path.Combine(dataDirectory,"Backups",$"sync-before-{id:N}.db");
            if(!File.Exists(path)||!File.Exists(path+".manifest.json"))throw new KeyNotFoundException("该次同步保护备份不存在。");
            return Results.File(await PortableSnapshot.Create(path,identity.Info,(await db.Database.GetAppliedMigrationsAsync(ct)).ToArray(),Path.Combine(dataDirectory,"Temp","ClientSnapshots"),ct),"application/octet-stream","pc-before.pcsnapshot");
        });
    }
}
