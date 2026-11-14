using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Client;

public static class ServerAddressPolicy
{
    public static Uri Parse(string address)
    {
        if(!Uri.TryCreate(address.Trim().TrimEnd('/')+"/",UriKind.Absolute,out var uri)||uri.Scheme is not("http" or "https")||!string.IsNullOrEmpty(uri.UserInfo)||uri.AbsolutePath!="/"||!string.IsNullOrEmpty(uri.Query)||!string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("请输入电脑的局域网地址，例如 http://192.168.1.10:5059。");
        if(!IPAddress.TryParse(uri.Host,out var ip))throw new ArgumentException("请使用电脑显示的局域网IP地址。");
        if(ip.IsIPv4MappedToIPv6)ip=ip.MapToIPv4();var b=ip.GetAddressBytes();
        if(!IPAddress.IsLoopback(ip)&&!(b.Length==4&&(b[0]==10||(b[0]==172&&b[1]>=16&&b[1]<=31)||(b[0]==192&&b[1]==168))))throw new ArgumentException("仅连接本机或局域网电脑。");
        return uri;
    }
}
public sealed class HttpWorkspaceGateway : IWorkspaceGateway,IDisposable
{
    private readonly HttpClient client;
    public HttpWorkspaceGateway(Uri address,string token)
    {client=new(new HttpClientHandler{AllowAutoRedirect=false}){BaseAddress=address,Timeout=TimeSpan.FromSeconds(20)};client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);}
    public static async Task<SessionResponse> Login(Uri address,string password,CancellationToken ct=default)
    {
        using var client=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){BaseAddress=address,Timeout=TimeSpan.FromSeconds(12)};
        using var response=await client.PostAsJsonAsync("api/v1/session",new SessionRequest(password),DeskJson.Options,ct);
        await Check(response,ct);var result=await response.Content.ReadFromJsonAsync<SessionResponse>(DeskJson.Options,ct)??throw new InvalidDataException("电脑返回的连接信息无效。");
        if(result.Server.ProductId!="PersonalOrderDesk"||result.Server.ApiVersion!=1||!Guid.TryParse(result.Server.InstanceId,out _))throw new InvalidDataException("该地址不是兼容的个人接单工作台。");return result;
    }
    private static async Task Check(HttpResponseMessage response,CancellationToken ct)
    {
        if(response.IsSuccessStatusCode)return;
        ClientApiError? error=null;try{error=await response.Content.ReadFromJsonAsync<ClientApiError>(DeskJson.Options,ct);}catch(System.Text.Json.JsonException){}
        throw new InvalidOperationException(error?.Message??(response.StatusCode==HttpStatusCode.NotFound?"电脑服务版本不兼容，或记录不存在。":"电脑暂时无法完成操作，请稍后重试。"));
    }
    private async Task<T> Get<T>(string path,CancellationToken ct){using var r=await client.GetAsync(path,ct);await Check(r,ct);return await r.Content.ReadFromJsonAsync<T>(DeskJson.Options,ct)??throw new InvalidDataException("电脑没有返回有效数据。");}
    private async Task<T> Post<T>(string path,object data,CancellationToken ct){using var r=await client.PostAsJsonAsync(path,data,DeskJson.Options,ct);await Check(r,ct);return await r.Content.ReadFromJsonAsync<T>(DeskJson.Options,ct)??throw new InvalidDataException("电脑没有返回有效数据。");}
    public async Task<byte[]> Snapshot(CancellationToken ct=default)
    {
        using var r=await client.GetAsync("api/v1/snapshot",HttpCompletionOption.ResponseHeadersRead,ct);await Check(r,ct);
        if(r.Content.Headers.ContentLength>PortableSnapshot.MaxBytes)throw new InvalidDataException("电脑副本超过大小限制。");
        await using var stream=await r.Content.ReadAsStreamAsync(ct);using var buffer=new MemoryStream();var block=new byte[81920];int n;
        while((n=await stream.ReadAsync(block,ct))>0){if(buffer.Length+n>PortableSnapshot.MaxBytes)throw new InvalidDataException("电脑副本超过大小限制。");await buffer.WriteAsync(block.AsMemory(0,n),ct);}return buffer.ToArray();
    }
    public Task<WorkspaceState> State(CancellationToken ct=default)=>Get<WorkspaceState>("api/v1/state",ct);
    public Task<ClientOrder> Order(Guid id,CancellationToken ct=default)=>Get<ClientOrder>($"api/v1/orders/{id}",ct);
    public Task<WorkItemEditorDto> Editor(Guid? id=null,CancellationToken ct=default)=>Get<WorkItemEditorDto>(id.HasValue?$"api/v1/orders/{id}/editor":"api/v1/editor",ct);
    public Task<WorkItemDetailDto> SaveOrder(Guid? id,WorkItemSaveRequestDto r,CancellationToken ct=default)=>Post<WorkItemDetailDto>(id.HasValue?$"api/v1/orders/{id}":"api/v1/orders",r,ct);
    public Task<WorkItemDetailDto> AddWork(Guid id,WorkItemSaveRequestDto r,CancellationToken ct=default)=>Post<WorkItemDetailDto>($"api/v1/orders/{id}/additional",r,ct);
    public Task<WorkItemDetailDto> Progress(Guid id,UpdateProgressRequestDto r,CancellationToken ct=default)=>Post<WorkItemDetailDto>($"api/v1/orders/{id}/progress",r,ct);
    public Task<WorkItemDetailDto> Status(Guid id,WorkStatusChangeRequestDto r,CancellationToken ct=default)=>Post<WorkItemDetailDto>($"api/v1/orders/{id}/status",r,ct);
    public Task<WorkItemDetailDto> Archive(Guid id,ArchiveRequestDto r,CancellationToken ct=default)=>Post<WorkItemDetailDto>($"api/v1/orders/{id}/archive",r,ct);
    public async Task Delete(Guid id,long version,CancellationToken ct=default)=>await Post<bool>($"api/v1/orders/{id}/delete",new VersionRequest(version),ct);
    public async Task Restore(Guid id,long version,CancellationToken ct=default)=>await Post<bool>($"api/v1/recycle/{id}/restore",new VersionRequest(version),ct);
    public Task<PaymentTransactionDto> Payment(CreatePaymentRequestDto r,CancellationToken ct=default)=>Post<PaymentTransactionDto>("api/v1/payments",r,ct);
    public Task<ExpenseGroupDto> Expense(Guid id,QuickExpenseRequest r,CancellationToken ct=default)=>Post<ExpenseGroupDto>($"api/v1/orders/{id}/expenses",r,ct);
    public Task<PaymentTransactionDto> ReversePayment(Guid id,ReverseTransactionRequestDto r,CancellationToken ct=default)=>Post<PaymentTransactionDto>($"api/v1/payments/{id}/reverse",r,ct);
    public Task<ExpenseDto> ReverseExpense(Guid id,ReverseExpenseRequestDto r,CancellationToken ct=default)=>Post<ExpenseDto>($"api/v1/expenses/{id}/reverse",r,ct);
    public Task<ShippingDraftDto> Ship(Guid id,MarkShippedRequestDto r,CancellationToken ct=default)=>Post<ShippingDraftDto>($"api/v1/orders/{id}/ship",r,ct);
    public Task<ShippingDraftDto> Deliver(Guid id,MarkDeliveredRequestDto r,CancellationToken ct=default)=>Post<ShippingDraftDto>($"api/v1/orders/{id}/deliver",r,ct);
    public void Dispose()=>client.Dispose();
}
