using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using PersonalOrderDesk.Services;
using PersonalOrderDesk.Domain;

namespace PersonalOrderDesk.Client;

public sealed class LocalWorkspaceGateway(IServiceScopeFactory scopes, bool readOnly = false) : IWorkspaceGateway
{
    private async Task<T> Read<T>(Func<IServiceProvider,Task<T>> action)
    { await using var scope = scopes.CreateAsyncScope(); return await action(scope.ServiceProvider); }
    private Task<T> Write<T>(Func<IServiceProvider,Task<T>> action)
    { if (readOnly) throw new InvalidOperationException("这是电脑只读副本。请连接电脑后修改，或切换到本地独立账本。"); return Read(action); }
    private static IWorkbenchService W(IServiceProvider p) => p.GetRequiredService<IWorkbenchService>();
    private static IFinanceService F(IServiceProvider p) => p.GetRequiredService<IFinanceService>();
    public Task<WorkspaceState> State(CancellationToken ct=default) => Read(async p => {
        var w=W(p); var orders=new List<WorkItemRowDto>();
        for(var page=1;;page++) { var part=await w.GetList(new(IncludeArchived:true,Page:page,PageSize:200),ct); orders.AddRange(part.Items); if(orders.Count>=part.TotalCount||part.Items.Count==0) break; }
        var money=(await F(p).GetOverview(new(),ct)).WorkItems.Select(x=>new OrderMoneySummary(x.WorkItemId,x.BusinessOrderNo,x.ProjectName,x.OrderAmountCents,x.NetReceivedCents,x.TotalCostCents,x.OutstandingCents)).ToArray();
        var dashboard=await w.GetDashboard(ct); return new WorkspaceState(p.GetRequiredService<IBusinessClock>().Today,orders,money,await w.GetRecycleBin(ct),dashboard.Sections);
    });
    public Task<ClientOrder> Order(Guid id,CancellationToken ct=default) => Read(async p => {
        var detail=await W(p).GetDetail(id,ct)??throw new KeyNotFoundException("项目不存在或已在回收站。");
        var money=await F(p).GetWorkItemFinance(id,ct)??throw new KeyNotFoundException("项目不存在。");
        ShippingDraftDto? ship=null;
        if(detail.WorkItem.ShippingRequirement==ShippingRequirement.Required) {
            try { ship=await p.GetRequiredService<IShippingService>().GetDraft(id,ct); }
            catch(InvalidOperationException ex) when(ex.Message.Contains("没有物流记录",StringComparison.Ordinal)) { }
        }
        return new ClientOrder(detail,money,ship);
    });
    public Task<WorkItemEditorDto> Editor(Guid? id=null,CancellationToken ct=default)=>Read(p=>W(p).GetEditor(id,ct));
    public Task<WorkItemDetailDto> SaveOrder(Guid? id,WorkItemSaveRequestDto request,CancellationToken ct=default)=>Write(p=>id.HasValue?W(p).Update(id.Value,request,ct):W(p).Create(request,ct));
    public Task<WorkItemDetailDto> AddWork(Guid id,WorkItemSaveRequestDto request,CancellationToken ct=default)=>Write(p=>W(p).CreateAdditionalWork(id,request,ct));
    public Task<WorkItemDetailDto> Progress(Guid id,UpdateProgressRequestDto request,CancellationToken ct=default)=>Write(p=>W(p).UpdateProgress(id,request,ct));
    public Task<WorkItemDetailDto> Status(Guid id,WorkStatusChangeRequestDto request,CancellationToken ct=default)=>Write(p=>W(p).ChangeStatus(id,request,ct));
    public Task<WorkItemDetailDto> Archive(Guid id,ArchiveRequestDto request,CancellationToken ct=default)=>Write(p=>W(p).Archive(id,request,ct));
    public async Task Delete(Guid id,long version,CancellationToken ct=default) => await Write(async p=>{await W(p).Delete(id,version,ct); return true;});
    public async Task Restore(Guid id,long version,CancellationToken ct=default) => await Write(async p=>{await W(p).RestoreDeleted(id,version,ct); return true;});
    public Task<PaymentTransactionDto> Payment(CreatePaymentRequestDto request,CancellationToken ct=default)=>Write(p=>F(p).CreateTransaction(request,ct));
    public Task<ExpenseGroupDto> Expense(Guid id,QuickExpenseRequest request,CancellationToken ct=default)=>Write(p=>F(p).SaveExpenseGroup(new(id,null,request.ExpectedVersion,
        string.IsNullOrWhiteSpace(request.Note)?"项目支出":request.Note,request.Category,null,
        [new(null,request.AmountCents,request.Date,request.Date.HasValue?DateKnowledge.Known:DateKnowledge.Unknown,ExpensePaymentStatus.Paid,request.Date,request.Date.HasValue?DateKnowledge.Known:DateKnowledge.Unknown,null)]),ct));
    public Task<PaymentTransactionDto> ReversePayment(Guid id,ReverseTransactionRequestDto request,CancellationToken ct=default)=>Write(p=>F(p).ReverseTransaction(id,request,ct));
    public Task<ExpenseDto> ReverseExpense(Guid id,ReverseExpenseRequestDto request,CancellationToken ct=default)=>Write(p=>F(p).ReverseExpense(id,request,ct));
    public Task<ShippingDraftDto> Ship(Guid id,MarkShippedRequestDto request,CancellationToken ct=default)=>Write(p=>p.GetRequiredService<IShippingService>().MarkShipped(id,request,ct));
    public Task<ShippingDraftDto> Deliver(Guid id,MarkDeliveredRequestDto request,CancellationToken ct=default)=>Write(p=>p.GetRequiredService<IShippingService>().MarkDelivered(id,request,ct));
}

public static class LocalWorkspaceServices
{
    public static IServiceCollection AddLocalWorkspace(this IServiceCollection services,string path,bool readOnly=false)
    {
        var connection=new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource=path,ForeignKeys=true,Pooling=false,
            Mode=readOnly?Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly:Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate }.ToString();
        services.AddLogging(); services.AddDbContext<PersonalOrderDesk.Data.DeskDbContext>(o=>o.UseSqlite(connection));
        services.AddSingleton<IBusinessClock,SystemBusinessClock>(); services.AddScoped<WorkItemService>();
        services.AddScoped<IWorkbenchService,WorkbenchService>(); services.AddScoped<IFinanceService,FinanceService>(); services.AddScoped<IShippingService,ShippingService>();
        services.AddSingleton<IWorkspaceGateway>(p=>new LocalWorkspaceGateway(p.GetRequiredService<IServiceScopeFactory>(),readOnly)); return services;
    }
}
