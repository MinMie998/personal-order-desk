using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using PersonalOrderDesk.Client;
using PersonalOrderDesk.Domain;
using PersonalOrderDesk.Services;
using PersonalOrderDesk.Presentation;

namespace PersonalOrderDesk.SharedUI;

public partial class WorkbenchShell : IDisposable
{
    [Inject] public IWorkspaceSession Session {get;set;}=default!;
    [Inject] public IClientPlatform Platform {get;set;}=default!;
    private readonly CancellationTokenSource lifetime=new();
    private readonly Dictionary<string,string> NavItems=new(){["overview"]="概览",["orders"]="工作项",["money"]="收支结算",["shipping"]="物流履约",["recycle"]="回收站",["settings"]="连接与数据"};
    private WorkspaceState? _state;
    private ClientOrder? _detail;
    private string _page="overview",_detailTab="info",_search="",_statusFilter="",_moneySearch="";
    private bool _busy,_includeArchived,_dueOnly=true;
    private MobileRestoreReview? _restoreReview;
    private Task PreviewLocalRestore()=>Run(async()=>{var path=await Platform.PickBackupFile();if(path is null)return;_restoreReview=await Session.PreviewRestore(path);ShowDialog("restore");});
    private Task RestoreLocal()=>Run(async()=>{if(!_confirmAction||_restoreReview is null)throw new ArgumentException("请核对并确认恢复。");await Session.RestoreBackup(_restoreReview.Id);_dialog=null;_detail=null;await LoadState();_notice="手机账本已恢复，恢复前保护备份已保留，可以导出留存。";});
    private Task ExportRestoreBackup()=>Run(async()=>await Platform.ShareFile(await Session.ExportRestoreProtection()));
    private SyncReview? _syncReview;
    private Dictionary<string,string> _syncChoices=new();
    private AvailableUpdate? _update;
    private Task CheckUpdate()=>Run(async()=>{_update=await GitHubUpdates.Check(Session.IsMobile,lifetime.Token);_notice=_update is null?"当前没有更高版本的发布包。":"发现新版本，可下载并校验安装包。";});
    private Task DownloadUpdate()=>Run(async()=>{if(_update is null)return;var path=await GitHubUpdates.Download(_update,Path.Combine(Path.GetTempPath(),"PersonalOrderDesk-updates"),lifetime.Token);await Platform.OpenUpdate(path);_notice=Session.IsMobile?"校验通过，已交给系统安装器；覆盖安装保留独立账本。":"校验通过，已打开更新压缩包。先在设置中备份并退出服务，再解压到新版本目录；保留原数据目录与本机数据登记文件。";});
    private string? _error,_notice,_dialog,_dialogError,_receiptAnimation;
    private string? _progressCurrent,_progressNext,_serverAddress,_serverPassword;
    private string? _carrier,_tracking; private bool _noTracking;private DateOnly? _shipDate,_deliveredDate;
    private Guid? _editId,_additionalSource;private WorkItemEditorDto? _editBase;
    private string? _editName,_editNumber,_editCurrent,_editNext,_editPath,_editNotes,_editAmountReason;
    private ProjectType _editType;private DateOnly? _editReceived,_editDue;private decimal? _editQuote;
    private DueDateMode _editDueMode;private ShippingRequirement? _editShipping;private string _typeDefaults="";
    private decimal? _entryAmount;private DateOnly? _entryDate;private string? _entryNote,_paymentMethod,_customMethod;
    private string _expenseCategory="材料",_idempotency="";private bool _confirmAction;private string? _actionReason;
    private FinanceLedgerRow? _reverseRow;private WorkStatus _targetStatus;
    private IReadOnlyList<FinanceLedgerRow> Ledger=>_detail is null?[]:FinanceLedgerRow.From(_detail.Money);
    private WorkItemNoteSections NoteSections=>WorkItemNoteSections.From(_detail?.Detail.WorkItem.Notes,_detail?.Detail.WorkItem.SourceKind??SourceKind.Manual);
    private IEnumerable<WorkItemRowDto> VisibleOrders=>(_state?.Orders??[]).Where(x=>(_includeArchived||!x.IsArchived)&&(_statusFilter==""||x.Status.ToString()==_statusFilter)&&Match(_search,x.BusinessOrderNo,x.ProjectName,x.CurrentWork,x.NextStep));
    private IEnumerable<OrderMoneySummary> VisibleMoney=>(_state?.Money??[]).Where(x=>(!_dueOnly||x.Due>0||x.Due is null)&&Match(_moneySearch,x.Number,x.Name));
    private UnsettledCopyList CopyList=>UnsettledCopyList.From(VisibleMoney);
    private IReadOnlyList<WorkItemRowDto> TodoItems=>(_state?.Sections??[]).SelectMany(x=>x.Items).DistinctBy(x=>x.Id).Take(8).ToArray();
    private static string NextAction(WorkItemRowDto row)=>row.Status==WorkStatus.Completed?(row.OutstandingCents>0?$"待收 {Money(row.OutstandingCents)}":row.FulfillmentState):row.NextStep??StatusName(row.Status);
    private bool QuoteChanged=>_editId.HasValue&&(_editQuote.HasValue?(long?)decimal.ToInt64(decimal.Round(_editQuote.Value*100,0)):null)!=_editBase?.OrderAmountCents;
    private string DialogTitle=>_dialog switch{"restore"=>"恢复手机账本","sync"=>"核对双向同步","order"=>_editId.HasValue?"编辑订单":_additionalSource.HasValue?"追加工作":"新增订单","receipt"=>"记收款","refund"=>"记退款","expense"=>"记支出","delete"=>"删除工作项","reverse"=>"撤销记录","status"=>"调整状态",_=>"工作台"};
    private IReadOnlyList<WorkStatus> AllowedStatuses=>_detail?.Detail.WorkItem.Status switch{
        WorkStatus.Cancelled=>_detail.Detail.WorkItem.CancelledFromStatus==CancelledStatus.Completed?[WorkStatus.Completed]:[WorkStatus.NotStarted,WorkStatus.InProgress,WorkStatus.NeedsRework],
        WorkStatus.Completed=>[WorkStatus.InProgress,WorkStatus.NeedsRework,WorkStatus.Cancelled],
        WorkStatus.NotStarted=>[WorkStatus.InProgress,WorkStatus.Completed,WorkStatus.Cancelled],
        WorkStatus.InProgress=>[WorkStatus.Completed,WorkStatus.NeedsRework,WorkStatus.Cancelled],
        _=>[WorkStatus.InProgress,WorkStatus.Completed,WorkStatus.Cancelled]};
    protected override async Task OnInitializedAsync()
    {
        _busy=true;
        try{await Session.Initialize();_serverAddress=Session.Profile.ServerAddress;await LoadState();}
        catch(Exception ex){_error=Explain(ex);}
        finally{_busy=false;}
    }
    private async Task Run(Func<Task> action)
    {
        if(_busy)return;_busy=true;_error=null;_dialogError=null;_notice=null;
        try{await action();}catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}catch(Exception ex){if(_dialog is not null)_dialogError=Explain(ex);else _error=Explain(ex);}finally{_busy=false;}
    }
    private static string Explain(Exception ex)=>ex switch{
        HttpRequestException=>"暂时无法连接电脑，请检查电脑服务、局域网和地址；当前账本没有自动切换。",
        TaskCanceledException=>"连接超时，请稍后重试。当前账本没有自动切换。",
        Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException=>"记录已在其他端更新，请刷新核对后再保存。",
        _=>ex.Message};
    private async Task LoadState()=>_state=await Session.Gateway.State(lifetime.Token);
    private Task Refresh()=>Run(LoadState);
    private void Navigate(string page){if(_busy)return;_page=page;_detail=null;_error=null;_notice=null;}
    private void BackToList(){_detail=null;_page="orders";}
    private void ClearSearch(){_search="";_statusFilter="";_includeArchived=false;}
    private static bool Match(string term,params string?[] fields)=>string.IsNullOrWhiteSpace(term)||fields.Any(x=>x?.Contains(term.Trim(),StringComparison.OrdinalIgnoreCase)==true);
    private async Task LoadOrder(Guid id,string tab="info")
    {
        _detail=await Session.Gateway.Order(id,lifetime.Token);_detailTab=tab;_page="orders";
        _progressCurrent=_detail.Detail.WorkItem.CurrentWork;_progressNext=_detail.Detail.WorkItem.NextStep;
        _carrier=_detail.Shipping?.Carrier;_tracking=_detail.Shipping?.TrackingNumber;_noTracking=_detail.Shipping?.NoTrackingNumber==true;
        _shipDate=_state?.Today??DateOnly.FromDateTime(DateTime.Today);_deliveredDate=_shipDate;
    }
    private Task OpenOrder(Guid id,string tab="info")=>Run(()=>LoadOrder(id,tab));
    private void ShowDialog(string kind){_dialog=kind;_dialogError=null;_confirmAction=false;_actionReason="";}
    private void CloseDialog(){if(_busy)return;_dialog=null;_dialogError=null;}
    private void DialogKey(KeyboardEventArgs e){if(e.Key=="Escape")CloseDialog();}
    private Task SaveProgress()=>Run(async()=>{var id=_detail!.Detail.WorkItem.Id!.Value;await Session.Gateway.Progress(id,new(_detail.Detail.WorkItem.Version,_progressCurrent,_progressNext,_detail.Detail.WorkItem.ProjectPath),lifetime.Token);await LoadState();await LoadOrder(id);_notice="进度已保存。";});
    private Task SetStatus(WorkStatus status)=>Run(async()=>{var w=_detail!.Detail.WorkItem;await Session.Gateway.Status(w.Id!.Value,new(status,w.Version),lifetime.Token);await LoadState();await LoadOrder(w.Id.Value);_notice="工作状态已更新。";});
    private void OpenStatusDialog(){ShowDialog("status");_targetStatus=AllowedStatuses[0];}
    private Task SaveStatus()=>Run(async()=>{if(_targetStatus==WorkStatus.Cancelled&&!_confirmAction)throw new ArgumentException("请勾选取消确认。");var w=_detail!.Detail.WorkItem;await Session.Gateway.Status(w.Id!.Value,new(_targetStatus,w.Version,_actionReason,w.Status==WorkStatus.Cancelled?_targetStatus:null),lifetime.Token);_dialog=null;await LoadState();await LoadOrder(w.Id.Value);_notice="状态已更新，历史记录保留。";});
    private Task Archive(bool archive)=>Run(async()=>{var w=_detail!.Detail.WorkItem;await Session.Gateway.Archive(w.Id!.Value,new(archive,w.Version),lifetime.Token);await LoadState();await LoadOrder(w.Id.Value);_notice=archive?"已归档。":"已取消归档。";});
    private Task OpenOrderEditor(Guid? id=null)=>Run(async()=>{_additionalSource=null;await LoadEditor(id);});
    private Task OpenAdditional()=>Run(async()=>{var w=_detail!.Detail.WorkItem;_additionalSource=w.Id;await LoadEditor(null);_editNumber=w.BusinessOrderNo;});
    private async Task LoadEditor(Guid? id)
    {
        _editBase=await Session.Gateway.Editor(id,lifetime.Token);_editId=id;_editName=_editBase.ProjectName;_editNumber=_editBase.BusinessOrderNo;
        _editType=id.HasValue?_editBase.ProjectType:ProjectType.Simulation;_editReceived=_editBase.ReceivedDate;_editQuote=_editBase.OrderAmountCents/100m;
        _editDueMode=_editBase.DueDateMode;_editDue=_editBase.SpecifiedDueDate;_editCurrent=_editBase.CurrentWork;_editNext=_editBase.NextStep;_editPath=_editBase.ProjectPath;
        _editNotes=WorkItemNoteSections.From(_editBase.Notes,_editBase.SourceKind).CurrentNotes;_editAmountReason="";_editShipping=null;_typeDefaults="";ShowDialog("order");
    }
    private Task SaveOrder()=>Run(async()=>{
        if(string.IsNullOrWhiteSpace(_editName))throw new ArgumentException("请填写项目名称。");
        if(_editQuote is {} quote&&(quote<0||decimal.Round(quote,2)!=quote))throw new ArgumentException("订单金额不能小于0，最多两位小数。");
        if(_editDueMode==DueDateMode.Manual&&!_editDue.HasValue)throw new ArgumentException("请选择约定交付日期。");
        bool? apply=null;if(_editId.HasValue&&_editBase!.ProjectType!=_editType)apply=_typeDefaults switch{"apply"=>true,"keep"=>false,_=>throw new ArgumentException("项目类型改变后，请选择默认值处理方式。")};
        var notes=_editId.HasValue?WorkItemNoteSections.MergeCurrentNotes(_editNotes,_editBase!.Notes,_editBase.SourceKind):_editNotes;
        var request=new WorkItemSaveRequestDto(_editId.HasValue?_editBase!.Version:null,_editNumber,_editName.Trim(),_editType,null,_editShipping,_editReceived,_editDueMode,
            _editDueMode==DueDateMode.Manual?_editDue:null,_editCurrent,_editNext,_editQuote.HasValue?decimal.ToInt64(_editQuote.Value*100):null,_editPath,notes,apply,_editAmountReason);
        var saved=_additionalSource.HasValue?await Session.Gateway.AddWork(_additionalSource.Value,request,lifetime.Token):await Session.Gateway.SaveOrder(_editId,request,lifetime.Token);
        _dialog=null;await LoadState();await LoadOrder(saved.WorkItem.Id!.Value);_notice="订单已保存。";
    });
    private void OpenEntry(string kind){ShowDialog(kind);_entryAmount=null;_entryDate=kind=="expense"?null:_state?.Today;_entryNote="";_paymentMethod=Session.Profile.PaymentMethod??"";_customMethod="";_expenseCategory="材料";_idempotency=Guid.NewGuid().ToString("N");}
    private static long Cents(decimal? amount){if(amount is null||amount<=0||decimal.Round(amount.Value,2)!=amount.Value)throw new ArgumentException("金额须大于0，最多两位小数。");return decimal.ToInt64(amount.Value*100);}
    private Task SaveEntry()=>Run(async()=>{
        var kind=_dialog!;var w=_detail!.Detail.WorkItem;var cents=Cents(_entryAmount);
        if(kind=="expense")await Session.Gateway.Expense(w.Id!.Value,new(w.Version,cents,_entryDate,_expenseCategory,_entryNote),lifetime.Token);
        else {var method=_paymentMethod=="其他"&&!string.IsNullOrWhiteSpace(_customMethod)?_customMethod.Trim():_paymentMethod;
            await Session.Gateway.Payment(new(kind=="refund"?TransactionType.Refund:TransactionType.Receipt,cents,_entryDate,_entryDate.HasValue?DateKnowledge.Known:DateKnowledge.Unknown,method,null,_entryNote,_idempotency,[new(w.Id!.Value,cents)]),lifetime.Token);
            await Session.RememberMethod(_paymentMethod);
        }
        _dialog=null;await LoadState();await LoadOrder(w.Id!.Value,"money");_notice=kind=="refund"?"退款已记录；若是降价退差价，可编辑最终订单金额。":kind=="expense"?"支出已记录，利润已更新。":"收款已记录。";
        if(kind=="receipt"){_receiptAnimation=Money(cents);_ = ClearReceiptLater();}
    });
    private async Task ClearReceiptLater(){try{await Task.Delay(2400,lifetime.Token);await InvokeAsync(()=>{_receiptAnimation=null;StateHasChanged();});}catch(OperationCanceledException){}catch(ObjectDisposedException){}}
    private Task DeleteOrder()=>Run(async()=>{if(!_confirmAction)throw new ArgumentException("请先核对并确认。");var w=_detail!.Detail.WorkItem;await Session.Gateway.Delete(w.Id!.Value,w.Version,lifetime.Token);_dialog=null;_detail=null;_page="orders";await LoadState();_notice="工作项已移入回收站，需要时可以恢复。";});
    private Task Restore(DeletedWorkItemDto row)=>Run(async()=>{await Session.Gateway.Restore(row.Id,row.Version,lifetime.Token);await LoadState();await LoadOrder(row.Id);_notice="已恢复，原状态和关联记录保留。";});
    private void OpenReverse(FinanceLedgerRow row){_reverseRow=row;ShowDialog("reverse");}
    private Task Reverse()=>Run(async()=>{if(!_confirmAction||string.IsNullOrWhiteSpace(_actionReason))throw new ArgumentException("请填写原因并确认撤销。");var w=_detail!.Detail.WorkItem;var row=_reverseRow!;if(row.Transaction is not null)await Session.Gateway.ReversePayment(row.Id,new(_actionReason),lifetime.Token);else await Session.Gateway.ReverseExpense(row.Id,new(_actionReason,w.Version),lifetime.Token);_dialog=null;await LoadState();await LoadOrder(w.Id!.Value,"money");_notice="记录已撤销，历史内容保留。";});
    private Task Ship()=>Run(async()=>{var w=_detail!.Detail.WorkItem;if(_shipDate is null)throw new ArgumentException("请选择实际寄出日期。");await Session.Gateway.Ship(w.Id!.Value,new(w.Version,_shipDate.Value,_carrier,_noTracking?null:_tracking,_noTracking),lifetime.Token);await LoadState();await LoadOrder(w.Id.Value,"shipping");_notice="寄出信息已记录。";});
    private Task Deliver()=>Run(async()=>{var w=_detail!.Detail.WorkItem;if(_deliveredDate is null)throw new ArgumentException("请选择实际签收日期。");await Session.Gateway.Deliver(w.Id!.Value,new(w.Version,_deliveredDate.Value),lifetime.Token);await LoadState();await LoadOrder(w.Id.Value,"shipping");_notice="已记录签收。";});
    private Task CopyText(string? text)=>Run(async()=>{if(string.IsNullOrWhiteSpace(text))throw new ArgumentException("没有可复制内容。");await Platform.CopyText(text);_notice="已复制。";});
    private Task CopyUnsettled()=>Run(async()=>{var list=CopyList;if(list.LineCount==0)throw new ArgumentException("当前没有可复制的未结款记录。");await Platform.CopyText(list.Text);_notice=$"已复制 {list.LineCount} 个编号的未结款清单。";});
    private async Task ChangedSource(Func<Task> action){await action();_state=null;_detail=null;await LoadState();_serverAddress=Session.Profile.ServerAddress;_notice="数据来源已切换，原账本保留。";}
    private Task UseLocal()=>Run(()=>ChangedSource(Session.UseLocal));
    private Task UseSnapshot()=>Run(()=>ChangedSource(Session.UsePcSnapshot));
    private Task ConnectPc()=>Run(async()=>{await ChangedSource(()=>Session.Connect(_serverAddress??"",_serverPassword??""));_serverPassword="";});
    private Task SyncPc()=>Run(async()=>{_syncReview=await Session.PreviewSync();if(_syncReview is null){if(Session.Profile.Mode==WorkspaceMode.PcSnapshot)await LoadState();_notice="电脑副本已更新；同步保护备份已保留。可离线修改，下次同步时合并两端改动。";}else{_syncChoices=_syncReview.Conflicts.ToDictionary(x=>x.Id,_=>"");ShowDialog("sync");}});
    private Task CommitSync()=>Run(async()=>{if(!_confirmAction||_syncReview is null||_syncChoices.Values.Any(x=>x is not("pc" or "phone")))throw new ArgumentException("请核对全部冲突并勾选确认。");await Session.CommitSync(_syncChoices);_dialog=null;_detail=null;await LoadState();_notice="双向同步完成，两端备份和合并记录已保留；手机独立账本继续单独保存。";});
    private Task BackupLocal()=>Run(async()=>{var path=await Session.Backup();await Platform.ShareFile(path);_notice="本地备份已创建。";});
    private Task ExportSyncBackup()=>Run(async()=>{await Platform.ShareFile(await Session.ExportSyncProtection());_notice="同步前的两端账本与记录已打包，可保存到应用外。";});
    private Task OpenAdvanced(string path)=>Run(()=>Platform.OpenWeb(path));
    private static string Money(long? cents)=>cents is null?"待补金额":$"¥{cents.Value/100m:N2}";
    private static string StatusName(WorkStatus s)=>s switch{WorkStatus.NotStarted=>"未开始",WorkStatus.InProgress=>"进行中",WorkStatus.Completed=>"已完成",WorkStatus.NeedsRework=>"需返工",_=>"已取消"};
    private static string StatusClass(WorkStatus s)=>s switch{WorkStatus.Completed=>"green",WorkStatus.InProgress=>"blue",WorkStatus.NeedsRework=>"orange",_=>"gray"};
    private static string TypeName(ProjectType t)=>t switch{ProjectType.Simulation=>"仿真",ProjectType.SchematicPcbDesign=>"原理图 / PCB",ProjectType.CodeReport=>"代码 / 报告",ProjectType.PhysicalProduction=>"实物制作",ProjectType.Other=>"其他",_=>"综合 / 未知"};
    private static string ShipName(ShipmentStatus s)=>s switch{ShipmentStatus.NotShipped=>"未寄出",ShipmentStatus.Shipped=>"已寄出",ShipmentStatus.Delivered=>"已签收",_=>"历史状态待核"};
    public void Dispose(){lifetime.Cancel();lifetime.Dispose();}
}
