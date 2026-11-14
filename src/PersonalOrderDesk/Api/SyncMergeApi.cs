using System.Collections.Concurrent;
using System.Text.Json;
using PersonalOrderDesk.Client;
using PersonalOrderDesk.Services;
namespace PersonalOrderDesk.Api;
public sealed class SyncMergePlans
{
    public sealed record Plan(SyncMergeRequest Input,SyncReview Review,DateTimeOffset Expires)
    {public SemaphoreSlim Gate {get;}=new(1,1);public bool Applied {get;set;}}
    private readonly ConcurrentDictionary<Guid,Plan> plans=new();
    public void Add(Plan plan){foreach(var old in plans.Where(x=>x.Value.Expires<DateTimeOffset.UtcNow))plans.TryRemove(old.Key,out _);if(plans.Count>=10)throw new InvalidOperationException("待处理同步过多，请完成已有预览或稍后重试。");if(!plans.TryAdd(plan.Input.OperationId,plan))throw new InvalidOperationException("该同步编号已有预览，请重新开始。");}
    public Plan Get(Guid id)=>plans.TryGetValue(id,out var plan)&&plan.Expires>DateTimeOffset.UtcNow?plan:throw new InvalidOperationException("同步预览已过期或电脑重启，请重新预览。");
}
public static class SyncMergeApi
{
    public static void MapSyncMerge(this WebApplication app,string database,string dataDirectory)
    {
        var api=app.MapGroup("/api/v1/sync").AddEndpointFilter<ClientApiErrors>();
        api.MapPost("/preview",async(SyncMergeRequest request,ClientApiIdentity identity,SyncMergePlans plans,CancellationToken ct)=>{
            if(request.InstanceId!=identity.Info.InstanceId)throw new InvalidOperationException("同步来源不是当前电脑，未修改账本。");
            var backup=Path.Combine(dataDirectory,"Backups",$"sync-before-{request.OperationId:N}.db");
            if(!File.Exists(backup)||!File.Exists(backup+".manifest.json"))throw new InvalidOperationException("电脑同步前备份未完成，不能进入合并。");
            var current=await ThreeWaySync.Read(database,ct);
            if(ThreeWaySync.Fingerprint(await ThreeWaySync.Read(backup,ct))!=ThreeWaySync.Fingerprint(current))throw new InvalidOperationException("备份后电脑又有改动，请重新发起同步，确保保护备份包含全部最新数据。");
            var result=ThreeWaySync.Merge(request.Baseline,current,request.Phone);
            var review=new SyncReview(request.OperationId,ThreeWaySync.Fingerprint(current),result.PcChanges,result.PhoneChanges,result.Conflicts,result.Warnings);
            plans.Add(new(request,review,DateTimeOffset.UtcNow.AddMinutes(20)));return review;
        });
        api.MapPost("/commit",async(SyncCommitRequest request,SyncMergePlans plans,IBusinessClock clock,CancellationToken ct)=>{
            if(!request.Confirm)throw new ArgumentException("请核对同步内容并确认。");
            var plan=plans.Get(request.OperationId);await plan.Gate.WaitAsync(ct);
            try{
                if(plan.Applied)return true;
                if(plan.Review.Conflicts.Any(c=>!request.Choices.TryGetValue(c.Id,out var choice)||choice is not("pc" or "phone")))throw new ArgumentException("每处冲突都需要选择保留电脑还是手机内容。");
                var current=await ThreeWaySync.Read(database,ct);if(ThreeWaySync.Fingerprint(current)!=plan.Review.PcFingerprint)throw new InvalidOperationException("预览后电脑数据已变化，请重新同步预览。");
                var merged=ThreeWaySync.Merge(plan.Input.Baseline,current,plan.Input.Phone,request.Choices);
                await ThreeWaySync.Apply(database,plan.Review.PcFingerprint,merged.Data,clock.Today,ct);plan.Applied=true;
                var history=Path.Combine(dataDirectory,"Backups",$"sync-before-{request.OperationId:N}.result.json");
                await File.WriteAllTextAsync(history,JsonSerializer.Serialize(new{OperationId=request.OperationId,AppliedAt=DateTimeOffset.UtcNow,plan.Review.PcChanges,plan.Review.PhoneChanges,Choices=request.Choices},DeskJson.Options),ct);
                return true;
            }finally{plan.Gate.Release();}
        });
    }
}
