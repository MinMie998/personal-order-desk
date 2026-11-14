using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PersonalOrderDesk.Data;
using PersonalOrderDesk.Domain;

namespace PersonalOrderDesk.Services;

public sealed class TransferService(DeskDbContext db, IMaintenanceService maintenance) : ITransferService
{
    public async Task<TransferPreview> PreviewAsync(Stream workbook, string fileName, DateTimeOffset? lastWriteTime = null, CancellationToken cancellationToken = default)
    {
        using var memory = new MemoryStream(); await workbook.CopyToAsync(memory, cancellationToken); var bytes = memory.ToArray();
        using var input = new MemoryStream(bytes); using var book = new XLWorkbook(input);
        var sheets = new List<TransferSheetPreview>();
        foreach (var ws in book.Worksheets)
        {
            if(ws.Name is "工作项报告" or "财务交易" or "收支记录" or "收款分配" or "成本" or "物流" || ws.RangeUsed()?.CellsUsed().Any(c=>c.GetFormattedString().Contains("单向报告：不得修改后回写",StringComparison.Ordinal))==true) throw new InvalidDataException("此文件是本应用的单向报告，不允许作为导入源。");
            var used = ws.RangeUsed(); var rows = new List<TransferSourceRow>();
            if (used is not null)
            {
                var first = used.RangeAddress.FirstAddress; var last = used.RangeAddress.LastAddress;
                var headerRow=Enumerable.Range(first.RowNumber,Math.Min(10,last.RowNumber-first.RowNumber+1)).FirstOrDefault(r=>Enumerable.Range(first.ColumnNumber,last.ColumnNumber-first.ColumnNumber+1).Select(c=>ws.Cell(r,c).GetFormattedString().Trim()).Any(v=>v is "订单编号" or "ProjectName"));
                if(headerRow==0) headerRow=first.RowNumber;
                var effectiveLastColumn=ws.Name=="基本信息"?Math.Min(last.ColumnNumber,first.ColumnNumber+8):last.ColumnNumber;
                var headers = Enumerable.Range(first.ColumnNumber, effectiveLastColumn-first.ColumnNumber+1).Select(c => ws.Cell(headerRow,c).GetFormattedString().Trim()).ToArray();
                var dataStart=headerRow==first.RowNumber && headerRow==1 ? 2 : headerRow+1;
                for (var r=dataStart; r<=last.RowNumber; r++)
                {
                    var vals = new Dictionary<string,string?>(StringComparer.OrdinalIgnoreCase);
                    for(var c=0;c<headers.Length;c++) if(!string.IsNullOrWhiteSpace(headers[c])) vals[headers[c]]=CellText(ws.Cell(r,first.ColumnNumber+c));
                    if(ws.Name is "基本信息" or "完成进度" or "收益记录") vals=NormalizeChinese(vals,ws.Name);
                    if(vals.Values.All(string.IsNullOrWhiteSpace)) continue;
                    var canonical=string.Join("\u001f",vals.OrderBy(x=>x.Key,StringComparer.Ordinal).Select(x=>$"{x.Key}={x.Value}"));
                    rows.Add(new(ws.Name,r,vals,Hash(Encoding.UTF8.GetBytes(canonical))));
                }
            }
            sheets.Add(new(ws.Name, rows.Count, rows));
        }
        var fingerprint=Hash(bytes); var seen=await db.ImportBatches.AnyAsync(x=>x.FileFingerprint==fingerprint,cancellationToken);
        var dateIssues=BuildDateIssues(sheets);
        var decisions=sheets.SelectMany(s=>s.Rows.Select(r=>new TransferRowDecision(s.SheetName,r.RowNumber,r.Values.ContainsKey("ProjectName")?"WorkItem": "Unknown",null,false,"请核对来源行并明确匹配目标；不会自动合并同业务编号项目。",SuggestedWorkItemGroupKey:SuggestedGroup(r.Values)))).ToArray();
        var priorBatch=(await db.ImportBatches.Where(x=>x.SourceFileName==Path.GetFileName(fileName)&&x.Status==ImportStatus.Imported).ToListAsync(cancellationToken)).OrderByDescending(x=>x.ImportedAt).FirstOrDefault();
        var differences=new List<TransferDifference>();var missingPriorRows=new List<string>();var priorRows=new List<ImportRowMapping>();
        if(priorBatch is not null)
        {
            priorRows=await db.ImportRowMappings.Where(x=>x.ImportBatchId==priorBatch.Id&&x.EntityType=="WorkItem").ToListAsync(cancellationToken);
            var candidates=priorRows.ToDictionary(x=>(x.SheetName,x.SourceRowNumber),x=>x.EntityId);
            decisions=decisions.Select(d=>candidates.TryGetValue((d.SheetName,d.RowNumber),out var id)?d with{TargetEntityId=id,Note="与先前已导入来源行匹配；请核对差异并显式确认，不会覆盖当前记录。"}:d).ToArray();
            var priorSnapshots=await db.ActivityLogs.Where(x=>x.ImportBatchId==priorBatch.Id&&x.ActionType=="HistoricalImportSourceSnapshot"&&x.AfterData!=null).ToListAsync(cancellationToken);
            foreach(var mapping in priorRows)
            {
                var incoming=sheets.FirstOrDefault(x=>x.SheetName==mapping.SheetName)?.Rows.FirstOrDefault(x=>x.RowNumber==mapping.SourceRowNumber);
                if(incoming is null){missingPriorRows.Add($"{mapping.SheetName}/{mapping.SourceRowNumber}");continue;}if(!Guid.TryParse(mapping.EntityId,out var targetId))continue;
                var target=await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==targetId,cancellationToken);if(target is null)continue;
                AddDifference(differences,mapping,incoming,target,"BusinessOrderNo","ProjectName","ProjectType","ReceivedDate","EffectiveDueDate","WorkStatus","CompletedDate","CurrentWork","NextStep","ProjectPath","Notes");
                var prior=priorSnapshots.FirstOrDefault(log=>SourceSnapshotMatches(log.AfterData!,mapping.SheetName,mapping.SourceRowNumber));if(prior is not null)
                {
                    using var json=System.Text.Json.JsonDocument.Parse(prior.AfterData!);var previous=System.Text.Json.JsonSerializer.Deserialize<Dictionary<string,string?>>(json.RootElement.GetProperty("Values").GetRawText())??new();
                    foreach(var field in previous.Keys.Union(incoming.Values.Keys,StringComparer.OrdinalIgnoreCase))
                    {var before=previous.GetValueOrDefault(field);var after=incoming.Values.GetValueOrDefault(field);if(!string.Equals(before,after,StringComparison.Ordinal))differences.Add(new(mapping.SheetName,mapping.SourceRowNumber,"SourceRow",target.Id.ToString(),$"Source:{field}",before,after));}
                }
            }
        }
        var info=File.Exists(fileName)?new FileInfo(fileName):null;
        var warnings=new List<string>();
        foreach (var sheet in sheets)
        foreach (var duplicates in sheet.Rows.GroupBy(row => Identity(row.Values), StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            warnings.Add($"{sheet.SheetName}的第 {string.Join("、", duplicates.Select(row => row.RowNumber))} 行有相同编号和项目名，请逐行核对归属。名称相同不代表一定是同一工作项；不同报价不能自动取第一笔。");
        var income=sheets.FirstOrDefault(x=>x.SheetName=="收益记录")?.Rows??[];var progress=sheets.FirstOrDefault(x=>x.SheetName=="完成进度")?.Rows??[];
        var receiptCandidates=income.Count(x=>x.Values.GetValueOrDefault("PaymentStatus")?.Contains("已结款")==true&&MoneyCents(x.Values.GetValueOrDefault("QuotedAmount")) is >0);
        var knownCosts=income.Count(x=>MoneyCents(x.Values.GetValueOrDefault("ExpenseAmount")) is >0);var pendingCosts=income.Count(x=>string.IsNullOrWhiteSpace(x.Values.GetValueOrDefault("ExpenseAmount"))||x.Values.GetValueOrDefault("ExpenseStatus")?.Contains("待确认")==true);
        var requiredShips=progress.Count(x=>x.Values.GetValueOrDefault("ShipmentStatus") is { } s && !s.Contains("无需") && !s.Contains("不需要"));var unknownShipping=progress.Count(x=>x.Values.GetValueOrDefault("ShipmentStatus") is null or "" or "待确认");
        warnings.Add($"预览预计按编号+项目名合并生成工作项 {sheets.SelectMany(x=>x.Rows).Select(r=>Identity(r.Values)).Distinct(StringComparer.OrdinalIgnoreCase).Count()} 条（正式创建需逐行确认）。");
        warnings.Add($"收款：报价不视为实收；来源标记已结款的候选 {receiptCandidates} 笔，金额合计 {income.Where(x=>x.Values.GetValueOrDefault("PaymentStatus")?.Contains("已结款")==true).Sum(x=>MoneyCents(x.Values.GetValueOrDefault("QuotedAmount"))??0)/100m:0.00} 元，只有逐行确认才创建历史收款及分配；缺失日期保留未知。");
        warnings.Add($"成本：已知正数来源支出 {knownCosts} 条可供确认建立成本事项和分项；成本状态或完整性未决 {pendingCosts} 条保留来源待核；0与空白分别处理。");
        warnings.Add($"物流：发现 {requiredShips} 条需核对物流字段，其中状态未知 {unknownShipping} 条；明确需要寄送后才建立物流记录；状态、日期、单号未知知识不会推断。");
        warnings.Add("已完成但没有完成日期时，保持完成且日期未知；源行快照及决定会随导入批次持久保存。导出是单向报告，不可导入回写。");
        if(dateIssues.Count>0)warnings.Add($"日期核对项 {dateIssues.Count} 条，其中无法解析必须修复 {dateIssues.Count(x=>!x.IsCrossRowConflict)} 条、截止日期跨表冲突 {dateIssues.Count(x=>x.IsCrossRowConflict)} 条；原值逐行保留，不会猜测年份。");
        var unresolvedStatus=progress.Where(x=>WorkStatusTry(x.Values.GetValueOrDefault("WorkStatus")) is null).ToArray();if(unresolvedStatus.Length>0)warnings.Add($"有 {unresolvedStatus.Length} 条进度状态为空或无法识别（来源行：{string.Join(", ",unresolvedStatus.Select(x=>x.RowNumber))}）；导入前必须逐行明确选择状态。");
        var basicRows=sheets.FirstOrDefault(x=>x.SheetName=="基本信息")?.Rows??[];var progressKeys=progress.Select(x=>Identity(x.Values)).ToHashSet(StringComparer.OrdinalIgnoreCase);var basicWithoutProgress=basicRows.Where(x=>!progressKeys.Contains(Identity(x.Values))).ToArray();if(basicWithoutProgress.Length>0)warnings.Add($"有 {basicWithoutProgress.Length} 条基本信息来源行没有精确匹配到进度行（{string.Join(", ",basicWithoutProgress.Select(x=>$"{x.RowNumber}:{x.Values.GetValueOrDefault("BusinessOrderNo")}/{x.Values.GetValueOrDefault("ProjectName")}"))}）；需明确匹配或在该来源行选择状态，不能默认未开始。");
        var namesBySheet=sheets.Where(x=>x.SheetName is "基本信息" or "完成进度" or "收益记录").SelectMany(s=>s.Rows.Select(r=>(s.SheetName,Row:r.RowNumber,Order:r.Values.GetValueOrDefault("BusinessOrderNo"),Name:r.Values.GetValueOrDefault("ProjectName")))).Where(x=>!string.IsNullOrWhiteSpace(x.Order)).GroupBy(x=>x.Order!,StringComparer.OrdinalIgnoreCase);
        var mismatchedNames=namesBySheet.Where(g=>g.Select(x=>x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()>1).Select(g=>$"{g.Key}：{string.Join("；",g.Select(x=>$"{x.SheetName}第{x.Row}行={x.Name}"))}").ToArray();if(mismatchedNames.Length>0)warnings.Add($"发现跨Sheet同编号不同项目名候选 {mismatchedNames.Length} 组，按编号+项目名保留为不同工作项并待核：{string.Join(" | ",mismatchedNames)}");
        if(seen)warnings.Add("此文件指纹已经导入；重复确认将被拒绝。");
        if(missingPriorRows.Count>0)warnings.Add($"修改后的来源中未找到既有映射行 {missingPriorRows.Count} 条（{string.Join(", ",missingPriorRows)}）；仅提示候选，不会因此删除应用记录。");
        var existingTargets=await db.WorkItems.AsNoTracking().Where(x=>!x.IsArchived).OrderBy(x=>x.ProjectName).Select(x=>new TransferWorkItemTarget(x.Id,x.BusinessOrderNo,x.ProjectName)).ToListAsync(cancellationToken);
        return new(Path.GetFileName(fileName),fingerprint,lastWriteTime??(info is null?null:info.LastWriteTimeUtc),bytes.LongLength,seen,sheets,decisions,differences,warnings,existingTargets,dateIssues);
    }
    public async Task<TransferPreview> PreviewFileAsync(string path,CancellationToken cancellationToken=default)
    { var fi=new FileInfo(path); await using var fs=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite); return await PreviewAsync(fs,fi.Name,fi.LastWriteTimeUtc,cancellationToken); }
    public async Task<TransferPreview> PreviewDifferencesAsync(Stream workbook,string fileName,IReadOnlyList<TransferRowDecision> decisions,CancellationToken cancellationToken=default)
    {
        var preview=await PreviewAsync(workbook,fileName,null,cancellationToken); var diffs=preview.Differences.ToList();
        foreach(var d in decisions.Where(x=>x.IsKnown&&Guid.TryParse(x.TargetEntityId,out _)))
        {
            var row=preview.Sheets.FirstOrDefault(x=>x.SheetName==d.SheetName)?.Rows.FirstOrDefault(x=>x.RowNumber==d.RowNumber); if(row is null) continue;
            var target=await db.WorkItems.FindAsync([Guid.Parse(d.TargetEntityId!)],cancellationToken); if(target is null) continue;
            AddDifference(diffs, new ImportRowMapping { SheetName = d.SheetName, SourceRowNumber = d.RowNumber,
                EntityType = "WorkItem", EntityId = target.Id.ToString() }, row, target, row.Values.Keys.ToArray());
        }
        return preview with { Decisions=decisions, Differences=diffs.Distinct().ToArray(), DateIssues=BuildDateIssues(preview.Sheets, decisions) };
    }
    public async Task<TransferImportResult> ConfirmImportAsync(TransferImportRequest request,CancellationToken cancellationToken=default)
    {
        if(!request.ConfirmWholeBatch) throw new InvalidOperationException("必须明确确认整批导入。");
        await using var fs=new FileStream(request.FilePath,FileMode.Open,FileAccess.Read,FileShare.Read); var p=await PreviewAsync(fs,request.FilePath,null,cancellationToken);
        if(p.AlreadyImported) return new(Guid.Empty,0,p.Decisions.Count,true);
        if(!string.Equals(request.PreviewFingerprint,p.FileFingerprint,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("确认所依据的预览指纹缺失或来源已变化；请重新预览并确认。");
        var expectedRows=p.Sheets.SelectMany(x=>x.Rows.Select(r=>(r.SheetName,r.RowNumber))).ToHashSet();var suppliedRows=request.Decisions.Select(x=>(x.SheetName,x.RowNumber)).ToArray();
        if(suppliedRows.Distinct().Count()!=suppliedRows.Length||!expectedRows.SetEquals(suppliedRows))throw new InvalidOperationException("确认决定必须逐条对应当前预览中的每个来源行，且不能重复。");
        if (request.Decisions.Any(decision => !decision.IsKnown || decision.EntityType != "WorkItem"))
            throw new InvalidOperationException("仍有来源行尚未确认项目关联，请全部核对后整批导入。收款、成本付款、物流和历史日期可以继续保留未知；不会将未关联行跳过后标记整份文件已导入。");
        foreach(var issue in p.DateIssues??[])
        {
            var decision=request.Decisions.FirstOrDefault(x=>x.SheetName==issue.SheetName&&x.RowNumber==issue.RowNumber);
            if(decision is null||!decision.IsKnown||!string.IsNullOrWhiteSpace(decision.TargetEntityId))continue;
            if(!issue.IsCrossRowConflict&&DateOverride(decision,issue.Field) is null)throw new InvalidDataException($"{issue.SheetName}第{issue.RowNumber}行字段“{issue.Field}”原值“{issue.RawValue}”无法解析，必须修正来源或明确填写日期后重试。");
        }
        var selectedGroups=new Dictionary<string,(string Order,string Name)> (StringComparer.OrdinalIgnoreCase);
        foreach(var d in request.Decisions.Where(x=>x.IsKnown&&string.Equals(x.EntityType,"WorkItem",StringComparison.OrdinalIgnoreCase)&&string.IsNullOrWhiteSpace(x.TargetEntityId)))
        {
            var row=p.Sheets.First(x=>x.SheetName==d.SheetName).Rows.First(x=>x.RowNumber==d.RowNumber);var order=row.Values.GetValueOrDefault("BusinessOrderNo")?.Trim()??"";var name=d.NewWorkItemProjectName?.Trim() is {Length:>0} n?n:row.Values.GetValueOrDefault("ProjectName")?.Trim()??"";var group=string.IsNullOrWhiteSpace(d.NewWorkItemGroupKey)?Identity(row.Values):$"{order}\u001fmanual\u001f{d.NewWorkItemGroupKey.Trim()}";
            if(selectedGroups.TryGetValue(group,out var prior)&&(prior.Order!=order||!string.Equals(prior.Name,name,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("同一待建工作项组的业务编号和规范项目名必须一致。");selectedGroups[group]=(order,name);
        }
        var datedGroups=request.Decisions.Where(x=>x.IsKnown&&x.EntityType=="WorkItem"&&string.IsNullOrWhiteSpace(x.TargetEntityId)).Select(d=>(Decision:d,Row:p.Sheets.First(x=>x.SheetName==d.SheetName).Rows.First(x=>x.RowNumber==d.RowNumber))).GroupBy(x=>DecisionGroup(x.Decision,x.Row),StringComparer.OrdinalIgnoreCase);
        foreach(var group in datedGroups)
        {
            var quotes = group.Where(x => x.Decision.SheetName == "收益记录")
                .Select(x => MoneyCents(x.Row.Values.GetValueOrDefault("QuotedAmount"))).Where(x => x.HasValue).Distinct().ToArray();
            if (quotes.Length > 1)
                throw new InvalidDataException("同一待建工作项包含不同报价，请逐行确认来源归属，或将无法确认的行留待核对；不会取第一笔覆盖其他金额。");
            var rows=group.Where(x=>x.Decision.SheetName is "基本信息" or "完成进度").Where(x=>DateTry(x.Row.Values.GetValueOrDefault("EffectiveDueDate")) is not null).ToArray();
            if(rows.Select(x=>DateTry(x.Row.Values.GetValueOrDefault("EffectiveDueDate"))).Distinct().Count()>1&&(rows.Any(x=>x.Decision.DueDateOverride is null)||rows.Select(x=>x.Decision.DueDateOverride).Distinct().Count()!=1))throw new InvalidDataException($"工作项来源组 {group.Key} 有冲突的截止日期，必须在所有冲突行选择同一日期。");
        }
        var priorBatches=await db.ImportBatches.Where(x=>x.SourceFileName==p.FileName&&x.Status==ImportStatus.Imported).Select(x=>x.Id).ToListAsync(cancellationToken);
        if(priorBatches.Count>0)
        {
            var priorMappings=await db.ImportRowMappings.Where(x=>priorBatches.Contains(x.ImportBatchId)&&x.EntityType=="WorkItem").ToListAsync(cancellationToken);
            foreach(var d in request.Decisions.Where(x=>x.IsKnown&&string.IsNullOrWhiteSpace(x.TargetEntityId)))
                if(priorMappings.Any(x=>x.SheetName==d.SheetName&&x.SourceRowNumber==d.RowNumber))throw new InvalidOperationException($"{d.SheetName}第{d.RowNumber}行已属于之前导入的来源记录；请确认映射到原工作项，或将该行留作未决，不能直接新建重复项。");
        }
        var backup = await maintenance.CreateImportProtectionBackupAsync(cancellationToken);
        if (!backup.Success) throw new InvalidOperationException($"导入前保护备份失败，已中止导入：{backup.Error}");
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,cancellationToken);
        var batch=new ImportBatch{SourceFileName=p.FileName,FileFingerprint=p.FileFingerprint,Status=ImportStatus.Previewed,Summary="历史工作簿显式确认导入"}; db.ImportBatches.Add(batch); await db.SaveChangesAsync(cancellationToken);
        var count=0; var skipped=0;
        try
        {
            var created=new Dictionary<string,WorkItem>(StringComparer.OrdinalIgnoreCase);
            var resolvedStatuses=new HashSet<Guid>();
            foreach(var d in request.Decisions.OrderBy(x=>x.SheetName=="基本信息"?0:x.SheetName=="完成进度"?1:2))
            {
                var row=p.Sheets.FirstOrDefault(x=>x.SheetName==d.SheetName)?.Rows.FirstOrDefault(x=>x.RowNumber==d.RowNumber); if(row is null) throw new InvalidDataException($"来源行不存在: {d.SheetName}/{d.RowNumber}");
                var sourceSnapshotId=Guid.NewGuid(); db.ActivityLogs.Add(new(){EntityType="ImportSourceRow",EntityId=sourceSnapshotId,ActionType="HistoricalImportSourceSnapshot",Summary=$"保留 {d.SheetName} 第 {d.RowNumber} 行原始字段快照与核对决定",AfterData=System.Text.Json.JsonSerializer.Serialize(new{SheetName=d.SheetName,RowNumber=d.RowNumber,Values=row.Values,d.ConfirmReceipt,d.ExpensePaymentDecision,d.CostCompletenessConfirmed,d.NoTrackingNumber,d.ShippingRequirementDecision,d.WorkStatusDecision,d.CancelledFromStatusDecision,d.NewWorkItemGroupKey,d.NewWorkItemProjectName,d.ReceivedDateOverride,d.DueDateOverride,d.CompletedDateOverride,d.ShippedDateOverride,d.DeliveredDateOverride,d.PaidDateOverride,d.Note}),SourceKind=LogSourceKind.Import,ImportBatchId=batch.Id});
                if(!d.IsKnown||!string.Equals(d.EntityType,"WorkItem",StringComparison.OrdinalIgnoreCase)||string.IsNullOrWhiteSpace(row.Values.GetValueOrDefault("ProjectName"))){db.ImportRowMappings.Add(new(){ImportBatchId=batch.Id,SheetName=d.SheetName,SourceRowNumber=d.RowNumber,EntityType="UnresolvedSourceRow",EntityId=sourceSnapshotId.ToString(),EntityOrdinal=0});skipped++;continue;}
                if(Guid.TryParse(d.TargetEntityId,out var existingId))
                {
                    if(!await db.WorkItems.AnyAsync(x=>x.Id==existingId,cancellationToken))throw new InvalidOperationException("所选工作项不存在或已在回收站，请先恢复后再关联。");
                    db.ImportRowMappings.Add(new(){ImportBatchId=batch.Id,SheetName=d.SheetName,SourceRowNumber=d.RowNumber,EntityType="WorkItem",EntityId=existingId.ToString(),EntityOrdinal=0});skipped++;continue;
                }
                var key=string.IsNullOrWhiteSpace(d.NewWorkItemGroupKey)?Identity(row.Values):$"{row.Values.GetValueOrDefault("BusinessOrderNo")?.Trim()}\u001fmanual\u001f{d.NewWorkItemGroupKey.Trim()}"; if(!created.TryGetValue(key,out var w)) { w=NewHistoricalWork(row.Values,batch.Id);if(!string.IsNullOrWhiteSpace(d.NewWorkItemProjectName))w.ProjectName=d.NewWorkItemProjectName.Trim(); created[key]=w; db.WorkItems.Add(w); count++; }
                else MergeSupplemental(w,row.Values);
                if(d.ShippingRequirementDecision.HasValue) w.ShippingRequirement=d.ShippingRequirementDecision.Value;
                if(d.ReceivedDateOverride.HasValue)w.ReceivedDate=d.ReceivedDateOverride;
                if(d.DueDateOverride.HasValue){w.DueDateMode=DueDateMode.Manual;w.SpecifiedDueDate=d.DueDateOverride;w.EffectiveDueDate=d.DueDateOverride;}
                else if(DateTry(row.Values.GetValueOrDefault("EffectiveDueDate")) is { } sourceDue&&w.EffectiveDueDate is null){w.DueDateMode=DueDateMode.Manual;w.SpecifiedDueDate=sourceDue;w.EffectiveDueDate=sourceDue;}
                if(d.CompletedDateOverride.HasValue){w.CompletedDate=d.CompletedDateOverride;w.CompletedDateKnowledge=w.WorkStatus==WorkStatus.Completed?CompletionDateKnowledge.Known:CompletionDateKnowledge.NotApplicable;}
                if(d.WorkStatusDecision.HasValue&&d.SheetName!="完成进度")
                {
                    w.WorkStatus=d.WorkStatusDecision.Value;w.CompletedDate=null;w.CompletedAt=null;w.CompletedDateKnowledge=w.WorkStatus==WorkStatus.Completed?CompletionDateKnowledge.Unknown:CompletionDateKnowledge.NotApplicable;
                    if(w.WorkStatus==WorkStatus.Cancelled)w.CancelledFromStatus=d.CancelledFromStatusDecision??CancelledStatus.Unknown;else w.CancelledFromStatus=null;resolvedStatuses.Add(w.Id);
                }
                if(d.SheetName=="完成进度")
                {
                    var parsed=WorkStatusTry(row.Values.GetValueOrDefault("WorkStatus"));var status=d.WorkStatusDecision??parsed;
                    if(!status.HasValue)throw new InvalidDataException($"{d.SheetName}第{d.RowNumber}行状态无法识别，需在预览中选择状态后再确认。");
                    w.WorkStatus=status.Value;w.CompletedDate=DateTry(row.Values.GetValueOrDefault("CompletedDate"));
                    if(status==WorkStatus.Completed){w.CompletedDateKnowledge=w.CompletedDate.HasValue?CompletionDateKnowledge.Known:CompletionDateKnowledge.Unknown;w.CompletedAt=null;}
                    else {w.CompletedDate=null;w.CompletedDateKnowledge=CompletionDateKnowledge.NotApplicable;w.CompletedAt=null;}
                    if(status==WorkStatus.Cancelled){var cancelledFrom=d.CancelledFromStatusDecision??CancelledFromTry(row.Values.GetValueOrDefault("ReworkStatus"));w.CancelledFromStatus=cancelledFrom??CancelledStatus.Unknown;}
                    else w.CancelledFromStatus=null;
                    resolvedStatuses.Add(w.Id);
                    if(w.ShippingRequirement==ShippingRequirement.Pending&&d.ShippingRequirementDecision is null)AddPending(w,$"历史物流要求待核：来源状态“{row.Values.GetValueOrDefault("ShipmentStatus")??"空白"}”，需明确无需寄送或需要寄送。");
                }
                db.ImportRowMappings.Add(new(){ImportBatchId=batch.Id,SheetName=d.SheetName,SourceRowNumber=d.RowNumber,EntityType="WorkItem",EntityId=w.Id.ToString(),EntityOrdinal=0});
                if(row.Values.GetValueOrDefault("QuotedAmount") is { } quote && MoneyCents(quote) is long cents && cents >= 0)
                {
                    w.OrderAmountCents ??= cents;
                    if(cents>0&&d.ConfirmReceipt==true && row.Values.GetValueOrDefault("PaymentStatus")?.Contains("已结款")==true)
                    {
                        var paidDate=d.PaidDateOverride??DateTry(row.Values.GetValueOrDefault("PaidDate"));var receipt=new PaymentTransaction{Type=TransactionType.Receipt,AmountCents=cents,TransactionDate=paidDate,DateKnowledge=paidDate.HasValue?DateKnowledge.Known:DateKnowledge.Unknown,IdempotencyKey=$"import:{batch.Id}:{d.SheetName}:{d.RowNumber}:receipt",Status=RecordStatus.Active,SourceKind=SourceKind.HistoricalImport,ImportBatchId=batch.Id,Notes="历史已结款；经用户确认的期初收款"};
                        db.PaymentTransactions.Add(receipt); db.PaymentAllocations.Add(new(){PaymentTransactionId=receipt.Id,WorkItemId=w.Id,AmountCents=cents});
                    }
                    else if(row.Values.GetValueOrDefault("PaymentStatus")?.Contains("已结款")==true) AddPending(w,$"历史结款待核：来源标记已结款，报价 {quote} 元；{(cents==0?"零金额不能创建收款。":"收款尚未由用户确认。")}");
                }
                else if(row.Values.GetValueOrDefault("PaymentStatus")?.Contains("已结款")==true) AddPending(w,$"历史结款待核：来源标记已结款，原始报价未知或格式无法识别：{row.Values.GetValueOrDefault("QuotedAmount")}。");
                else if(d.SheetName=="收益记录"&&!string.IsNullOrWhiteSpace(row.Values.GetValueOrDefault("PaymentStatus"))&&row.Values.GetValueOrDefault("PaymentStatus")!="待确认")AddPending(w,$"历史结款标记待核：“{row.Values.GetValueOrDefault("PaymentStatus")}”，报价 {row.Values.GetValueOrDefault("QuotedAmount")} 元不视为实际收款。");
                if(d.SheetName=="收益记录")
                {
                    var rawCost=row.Values.GetValueOrDefault("ExpenseAmount");
                    if(rawCost is not null&&MoneyCents(rawCost) is long zeroCost&&zeroCost==0) { if(d.CostCompletenessConfirmed==true) w.CostConfirmationStatus=CostConfirmationStatus.ConfirmedNoCost;else AddPending(w,$"历史成本待核：来源明确填写{rawCost}，但尚未确认成本记录完整。"); }
                    else if(MoneyCents(rawCost) is long cost && cost > 0 && d.CostCompletenessConfirmed==true && d.ExpensePaymentDecision.HasValue)
                    {
                        var group=new ExpenseGroup{WorkItemId=w.Id,Title="历史来源支出",Category="历史导入",Notes=$"原始支出：{rawCost}"};db.ExpenseGroups.Add(group);
                        db.Expenses.Add(new(){ExpenseGroupId=group.Id,AmountCents=cost,ExpenseDate=null,ExpenseDateKnowledge=DateKnowledge.Unknown,PaymentStatus=d.ExpensePaymentDecision.Value,PaidDate=null,PaidDateKnowledge=d.ExpensePaymentDecision==ExpensePaymentStatus.Paid?DateKnowledge.Unknown:DateKnowledge.NotApplicable,SourceKind=SourceKind.HistoricalImport,ImportBatchId=batch.Id,Status=ExpenseStatus.Active,Notes="历史支出；付款状态由用户确认，原始日期未知"}); w.CostConfirmationStatus=CostConfirmationStatus.ConfirmedComplete;
                    }
                    else if(string.IsNullOrWhiteSpace(rawCost)||rawCost!="0") { w.CostConfirmationStatus=CostConfirmationStatus.Pending;AddPending(w,$"历史成本待核：原始支出“{rawCost}”，成本完整性或支付状态未确认。"); }
                }
                if(d.SheetName=="完成进度" && w.ShippingRequirement==ShippingRequirement.Required && !db.Shipments.Local.Any(x=>x.WorkItemId==w.Id))
                {
                    var rawShipDate=d.ShippedDateOverride??DateTry(row.Values.GetValueOrDefault("ShippedDate"));var rawDeliveredDate=d.DeliveredDateOverride??DateTry(row.Values.GetValueOrDefault("DeliveredDate"));var shipStatus=ShipmentTry(row.Values.GetValueOrDefault("ShipmentStatus"),rawShipDate,rawDeliveredDate);var tracking=row.Values.GetValueOrDefault("TrackingNumber");
                    if(w.ShippingRequirement==ShippingRequirement.None&&shipStatus.HasValue)throw new InvalidDataException($"{d.SheetName}第{d.RowNumber}行标记无需物流，却包含寄出/签收状态或日期，请核对。");
                    db.Shipments.Add(new(){WorkItemId=w.Id,ShipmentStatus=shipStatus??ShipmentStatus.Unknown,TrackingNumber=string.IsNullOrWhiteSpace(tracking)?null:tracking,NoTrackingNumber=string.IsNullOrWhiteSpace(tracking)?d.NoTrackingNumber:(d.NoTrackingNumber==true?null:false),ShippedDate=shipStatus is ShipmentStatus.Shipped or ShipmentStatus.Delivered?rawShipDate:null,ShippedDateKnowledge=shipStatus is ShipmentStatus.Shipped or ShipmentStatus.Delivered?(rawShipDate.HasValue?DateKnowledge.Known:DateKnowledge.Unknown):DateKnowledge.NotApplicable,DeliveredDate=shipStatus==ShipmentStatus.Delivered?rawDeliveredDate:null,DeliveredDateKnowledge=shipStatus==ShipmentStatus.Delivered?(rawDeliveredDate.HasValue?DateKnowledge.Known:DateKnowledge.Unknown):DateKnowledge.NotApplicable,SourceKind=SourceKind.HistoricalImport,ImportBatchId=batch.Id,Notes="历史来源物流事实；未明确状态按未知保留"});
                }
                if(w.WorkStatus==WorkStatus.Completed && !db.WorkStatusHistories.Local.Any(x=>x.WorkItemId==w.Id)) db.WorkStatusHistories.Add(new(){WorkItemId=w.Id,OldStatus=WorkStatus.Completed,NewStatus=WorkStatus.Completed,EventType=HistoryEventType.Completed,OccurredDate=w.CompletedDate,RecordedAt=DateTimeOffset.UtcNow,SourceKind=SourceKind.HistoricalImport,ImportBatchId=batch.Id,IsBackfilled=true,Reason="历史完成状态快照；变更前状态未知"});
            }
            if(created.Values.Any(w=>!resolvedStatuses.Contains(w.Id)))throw new InvalidDataException("有工作项缺少可识别的进度状态来源，请在预览逐行补充状态决定后重试。");
            batch.Status=ImportStatus.Imported; await db.SaveChangesAsync(cancellationToken); await tx.CommitAsync(cancellationToken); return new(batch.Id,count,skipped,false);
        }
        catch(Exception ex){await tx.RollbackAsync(cancellationToken); throw new InvalidOperationException("整批导入失败，事务已回滚。",ex);}
    }
    public async Task<byte[]> ExportReportAsync(CancellationToken cancellationToken=default)
    {
        var items=await db.WorkItems.IgnoreQueryFilters().AsNoTracking().OrderBy(x=>x.ProjectName).ToListAsync(cancellationToken);var transactions=await db.PaymentTransactions.AsNoTracking().ToListAsync(cancellationToken);var allocations=await db.PaymentAllocations.AsNoTracking().ToListAsync(cancellationToken);var expenses=await db.Expenses.AsNoTracking().ToListAsync(cancellationToken);var groups=await db.ExpenseGroups.AsNoTracking().ToDictionaryAsync(x=>x.Id,cancellationToken);var shipments=await db.Shipments.AsNoTracking().ToListAsync(cancellationToken);var itemById=items.ToDictionary(x=>x.Id);
        using var book=new XLWorkbook();
        var wi=book.AddWorksheet("工作项");wi.Cell(1,1).Value="单向报告：禁止修改后回写";wi.Cell(1,2).Value=$"导出时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}；范围：全部记录（含回收站）；回收站记录不计入工作台统计";wi.Cell(2,1).InsertTable(items.Select(x=>new{内部编号=x.Id,业务订单编号=x.BusinessOrderNo,项目名称=x.ProjectName,项目类型=ProjectTypeName(x.ProjectType),排程类别=ScheduleName(x.ScheduleCategory),工作状态=WorkStatusName(x.WorkStatus),接单日期=x.ReceivedDate?.ToDateTime(TimeOnly.MinValue),截止日期知识=DueKnowledgeName(x.DueDateMode),截止日期=x.EffectiveDueDate?.ToDateTime(TimeOnly.MinValue),完成日期知识=CompletionKnowledgeName(x.CompletedDateKnowledge),完成日期=x.CompletedDate?.ToDateTime(TimeOnly.MinValue),订单金额_元=x.OrderAmountCents/100m,工程目录=x.ProjectPath,当前工作=x.CurrentWork,下一步=x.NextStep,来源=SourceName(x.SourceKind),备注=x.Notes,回收站=x.IsDeleted?"已删除":"",删除时间=x.DeletedAt?.ToString("O")}));wi.Column(12).Style.NumberFormat.Format="0.00";
        var pay=book.AddWorksheet("财务交易");pay.Cell(1,1).Value="单向报告；金额单位元；交易与分配分表，禁止重复汇总";pay.Cell(2,1).InsertTable(transactions.Select(x=>new{交易编号=x.Id,交易类型=TransactionName(x.Type),金额_元=x.AmountCents/100m,交易日期=x.TransactionDate?.ToDateTime(TimeOnly.MinValue),日期知识=DateKnowledgeName(x.DateKnowledge),记录状态=RecordStatusName(x.Status),撤销原因=x.ReversalReason,支付方式=x.PaymentMethod,外部参考=x.ExternalReference,来源=SourceName(x.SourceKind),备注=x.Notes}));pay.Column(3).Style.NumberFormat.Format="0.00";
        var alloc=book.AddWorksheet("收款分配明细");alloc.Cell(1,1).Value="分配明细金额不能与交易主表金额相加";alloc.Cell(2,1).InsertTable(allocations.Select(x=>new{交易编号=x.PaymentTransactionId,工作项编号=x.WorkItemId,业务订单编号=itemById.GetValueOrDefault(x.WorkItemId)?.BusinessOrderNo,项目名称=itemById.GetValueOrDefault(x.WorkItemId)?.ProjectName,分配金额_元=x.AmountCents/100m}));alloc.Column(5).Style.NumberFormat.Format="0.00";
        var exp=book.AddWorksheet("成本明细");exp.Cell(1,1).Value="单向报告；金额单位元";exp.Cell(2,1).InsertTable(expenses.Select(x=>{var g=groups.GetValueOrDefault(x.ExpenseGroupId);var item=g is null?null:itemById.GetValueOrDefault(g.WorkItemId);return new{成本编号=x.Id,成本组编号=x.ExpenseGroupId,工作项编号=item?.Id,业务订单编号=item?.BusinessOrderNo,项目名称=item?.ProjectName,成本事项=g?.Title,成本类别=g?.Category,成本备注=g?.Notes,成本金额_元=x.AmountCents/100m,支出日期=x.ExpenseDate?.ToDateTime(TimeOnly.MinValue),支出日期知识=DateKnowledgeName(x.ExpenseDateKnowledge),支付状态=ExpensePaymentName(x.PaymentStatus),支付日期=x.PaidDate?.ToDateTime(TimeOnly.MinValue),支付日期知识=DateKnowledgeName(x.PaidDateKnowledge),成本状态=ExpenseStatusName(x.Status),作废原因=x.ReversalReason,来源=SourceName(x.SourceKind),备注=x.Notes};}));exp.Column(9).Style.NumberFormat.Format="0.00";
        var ship=book.AddWorksheet("物流");ship.Cell(1,1).Value="单向报告；物流状态与日期知识分开";ship.Cell(2,1).InsertTable(shipments.Select(x=>new{工作项编号=x.WorkItemId,业务订单编号=itemById.GetValueOrDefault(x.WorkItemId)?.BusinessOrderNo,项目名称=itemById.GetValueOrDefault(x.WorkItemId)?.ProjectName,物流状态=ShipmentName(x.ShipmentStatus),承运商=x.Carrier,快递单号=x.TrackingNumber,单号知识=x.NoTrackingNumber switch{true=>"明确无单号",false=>"有单号",null=>"未知"},寄出日期=x.ShippedDate?.ToDateTime(TimeOnly.MinValue),寄出日期知识=DateKnowledgeName(x.ShippedDateKnowledge),签收日期=x.DeliveredDate?.ToDateTime(TimeOnly.MinValue),签收日期知识=DateKnowledgeName(x.DeliveredDateKnowledge),来源=SourceName(x.SourceKind),备注=x.Notes}));
        foreach(var ws in book.Worksheets){ForceTextCells(ws);ws.Columns().AdjustToContents();}using var ms=new MemoryStream();book.SaveAs(ms);return ms.ToArray();
    }
    private static string? CellText(IXLCell c)=>c.IsEmpty()?null:c.GetFormattedString(CultureInfo.InvariantCulture);
    private static void ForceTextCells(IXLWorksheet ws)
    {foreach(var cell in ws.CellsUsed().Where(x=>x.DataType==XLDataType.Text)){var text=cell.GetString();cell.Value=text;}}
    private static string ProjectTypeName(ProjectType x)=>x switch{ProjectType.Simulation=>"仿真",ProjectType.SchematicPcbDesign=>"原理图/PCB设计",ProjectType.CodeReport=>"代码/报告",ProjectType.PhysicalProduction=>"实物制作",ProjectType.Other=>"其他",_=>"混合或未知"};
    private static string ScheduleName(ScheduleCategory x)=>x switch{ScheduleCategory.Physical=>"实物排程",ScheduleCategory.Digital=>"数字项目排程",_=>"待确认"};
    private static string WorkStatusName(WorkStatus x)=>x switch{WorkStatus.NotStarted=>"未开始",WorkStatus.InProgress=>"进行中",WorkStatus.Completed=>"已完成",WorkStatus.NeedsRework=>"需要返工",_=>"已取消"};
    private static string DueKnowledgeName(DueDateMode x)=>x switch{DueDateMode.Auto=>"自动排程",DueDateMode.Manual=>"手动日期",_=>"历史未知"};
    private static string CompletionKnowledgeName(CompletionDateKnowledge x)=>x switch{CompletionDateKnowledge.Known=>"已知",CompletionDateKnowledge.Unknown=>"未知",_=>"不适用"};
    private static string DateKnowledgeName(DateKnowledge x)=>x switch{DateKnowledge.Known=>"已知",DateKnowledge.Unknown=>"未知",_=>"不适用"};
    private static string RecordStatusName(RecordStatus x)=>x==RecordStatus.Active?"有效":"已撤销";
    private static string ExpenseStatusName(ExpenseStatus x)=>x==ExpenseStatus.Active?"有效":"已作废";
    private static string ExpensePaymentName(ExpensePaymentStatus x)=>x==ExpensePaymentStatus.Paid?"已支付":"未支付";
    private static string TransactionName(TransactionType x)=>x==TransactionType.Receipt?"收款":"退款";
    private static string ShipmentName(ShipmentStatus x)=>x switch{ShipmentStatus.NotShipped=>"未寄出",ShipmentStatus.Shipped=>"已寄出",ShipmentStatus.Delivered=>"已签收",_=>"未知"};
    private static string SourceName(SourceKind x)=>x==SourceKind.HistoricalImport?"历史导入":"手动录入";
    private static Dictionary<string,string?> NormalizeChinese(Dictionary<string,string?> v,string sheet)
    {
        var map=sheet switch {"基本信息"=>new Dictionary<string,string>{{"订单编号","BusinessOrderNo"},{"项目名称","ProjectName"},{"项目类型","ProjectType"},{"接单日期","ReceivedDate"},{"截止日期","EffectiveDueDate"},{"客户平台","CustomerPlatform"},{"工程目录","ProjectPath"},{"提醒提前天数","ReminderDays"},{"备注","Notes"}},"完成进度"=>new Dictionary<string,string>{{"订单编号","BusinessOrderNo"},{"项目名称","ProjectName"},{"项目类型","ProjectType"},{"截止日期","EffectiveDueDate"},{"订单状态","WorkStatus"},{"当前阶段","CurrentStage"},{"完成比例","CompletionPercent"},{"当前工作","CurrentWork"},{"下一步","NextStep"},{"最近更新","LastUpdated"},{"完成日期","CompletedDate"},{"提醒状态","ReminderStatus"},{"工程目录","ProjectPath"},{"返工状态","ReworkStatus"},{"物流状态","ShipmentStatus"},{"快递单号","TrackingNumber"},{"寄出日期","ShippedDate"},{"收货日期","DeliveredDate"}},"收益记录"=>new Dictionary<string,string>{{"订单编号","BusinessOrderNo"},{"项目名称","ProjectName"},{"收入（原表金额）","QuotedAmount"},{"收入(原表金额)","QuotedAmount"},{"支出","ExpenseAmount"},{"利润","Profit"},{"支出记录状态","ExpenseStatus"},{"结款状态","PaymentStatus"},{"结款日期","PaidDate"},{"备注","Notes"}},_=>new()};
        var result=new Dictionary<string,string?>(StringComparer.OrdinalIgnoreCase); foreach(var (k,val) in v) result[map.GetValueOrDefault(k,k)]=val;
        foreach(var key in new[]{"ProjectPath"}) if(result.GetValueOrDefault(key)=="0")result[key]=null;
        return result;
    }
    private static string Hash(byte[] b)=>Convert.ToHexString(SHA256.HashData(b));
    private static void AddDifference(List<TransferDifference> results,ImportRowMapping mapping,TransferSourceRow incoming,WorkItem target,params string[] fields)
    {
        foreach(var field in fields)
        {
            var raw=incoming.Values.GetValueOrDefault(field);if(raw is null)continue;var prop=typeof(WorkItem).GetProperty(field);if(prop is null)continue;
            var normalized=field switch{"ReceivedDate" or "EffectiveDueDate" or "CompletedDate"=>DateTry(raw)?.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),"WorkStatus"=>WorkStatusTry(raw)?.ToString(),"ProjectType"=>ProjectTypeTry(raw).ToString(),_=>raw};
            var value = prop.GetValue(target);
            var existing=value is DateOnly storedDate ? storedDate.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)
                : Convert.ToString(value,CultureInfo.InvariantCulture);if(existing is "" )existing=null;
            if(!string.Equals(existing,normalized,StringComparison.OrdinalIgnoreCase))results.Add(new(mapping.SheetName,mapping.SourceRowNumber,"WorkItem",target.Id.ToString(),field,existing,normalized));
        }
    }
    private static bool SourceSnapshotMatches(string json,string sheetName,int rowNumber)
    {using var doc=System.Text.Json.JsonDocument.Parse(json);var root=doc.RootElement;return root.TryGetProperty("SheetName",out var sheet)&&sheet.GetString()==sheetName&&root.TryGetProperty("RowNumber",out var row)&&row.GetInt32()==rowNumber;}
    private static List<TransferDateIssue> BuildDateIssues(IReadOnlyList<TransferSheetPreview> sheets, IReadOnlyList<TransferRowDecision>? decisions = null)
    {
        var issues=new List<TransferDateIssue>();var dateFields=new Dictionary<string,string[]>(StringComparer.Ordinal){{"基本信息",["ReceivedDate","EffectiveDueDate"]},{"完成进度",["EffectiveDueDate","CompletedDate","ShippedDate","DeliveredDate"]},{"收益记录",["PaidDate"]}};
        foreach(var sheet in sheets.Where(x=>dateFields.ContainsKey(x.SheetName)))foreach(var row in sheet.Rows)foreach(var field in dateFields[sheet.SheetName])
        {var raw=row.Values.GetValueOrDefault(field);if(!string.IsNullOrWhiteSpace(raw)&&DateTry(raw) is null)issues.Add(new(sheet.SheetName,row.RowNumber,field,raw,"日期非空但无法解析为完整年份日期或Excel日期序列。","修正来源日期，或在预览为该字段填写明确日期；不推测年份。"));}
        var decisionsByRow = decisions?.ToDictionary(decision => (decision.SheetName, decision.RowNumber));
        var dateRows=sheets.Where(x=>x.SheetName is "基本信息" or "完成进度").SelectMany(s=>s.Rows.Select(r=>(Sheet:s.SheetName,Row:r,Date:DateTry(r.Values.GetValueOrDefault("EffectiveDueDate"))))).Where(x=>x.Date.HasValue)
            .GroupBy(x => decisionsByRow is not null && decisionsByRow.TryGetValue((x.Sheet, x.Row.RowNumber), out var decision)
                ? DecisionGroup(decision, x.Row) : Identity(x.Row.Values), StringComparer.OrdinalIgnoreCase);
        foreach(var group in dateRows.Where(g=>g.Select(x=>x.Date).Distinct().Count()>1))foreach(var item in group)issues.Add(new(item.Sheet,item.Row.RowNumber,"EffectiveDueDate",item.Row.Values.GetValueOrDefault("EffectiveDueDate"),"当前关联到同一工作项的来源行截止日期不一致。","在所有冲突来源行填写相同的确认日期，或修正关联后重新预览。",true));
        return issues;
    }
    private static DateOnly? DateOverride(TransferRowDecision d,string field)=>field switch{"ReceivedDate"=>d.ReceivedDateOverride,"EffectiveDueDate"=>d.DueDateOverride,"CompletedDate"=>d.CompletedDateOverride,"ShippedDate"=>d.ShippedDateOverride,"DeliveredDate"=>d.DeliveredDateOverride,"PaidDate"=>d.PaidDateOverride,_=>null};
    private static string DecisionGroup(TransferRowDecision d,TransferSourceRow row)=>string.IsNullOrWhiteSpace(d.NewWorkItemGroupKey)?Identity(row.Values):$"{row.Values.GetValueOrDefault("BusinessOrderNo")?.Trim()}\u001fmanual\u001f{d.NewWorkItemGroupKey.Trim()}";
    private static WorkItem NewHistoricalWork(IReadOnlyDictionary<string,string?> v,Guid batch)
    {
        string? Get(string k)=>v.GetValueOrDefault(k)?.Trim();
        var due=DateTry(Get("EffectiveDueDate")); var completed=DateTry(Get("CompletedDate")); var status=WorkStatusTry(Get("WorkStatus"))??WorkStatus.NotStarted;
        var shipRaw=Get("ShipmentStatus"); var requirement=shipRaw is null or "" or "待确认"?ShippingRequirement.Pending:shipRaw.Contains("无需")||shipRaw.Contains("不需要")?ShippingRequirement.None:ShippingRequirement.Required;
        var projectType=ProjectTypeTry(Get("ProjectType"));var schedule=projectType==ProjectType.PhysicalProduction?ScheduleCategory.Physical:projectType is ProjectType.Simulation or ProjectType.SchematicPcbDesign or ProjectType.CodeReport?ScheduleCategory.Digital:ScheduleCategory.Pending;
        var w=new WorkItem{ProjectName=Get("ProjectName")!,BusinessOrderNo=Get("BusinessOrderNo"),SourceKind=SourceKind.HistoricalImport,ImportBatchId=batch,ProjectType=projectType,ScheduleCategory=schedule,ScheduleCategorySource=ValueSource.Imported,ShippingRequirement=requirement,ShippingRequirementSource=ValueSource.Imported,ReceivedDate=DateTry(Get("ReceivedDate")),DueDateMode=due.HasValue?DueDateMode.Manual:DueDateMode.HistoricalUnknown,SpecifiedDueDate=due,EffectiveDueDate=due,WorkStatus=status,CompletedDate=completed,CompletedDateKnowledge=status==WorkStatus.Completed?(completed.HasValue?CompletionDateKnowledge.Known:CompletionDateKnowledge.Unknown):CompletionDateKnowledge.NotApplicable,CostConfirmationStatus=CostConfirmationStatus.Pending,OrderAmountCents=null,ProjectPath=Get("ProjectPath"),CurrentWork=Get("CurrentWork"),NextStep=Get("NextStep"),Notes=Get("Notes"),Version=1};
        return w;
    }
    private static string Identity(IReadOnlyDictionary<string,string?> v)=>$"{v.GetValueOrDefault("BusinessOrderNo")?.Trim()}\u001f{v.GetValueOrDefault("ProjectName")?.Trim()}";
    private static string SuggestedGroup(IReadOnlyDictionary<string,string?> v)=>$"{v.GetValueOrDefault("BusinessOrderNo")?.Trim()}｜{v.GetValueOrDefault("ProjectName")?.Trim()}";
    private static void MergeSupplemental(WorkItem w,IReadOnlyDictionary<string,string?> v){string? Get(string k)=>v.GetValueOrDefault(k); if(w.ProjectPath is null&&Get("ProjectPath") is {Length:>0} p&&p!="0")w.ProjectPath=p;if(w.CurrentWork is null)w.CurrentWork=Get("CurrentWork");if(w.NextStep is null)w.NextStep=Get("NextStep");if(w.Notes is null)w.Notes=Get("Notes");if(w.EffectiveDueDate is null&&DateTry(Get("EffectiveDueDate")) is { } due){w.DueDateMode=DueDateMode.Manual;w.SpecifiedDueDate=due;w.EffectiveDueDate=due;}var shipping=Get("ShipmentStatus");if(shipping is not null){w.ShippingRequirement=shipping.Contains("无需")||shipping.Contains("不需要")?ShippingRequirement.None:shipping is "待确认" or ""?ShippingRequirement.Pending:ShippingRequirement.Required;}}
    private static void AddPending(WorkItem w,string note)=>w.Notes=string.IsNullOrWhiteSpace(w.Notes)?note:$"{w.Notes}\n{note}";
    private static WorkStatus? WorkStatusTry(string? s)=>s?.Trim() switch{"已完成" or "完成"=>WorkStatus.Completed,"进行中" or "处理中"=>WorkStatus.InProgress,"未开始" or "待开始"=>WorkStatus.NotStarted,"已取消" or "取消"=>WorkStatus.Cancelled,"需返工" or "返工中" or "需要返工" or "待返工"=>WorkStatus.NeedsRework,_=>Enum.TryParse<WorkStatus>(s,true,out var parsed)?parsed:null};
    private static CancelledStatus? CancelledFromTry(string? s)=>s?.Trim() switch{"未开始"=>CancelledStatus.NotStarted,"进行中" or "处理中"=>CancelledStatus.InProgress,"已完成"=>CancelledStatus.Completed,"需返工" or "返工中"=>CancelledStatus.NeedsRework,_=>null};
    private static T EnumTry<T>(string? s,T fallback) where T:struct,Enum=>Enum.TryParse<T>(s,true,out var val)?val:fallback;
    private static ProjectType ProjectTypeTry(string? s)=>s switch{"实物制作"=>ProjectType.PhysicalProduction,"原理图/PCB"=>ProjectType.SchematicPcbDesign,"代码/报告" or "代码报告"=>ProjectType.CodeReport,"仿真"=>ProjectType.Simulation,_=>ProjectType.MixedOrUnknown};
    private static ShipmentStatus? ShipmentTry(string? s,DateOnly? shippedDate=null,DateOnly? deliveredDate=null)=>s switch{"已寄出"=>ShipmentStatus.Shipped,"已签收" or "已收货"=>ShipmentStatus.Delivered,"未寄出"=>ShipmentStatus.NotShipped,"待确认" or "" or null=>deliveredDate.HasValue?ShipmentStatus.Delivered:shippedDate.HasValue?ShipmentStatus.Shipped:null,_=>deliveredDate.HasValue?ShipmentStatus.Delivered:shippedDate.HasValue?ShipmentStatus.Shipped:null};
    private static long? MoneyCents(string? s){if(string.IsNullOrWhiteSpace(s))return null;if(!decimal.TryParse(s,NumberStyles.Number,CultureInfo.InvariantCulture,out var n))return null;if(n<0)throw new InvalidDataException($"金额不得为负数：{s}");if(decimal.Round(n,2)!=n)throw new InvalidDataException($"金额最多支持两位小数，原始值需核对：{s}");try{return checked(decimal.ToInt64(n*100m));}catch(OverflowException ex){throw new InvalidDataException($"金额超出允许范围：{s}",ex);}}
    private static DateOnly? DateTry(string? s)
    {
        if(string.IsNullOrWhiteSpace(s))return null;var formats=new[]{"yyyy-MM-dd","yyyy-M-d","yyyy.MM.dd","yyyy.M.d","yyyy/MM/dd","yyyy/M/d","yyyyMMdd","yyyy年M月d日","M/d/yyyy","MM/dd/yyyy","M-d-yyyy","MM-dd-yyyy","yyyy-MM-dd HH:mm:ss","yyyy/M/d H:mm:ss"};
        if(DateOnly.TryParseExact(s.Trim(),formats,CultureInfo.InvariantCulture,DateTimeStyles.None,out var date))return date;
        if(double.TryParse(s,NumberStyles.Number,CultureInfo.InvariantCulture,out var serial)&&serial>=0&&serial<=2958465){try{return DateOnly.FromDateTime(DateTime.FromOADate(serial));}catch(ArgumentException){return null;}}
        return null;
    }
    private static long? LongTry(string? s)=>long.TryParse(s,NumberStyles.Integer,CultureInfo.InvariantCulture,out var n)?n:null;
}
