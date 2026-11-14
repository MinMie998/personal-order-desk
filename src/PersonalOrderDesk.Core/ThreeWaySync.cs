using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PersonalOrderDesk.Data;
using PersonalOrderDesk.Domain;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Client;

public sealed record SyncTable(string[] Columns,Dictionary<string,Dictionary<string,string?>> Rows);
public sealed record SyncData(Dictionary<string,SyncTable> Tables,string? ConfigurationFingerprint=null);
public sealed record SyncConflict(string Id,string Project,string Field,string Baseline,string Pc,string Phone);
public sealed record SyncReview(Guid OperationId,string PcFingerprint,int PcChanges,int PhoneChanges,IReadOnlyList<SyncConflict> Conflicts,IReadOnlyList<string> Warnings);
public sealed record SyncMergeRequest(Guid OperationId,string InstanceId,SyncData Baseline,SyncData Phone);
public sealed record SyncCommitRequest(Guid OperationId,Dictionary<string,string> Choices,bool Confirm);
public sealed record SyncMergeResult(SyncData Data,IReadOnlyList<SyncConflict> Conflicts,int PcChanges,int PhoneChanges,IReadOnlyList<string> Warnings);

/// <summary>Three-way business-row merge. IDs survive copying; computed versions and deadlines are rebuilt on PC.</summary>
public static class ThreeWaySync
{
    public static readonly string[] Tables=["WorkItems","WorkItemRelations","PaymentTransactions","PaymentAllocations","ExpenseGroups","Expenses","Shipments","WorkStatusHistories","ActivityLogs"];
    private static readonly string[] StatusFields=["WorkStatus","CompletedDate","CompletedDateKnowledge","CompletedAt","CancelledFromStatus"];
    private static readonly HashSet<string> VolatileFields=["Version","UpdatedAt","EffectiveDueDate","LatestShipDate"];
    private static string Q(string name)=>"\""+name.Replace("\"","\"\"")+"\"";
    public static async Task<SyncData> Read(string path,CancellationToken ct=default)
    {
        await using var connection=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());await connection.OpenAsync(ct);
        await using var transaction=connection.BeginTransaction(deferred:true);return await Read(connection,transaction,ct);
    }
    private static async Task<SyncData> Read(SqliteConnection connection,SqliteTransaction transaction,CancellationToken ct)
    {
        var tables=new Dictionary<string,SyncTable>();
        foreach(var name in Tables){
            await using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="SELECT * FROM "+Q(name)+" ORDER BY Id";
            await using var reader=await command.ExecuteReaderAsync(ct);var columns=Enumerable.Range(0,reader.FieldCount).Select(reader.GetName).ToArray();var rows=new Dictionary<string,Dictionary<string,string?>>(StringComparer.Ordinal);
            while(await reader.ReadAsync(ct)){
                if(rows.Count>=100000)throw new InvalidDataException("账本记录超出同步上限。");
                var row=new Dictionary<string,string?>();for(int i=0;i<columns.Length;i++)row[columns[i]]=reader.IsDBNull(i)?null:Convert.ToString(reader.GetValue(i),CultureInfo.InvariantCulture);
                rows.Add(row["Id"]??throw new InvalidDataException("记录编号缺失。"),row);
            }tables.Add(name,new(columns,rows));
        }
        await using var settings=connection.CreateCommand();settings.Transaction=transaction;settings.CommandText="SELECT Key,Value,UpdatedAt FROM Settings ORDER BY Key";
        var configuration=new List<string?>();await using(var reader=await settings.ExecuteReaderAsync(ct))while(await reader.ReadAsync(ct))for(int i=0;i<3;i++)configuration.Add(reader.IsDBNull(i)?null:reader.GetString(i));
        return new(tables,Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(configuration))));
    }
    public static string Fingerprint(SyncData data)
    {
        var stable=Tables.Where(x=>x!="ActivityLogs").Select(t=>new{Table=t,Rows=data.Tables[t].Rows.OrderBy(x=>x.Key,StringComparer.Ordinal).Select(r=>r.Value.OrderBy(x=>x.Key,StringComparer.Ordinal))});
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{data.ConfigurationFingerprint,Tables=stable},DeskJson.Options)));
    }
    private static bool Equal(Dictionary<string,string?>? a,Dictionary<string,string?>? b,IEnumerable<string>? columns=null)
    {
        if(a is null||b is null)return a is null&&b is null;
        return (columns??a.Keys.Where(x=>!VolatileFields.Contains(x))).All(c=>a.GetValueOrDefault(c)==b.GetValueOrDefault(c));
    }
    private static Dictionary<string,string?> Clone(Dictionary<string,string?> row)=>new(row);
    private static string Value(Dictionary<string,string?>? row,string field)=>row is null?"记录不存在":field=="_status"?StatusSummary(row):field=="_record"?RecordSummary(row):field=="OrderAmountCents"&&long.TryParse(row.GetValueOrDefault(field),out var amount)?$"¥{amount/100m:0.00}":field is "IsArchived" or "IsDeleted"?(row.GetValueOrDefault(field)=="1"?"是":"否"):DisplayValue(row.GetValueOrDefault(field))??"未填写";
    private static string StatusSummary(Dictionary<string,string?> row)=>$"{DisplayValue(row.GetValueOrDefault("WorkStatus"))}；完成日期：{row.GetValueOrDefault("CompletedDate")??"未记录"}；日期情况：{row.GetValueOrDefault("CompletedDateKnowledge") switch{"Known"=>"已确认","Unknown"=>"历史未知",_=>"不适用"}}；完成时刻：{row.GetValueOrDefault("CompletedAt")??"未记录"}";
    private static string RecordSummary(Dictionary<string,string?> row)=>string.Join(" · ",new[]{"ProjectName","IsDeleted","WorkStatus","CurrentWork","NextStep","OrderAmountCents","ShipmentStatus","Type","AmountCents","Status","Carrier","TrackingNumber","ShippedDate","DeliveredDate","TransactionDate","PaymentMethod","ReversalReason","Notes"}.Where(row.ContainsKey).Select(k=>k is "AmountCents" or "OrderAmountCents"&&long.TryParse(row[k],out var cents)?$"¥{cents/100m:0.00}":k=="IsDeleted"?(row[k]=="1"?"已移入回收站":"正常保留"):DisplayValue(row[k])).Where(x=>!string.IsNullOrWhiteSpace(x)));
    private static string? DisplayValue(string? value)=>value switch{"NotStarted"=>"未开始","InProgress"=>"进行中","Completed"=>"已完成","NeedsRework"=>"需返工","Cancelled"=>"已取消","NotShipped"=>"未寄出","Shipped"=>"已寄出","Delivered"=>"已签收","Receipt"=>"收款","Refund"=>"退款","Active"=>"有效","Reversed"=>"已撤销",_=>value};
    private static bool RelatedChanged(SyncData baseline,SyncData changed,string id)
    {
        var groupIds=baseline.Tables["ExpenseGroups"].Rows.Values.Concat(changed.Tables["ExpenseGroups"].Rows.Values).Where(r=>r["WorkItemId"]==id).Select(r=>r["Id"]!).ToHashSet();
        var paymentIds=baseline.Tables["PaymentAllocations"].Rows.Values.Concat(changed.Tables["PaymentAllocations"].Rows.Values).Where(r=>r["WorkItemId"]==id).Select(r=>r["PaymentTransactionId"]!).ToHashSet();
        foreach(var t in new[]{"PaymentAllocations","PaymentTransactions","ExpenseGroups","Expenses","Shipments"}){
            bool Related(Dictionary<string,string?> r)=>r.GetValueOrDefault("WorkItemId")==id||(t=="Expenses"&&groupIds.Contains(r["ExpenseGroupId"]!))||(t=="PaymentTransactions"&&paymentIds.Contains(r["Id"]!));
            foreach(var key in baseline.Tables[t].Rows.Keys.Union(changed.Tables[t].Rows.Keys)){var b=baseline.Tables[t].Rows.GetValueOrDefault(key);var c=changed.Tables[t].Rows.GetValueOrDefault(key);if(((b is not null&&Related(b))||(c is not null&&Related(c)))&&!Equal(b,c))return true;}
        }return false;
    }
    private static string FieldName(string field)=>field switch{"_record"=>"整条记录","_status"=>"完成 / 取消状态与日期","ProjectName"=>"项目名称","BusinessOrderNo"=>"订单编号","CurrentWork"=>"当前进度","NextStep"=>"下一步","OrderAmountCents"=>"订单金额","Notes"=>"备注","ProjectPath"=>"工程路径","IsArchived"=>"归档状态","IsDeleted"=>"删除状态","DeletedAt"=>"删除时间","ReceivedDate"=>"接单日期","SpecifiedDueDate"=>"约定交付日","DueDateMode"=>"截止规则","ProjectType"=>"项目类型","ScheduleCategory"=>"工期类别","ShippingRequirement"=>"快递需求","CostConfirmationStatus"=>"成本确认",_=>"资料（"+field+"）"};
    private static void ValidateShape(SyncData expected,SyncData incoming)
    {
        if(incoming.Tables.Count!=Tables.Length||Tables.Any(t=>!incoming.Tables.ContainsKey(t)))throw new InvalidDataException("同步内容包含不支持的数据表。");
        foreach(var table in Tables){var schema=expected.Tables[table].Columns;if(!incoming.Tables[table].Columns.SequenceEqual(schema))throw new InvalidDataException("两端数据结构不一致，请更新客户端。");
            if(incoming.Tables[table].Rows.Count>100000)throw new InvalidDataException("同步记录过多。");
            foreach(var entry in incoming.Tables[table].Rows){if(entry.Value.Count!=schema.Length||schema.Any(c=>!entry.Value.ContainsKey(c))||entry.Value.GetValueOrDefault("Id")!=entry.Key||!Guid.TryParse(entry.Key,out _))throw new InvalidDataException("同步记录结构无效。");if(entry.Value.Values.Any(v=>v?.Length>2_000_000))throw new InvalidDataException("同步字段过大。");}
        }
    }
    public static SyncMergeResult Merge(SyncData baseline,SyncData pc,SyncData phone,IReadOnlyDictionary<string,string>? choices=null)
    {
        ValidateShape(pc,baseline);ValidateShape(pc,phone);
        var result=new Dictionary<string,SyncTable>();var conflicts=new List<SyncConflict>();var warnings=new List<string>();int pcChanges=0,phoneChanges=0;
        string Project(string table,string id,Dictionary<string,string?>? row){var itemId=table=="WorkItems"?id:row?.GetValueOrDefault("WorkItemId");var item=itemId is not null?phone.Tables["WorkItems"].Rows.GetValueOrDefault(itemId)??pc.Tables["WorkItems"].Rows.GetValueOrDefault(itemId):null;return item is null?table switch{"PaymentTransactions"=>"收退款流水","Expenses"=>"支出流水","ExpenseGroups"=>"支出项目",_=>"关联记录"}:$"{item.GetValueOrDefault("BusinessOrderNo")} · {item.GetValueOrDefault("ProjectName")}";}
        bool ChoosePhone(string table,string id,string field,Dictionary<string,string?>? b,Dictionary<string,string?>? p,Dictionary<string,string?>? m){var key=table+":"+id+":"+field;conflicts.Add(new(key,Project(table,id,m??p),FieldName(field),Value(b,field),Value(p,field),Value(m,field)));return choices?.GetValueOrDefault(key)=="phone";}
        foreach(var table in Tables){
            var before=baseline.Tables[table];var computer=pc.Tables[table];var mobile=phone.Tables[table];var rows=computer.Rows.ToDictionary(x=>x.Key,x=>Clone(x.Value));
            foreach(var id in before.Rows.Keys.Union(computer.Rows.Keys).Union(mobile.Rows.Keys)){
                var b=before.Rows.GetValueOrDefault(id);var p=computer.Rows.GetValueOrDefault(id);var m=mobile.Rows.GetValueOrDefault(id);
                if(table!="ActivityLogs"){if(!Equal(b,p))pcChanges++;if(!Equal(b,m))phoneChanges++;}
                if(b is not null&&m is null)throw new InvalidDataException("手机账本中有记录被直接移除，请恢复手机备份；请通过回收站删除订单。");
                if(Equal(b,m)||Equal(p,m))continue;
                if(b is not null&&m is not null){foreach(var immutable in new[]{"Id","CreatedAt","SourceKind","ImportBatchId"}.Where(b.ContainsKey))if(b[immutable]!=m[immutable])throw new InvalidDataException("手机修改了历史来源或记录身份，同步已停止。");}
                Dictionary<string,string?>? merged;
                if(table=="WorkItems"&&b is not null&&p is not null&&m is not null&&b["IsDeleted"]!=m["IsDeleted"]&&RelatedChanged(baseline,pc,id))merged=ChoosePhone(table,id,"_record",b,p,m)?Clone(m):Clone(p);
                else if(Equal(b,p))merged=m is null?null:Clone(m);
                else if(b is null||p is null||m is null||table is not "WorkItems"){
                    // An existing financial/shipping row is atomic; separate new IDs remain independent.
                    if(b is null&&p is null)merged=m is null?null:Clone(m);
                    else merged=ChoosePhone(table,id,"_record",b,p,m)?(m is null?null:Clone(m)):(p is null?null:Clone(p));
                }else{
                    merged=Clone(p);
                    if((b["IsDeleted"]!=m["IsDeleted"]||b["IsDeleted"]!=p["IsDeleted"])&&!Equal(p,m))merged=ChoosePhone(table,id,"_record",b,p,m)?Clone(m):Clone(p);
                    else{
                        bool phoneStatus=!Equal(b,m,StatusFields),pcStatus=!Equal(b,p,StatusFields);
                        if(phoneStatus){var usePhone=!pcStatus||Equal(p,m,StatusFields)||ChoosePhone(table,id,"_status",b,p,m);if(usePhone)foreach(var c in StatusFields)merged[c]=m[c];}
                        foreach(var c in before.Columns.Where(c=>!VolatileFields.Contains(c)&&!StatusFields.Contains(c))){if(b[c]==m[c])continue;if(b[c]==p[c]||p[c]==m[c]||ChoosePhone(table,id,c,b,p,m))merged[c]=m[c];}
                    }
                }
                if(merged is null){rows.Remove(id);continue;}
                if(!Equal(p,merged)){
                    if(merged.ContainsKey("Version"))merged["Version"]=(Math.Max(long.Parse(p?.GetValueOrDefault("Version")??"0"),long.Parse(m?.GetValueOrDefault("Version")??"0"))+1).ToString(CultureInfo.InvariantCulture);
                    if(merged.ContainsKey("UpdatedAt"))merged["UpdatedAt"]=DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz",CultureInfo.InvariantCulture);
                }rows[id]=merged;
            }result[table]=new(computer.Columns,rows);
        }
        var newPc=pc.Tables["PaymentTransactions"].Rows.Where(x=>!baseline.Tables["PaymentTransactions"].Rows.ContainsKey(x.Key)).Select(x=>x.Value).ToArray();
        string Allocations(SyncData data,string transaction)=>string.Join('|',data.Tables["PaymentAllocations"].Rows.Values.Where(a=>a["PaymentTransactionId"]==transaction).OrderBy(a=>a["WorkItemId"],StringComparer.Ordinal).Select(a=>a["WorkItemId"]+":"+a["AmountCents"]));
        foreach(var mobile in phone.Tables["PaymentTransactions"].Rows.Where(x=>!baseline.Tables["PaymentTransactions"].Rows.ContainsKey(x.Key)&&!pc.Tables["PaymentTransactions"].Rows.ContainsKey(x.Key)).Select(x=>x.Value)){
            if(newPc.Any(p=>new[]{"Type","AmountCents","TransactionDate","PaymentMethod"}.All(c=>p[c]==mobile[c])&&Allocations(pc,p["Id"]!)==Allocations(phone,mobile["Id"]!)))warnings.Add("同一订单在两端有金额、日期和方式相同的新收退款，请核对是否重复登记。需要撤销重复记录时，先取消同步处理后重试。");
        }
        return new(new(result,pc.ConfigurationFingerprint),conflicts,pcChanges,phoneChanges,warnings.Distinct().ToArray());
    }
    public static async Task Apply(string database,string expectedFingerprint,SyncData proposed,DateOnly today,CancellationToken ct=default,Guid? operation=null,string? choicesHash=null)
    {
        await using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=database,Mode=SqliteOpenMode.ReadWrite,ForeignKeys=true,Pooling=false}.ToString());await connection.OpenAsync(ct);
        await using var transaction=connection.BeginTransaction(deferred:false);
        var current=await Read(connection,transaction,ct);if(Fingerprint(current)!=expectedFingerprint)throw new InvalidOperationException("预览后电脑账本又有改动，请重新预览；本次尚未写入。");ValidateShape(current,proposed);
        await using(var defer=connection.CreateCommand()){defer.Transaction=transaction;defer.CommandText="PRAGMA defer_foreign_keys=ON";await defer.ExecuteNonQueryAsync(ct);}
        foreach(var table in Tables){var columns=current.Tables[table].Columns;
            foreach(var row in proposed.Tables[table].Rows){var old=current.Tables[table].Rows.GetValueOrDefault(row.Key);if(Equal(old,row.Value,columns))continue;
                await using var command=connection.CreateCommand();command.Transaction=transaction;
                command.CommandText=$"INSERT INTO {Q(table)} ({string.Join(',',columns.Select(Q))}) VALUES ({string.Join(',',columns.Select((_,i)=>"@v"+i))}) ON CONFLICT(Id) DO UPDATE SET {string.Join(',',columns.Where(c=>c!="Id").Select(c=>Q(c)+"=excluded."+Q(c)))}";
                for(int i=0;i<columns.Length;i++)command.Parameters.AddWithValue("@v"+i,(object?)row.Value[columns[i]]??DBNull.Value);
                await command.ExecuteNonQueryAsync(ct);
            }
        }
        await ValidateGraph(connection,transaction,today,ct);
        if(operation is {} syncId){
            await using var receipt=connection.CreateCommand();receipt.Transaction=transaction;
            receipt.CommandText="CREATE TABLE IF NOT EXISTS __DeskSyncResults (OperationId TEXT PRIMARY KEY, ChoicesHash TEXT NOT NULL); INSERT INTO __DeskSyncResults(OperationId,ChoicesHash) VALUES(@id,@hash)";
            receipt.Parameters.AddWithValue("@id",syncId.ToString("N"));receipt.Parameters.AddWithValue("@hash",choicesHash??"");await receipt.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }
    public static async Task<string?> AppliedSync(string database,Guid operation,CancellationToken ct=default)
    {
        await using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=database,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());await connection.OpenAsync(ct);
        await using var command=connection.CreateCommand();command.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='__DeskSyncResults'";
        if(Convert.ToInt64(await command.ExecuteScalarAsync(ct))==0)return null;
        command.CommandText="SELECT ChoicesHash FROM __DeskSyncResults WHERE OperationId=@id";command.Parameters.AddWithValue("@id",operation.ToString("N"));return await command.ExecuteScalarAsync(ct) as string;
    }
    private static async Task ValidateGraph(SqliteConnection connection,SqliteTransaction transaction,DateOnly today,CancellationToken ct)
    {
        await using var db=new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(connection).Options);await db.Database.UseTransactionAsync(transaction,ct);
        var work=await db.WorkItems.IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);var payments=await db.PaymentTransactions.AsNoTracking().ToListAsync(ct);var allocations=await db.PaymentAllocations.AsNoTracking().ToListAsync(ct);var groups=await db.ExpenseGroups.AsNoTracking().ToListAsync(ct);var costs=await db.Expenses.AsNoTracking().ToListAsync(ct);var shipments=await db.Shipments.AsNoTracking().ToListAsync(ct);
        foreach(var payment in payments){if(allocations.Where(a=>a.PaymentTransactionId==payment.Id).Sum(a=>a.AmountCents)!=payment.AmountCents)throw new InvalidDataException("收退款金额与订单分配不一致，本次合并未写入。");if(payment.TransactionDate>today)throw new InvalidDataException("收退款日期晚于电脑当前日期，请核对手机时间。");}
        var active=payments.Where(x=>x.Status==RecordStatus.Active).ToDictionary(x=>x.Id);
        var settings=await db.Settings.AsNoTracking().SingleOrDefaultAsync(x=>x.Key=="WorkbenchSettings",ct);var duration=1;
        if(settings is not null){var configured=JsonSerializer.Deserialize<WorkbenchSettingsDto>(settings.Value,new JsonSerializerOptions(JsonSerializerDefaults.Web));duration=configured?.NonPhysicalDefaultDurationDays??1;}
        foreach(var item in work){
            var net=allocations.Where(a=>a.WorkItemId==item.Id&&active.ContainsKey(a.PaymentTransactionId)).Sum(a=>active[a.PaymentTransactionId].Type==TransactionType.Receipt?a.AmountCents:-a.AmountCents);
            if(net<0)throw new InvalidDataException($"{item.BusinessOrderNo} 合并后的退款超过实收。两端备份已保留，尚未写入；请先撤销重复退款再同步。");
            var total=costs.Where(e=>e.Status==ExpenseStatus.Active&&groups.Any(g=>g.Id==e.ExpenseGroupId&&g.WorkItemId==item.Id)).Sum(e=>e.AmountCents);
            if(item.CostConfirmationStatus==CostConfirmationStatus.ConfirmedNoCost&&total!=0)throw new InvalidDataException($"{item.BusinessOrderNo} 一端确认无支出，另一端新增了支出，请先核对成本状态。");
            if(item.CompletedDate>today||item.CompletedDate<item.ReceivedDate)throw new InvalidDataException("完成日期与接单日期冲突，请先核对后同步。");
            var due=WorkItemService.CalculateDue(item,duration);
            if(due!=item.EffectiveDueDate){await using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="UPDATE WorkItems SET EffectiveDueDate=@due,Version=Version+1 WHERE Id=@id";command.Parameters.AddWithValue("@due",(object?)due?.ToString("yyyy-MM-dd")??DBNull.Value);command.Parameters.AddWithValue("@id",item.Id.ToString().ToUpperInvariant());await command.ExecuteNonQueryAsync(ct);}
        }
        foreach(var ship in shipments){var item=work.Single(w=>w.Id==ship.WorkItemId);if(ship.ShippedDate>today||ship.DeliveredDate>today||ship.ShippedDate<item.ReceivedDate)throw new InvalidDataException("寄出或签收日期不符合账本日期，请先核对。");if(ship.ShipmentStatus is ShipmentStatus.Shipped or ShipmentStatus.Delivered&&item.WorkStatus is not(WorkStatus.Completed or WorkStatus.NeedsRework or WorkStatus.Cancelled))throw new InvalidDataException("合并后存在未完成却已寄出的项目，请先核对状态。");}
        await using var check=connection.CreateCommand();check.Transaction=transaction;check.CommandText="PRAGMA foreign_key_check";await using var reader=await check.ExecuteReaderAsync(ct);if(await reader.ReadAsync(ct))throw new InvalidDataException("合并后关联记录不完整，本次尚未写入。");
    }
}


