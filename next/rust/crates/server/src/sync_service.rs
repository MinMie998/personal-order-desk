use desk_storage::ledger::Ledger;
use desk_sync::{protocol::{self,Snapshot,Merge,Result},remote};
use serde::{Serialize,Deserialize};
use serde_json::{Value,json};
use std::collections::BTreeMap;
#[cfg(test)]#[path="sync_tests.rs"]mod tests;
fn merge_mode()->String{"merge".into()}
#[derive(Clone,Serialize,Deserialize)]#[serde(rename_all="camelCase")]
struct Plan{#[serde(default)]rules:u32,local:Snapshot,head:remote::Head,backup_path:String,created_at:String,#[serde(default="merge_mode")]mode:String,#[serde(default)]target_generation:Option<String>}
#[derive(Clone,Serialize,Deserialize)]#[serde(rename_all="camelCase")]
struct SyncState{repository:String,key:String,baseline:Option<Snapshot>,last_head:Option<String>,last_synced_at:Option<String>,device_id:String,#[serde(default)]replacement_pending:bool,first_upload_approved:bool,pending:Option<Plan>}
impl SyncState{fn new(repository:String)->Result<Self>{Ok(Self{repository,key:protocol::new_key()?,baseline:None,last_head:None,last_synced_at:None,device_id:uuid::Uuid::new_v4().to_string(),replacement_pending:false,first_upload_approved:false,pending:None})}}
async fn load(db:&Ledger)->Result<Option<SyncState>>{let mut state:Option<SyncState>=db.read_sync_state().await?.map(|v|serde_json::from_value(v).map_err(|_|String::from("同步设置格式不兼容"))).transpose()?;if let Some(s)=state.as_mut(){if s.pending.as_ref().is_some_and(|p|p.rules!=2){s.pending=None;}}Ok(state)}
async fn save(db:&Ledger,state:&SyncState)->Result<()>{db.save_sync_state(&json!(state)).await}
fn check_repo(repo:&str,preview:bool)->Result<()>{if !remote::valid_repository(repo){return Err("请填写所有者/私有仓库名".into())}if preview{return Err("演示模式不连接远端，请先创建或打开自己的本机账本".into())}Ok(())}
fn counts(snapshot:&Snapshot)->Value{
 let orders:Vec<_>=snapshot.orders.values().filter(|o|o["deletedAt"].is_null()).collect();
 let mut received=0i128;let mut cost=0i128;let mut quote=0i128;let mut remaining=0i128;
 for o in &orders{let mut paid=0i128;for m in snapshot.money.values().filter(|m|m["orderId"]==o["id"]&&m["voidedAt"].is_null()){
  let amount=m["amountCents"].as_str().and_then(|v|v.parse::<i128>().ok()).unwrap_or(0);
  match m["kind"].as_str(){Some("receipt")=>paid+=amount,Some("refund")=>paid-=amount,Some("cost")=>cost+=amount,Some("cost-refund")=>cost-=amount,_=>{}}
 }received+=paid;if o["status"]!="cancelled"{let amount=o["quoteCents"].as_str().and_then(|v|v.parse::<i128>().ok()).unwrap_or(0);quote+=amount;remaining+=(amount-paid).max(0);}}
 json!({"orders":snapshot.orders.len(),"activeOrders":orders.len(),"money":snapshot.money.len(),"history":snapshot.history.len(),"tasks":snapshot.orders.values().map(|o|o["tasks"].as_array().map_or(0,|t|t.len())).sum::<usize>(),"quoteCents":quote.to_string(),"receivedCents":received.to_string(),"costCents":cost.to_string(),"profitCents":(received-cost).to_string(),"remainingCents":remaining.to_string()})
}
fn changes(before:&Snapshot,after:&Snapshot)->Value{
 let a=before.normalized();let b=after.normalized();let mut added=0;let mut updated=0;let mut trashed=0;let mut restored=0;let mut removed=0;
 for(id,row)in &b.orders{match a.orders.get(id){None=>added+=1,Some(old)if old!=row=>{if old["deletedAt"].is_null()&&!row["deletedAt"].is_null(){trashed+=1}else if !old["deletedAt"].is_null()&&row["deletedAt"].is_null(){restored+=1}else{updated+=1}},_=>{}}}
 for id in a.orders.keys(){if !b.orders.contains_key(id){removed+=1}}
 let money_added=b.money.keys().filter(|id|!a.money.contains_key(*id)).count();let money_changed=b.money.iter().filter(|(id,row)|a.money.get(*id).is_some_and(|old|old!=*row)).count();let money_removed=a.money.keys().filter(|id|!b.money.contains_key(*id)).count();
 json!({"addedOrders":added,"updatedOrders":updated,"trashedOrders":trashed,"restoredOrders":restored,"removedOrders":removed,"addedMoney":money_added,"changedMoney":money_changed,"removedMoney":money_removed})
}
fn duplicate_warnings(state:&SyncState,plan:&Plan)->Vec<Value>{
 let Some(remote)=&plan.head.snapshot else{return vec![]};let baseline=base(state,&plan.local.workspace_id);let mut warnings=vec![];
 for(id,local)in &plan.local.money{if baseline.money.contains_key(id)||!local["voidedAt"].is_null(){continue}
  for(other_id,other)in &remote.money{if id==other_id||baseline.money.contains_key(other_id)||!other["voidedAt"].is_null(){continue}
   if ["orderId","kind","amountCents","date"].iter().all(|key|local[*key]==other[*key]){let number=plan.local.orders.get(local["orderId"].as_str().unwrap_or("")).or_else(||remote.orders.get(local["orderId"].as_str().unwrap_or(""))).map(|o|o["number"].clone()).unwrap_or(Value::Null);warnings.push(json!({"number":number,"amountCents":local["amountCents"],"kind":local["kind"],"date":local["date"]}));if warnings.len()>=20{return warnings}}
  }
 }warnings
}
fn base(state:&SyncState,id:&str)->Snapshot{state.baseline.clone().unwrap_or_else(||Snapshot::empty(id))}
fn empty_device(s:&Snapshot)->bool{s.orders.is_empty()&&s.money.is_empty()}
fn reset_detected(state:&SyncState,plan:&Plan)->bool{
 plan.head.snapshot.as_ref().is_some_and(|r|state.baseline.as_ref().map_or(!empty_device(&plan.local),|b|b.generation!=r.generation||plan.local.generation!=r.generation))
}
fn result_for(state:&SyncState,plan:&Plan,choices:&BTreeMap<String,String>)->Result<Merge>{
 let mut result=if plan.mode=="reset"{Merge{snapshot:plan.local.clone(),conflicts:vec![]}}else if plan.mode=="download"{Merge{snapshot:plan.head.snapshot.clone().ok_or("远端尚无这本账本，不能取回替换")?,conflicts:vec![]}}else if reset_detected(state,plan){return Err("远端已整本重置或缺少共同版本，请先备份并接受远端账本；旧副本不会自动合并回去".into())}else if state.baseline.is_none()&&plan.head.snapshot.is_some()&&empty_device(&plan.local){Merge{snapshot:plan.head.snapshot.clone().unwrap(),conflicts:vec![]}}else{
  let mut remote=plan.head.snapshot.clone().unwrap_or_else(||Snapshot::empty(&plan.local.workspace_id));if plan.head.snapshot.is_none(){remote.generation=plan.local.generation.clone();}
  let mut baseline=base(state,&plan.local.workspace_id);if state.baseline.is_none(){baseline.generation=plan.local.generation.clone();}
  protocol::merge(&baseline,&plan.local,&remote,choices)?
 };
 result.snapshot.format=2;
 if plan.mode!="download"{if let Some(generation)=&plan.target_generation{result.snapshot.generation=generation.clone();}}
 Ok(result)
}
fn plan_summary(state:&SyncState)->Result<Value>{
 let Some(plan)=&state.pending else{return Ok(Value::Null)};let remote=plan.head.snapshot.clone().unwrap_or_else(||Snapshot::empty(&plan.local.workspace_id));
 let blocked=plan.mode=="merge"&&reset_detected(state,plan);
 let merged=if blocked{Merge{snapshot:remote.clone(),conflicts:vec![]}}else{result_for(state,plan,&BTreeMap::new())?};
 let initial_download=state.baseline.is_none()&&empty_device(&plan.local)&&plan.head.snapshot.is_some();
 let needs_upload=plan.mode!="download"&&!blocked&&(plan.head.snapshot.as_ref().is_none_or(|r|r.format<2)||merged.snapshot.digest()?!=remote.digest()?);
 Ok(json!({"duplicateWarnings":if plan.mode=="merge"{duplicate_warnings(state,plan)}else{vec![]},"mode":plan.mode,"replacement":plan.mode=="reset","resetDetected":blocked,"initialDownload":initial_download,"schemaUpgrade":plan.mode=="merge"&&plan.head.snapshot.as_ref().is_some_and(|r|r.format<2),"generation":merged.snapshot.generation,"createdAt":plan.created_at,"local":counts(&plan.local),"remote":counts(&remote),"merged":counts(&merged.snapshot),"upload":changes(&remote,&merged.snapshot),"download":changes(&plan.local,&merged.snapshot),"conflicts":merged.conflicts,"backupPath":plan.backup_path,"remoteExists":plan.head.snapshot.is_some(),"needsUpload":needs_upload,"firstUploadConfirmation":!state.first_upload_approved}))
}
pub async fn status(db:&Ledger)->Result<Value>{
 let Some(state)=load(db).await?else{return Ok(json!({"syncRules":2,"configured":false,"workspaceId":db.workspace_id()?,"defaultRepository":"","state":"not-configured"}))};
 let local=db.sync_snapshot().await?;let baseline=base(&state,&local.workspace_id);let delta=changes(&baseline,&local);
 Ok(json!({"syncRules":2,"workspaceId":local.workspace_id,"generation":local.generation,"deviceId":state.device_id,"configured":true,"replacementPending":state.replacement_pending,"repository":state.repository,"branch":remote::BRANCH,"lastHead":state.last_head,"lastSyncedAt":state.last_synced_at,"keyId":protocol::key_id(&state.key)?,"localChanges":local.digest()?!=baseline.digest()?,"changedOrders":delta["addedOrders"].as_u64().unwrap_or(0)+delta["updatedOrders"].as_u64().unwrap_or(0)+delta["trashedOrders"].as_u64().unwrap_or(0)+delta["restoredOrders"].as_u64().unwrap_or(0),"changedMoney":delta["addedMoney"].as_u64().unwrap_or(0)+delta["changedMoney"].as_u64().unwrap_or(0),"plan":plan_summary(&state)?,"runs":db.sync_runs().await?}))
}
pub async fn configure(db:&Ledger,payload:&Value,preview:bool)->Result<Value>{
 let repository=payload["repository"].as_str().unwrap_or("").trim().to_string();check_repo(&repository,preview)?;
 let state=match load(db).await?{Some(mut old)=>{if old.repository!=repository{old.repository=repository;old.baseline=None;old.last_head=None;old.last_synced_at=None;old.first_upload_approved=false;old.pending=None;}old},None=>SyncState::new(repository)?};save(db,&state).await?;status(db).await
}
pub async fn cancel(db:&Ledger)->Result<Value>{if let Some(mut state)=load(db).await?{state.pending=None;save(db,&state).await?;}status(db).await}
pub async fn prepare(db:&Ledger,payload:&Value,preview:bool)->Result<Value>{
 let mut state=load(db).await?.ok_or("请先保存同步仓库设置")?;check_repo(&state.repository,preview)?;
 let requested=payload["mode"].as_str().unwrap_or("merge");if !["merge","download","reset"].contains(&requested){return Err("不支持的同步操作".into())}
 let mut mode=if state.replacement_pending&&requested=="merge"{"reset"}else{requested};
 let backup_path=db.backup().await?;let local=db.sync_snapshot().await?;Ledger::validate_snapshot(&local)?;
 let repository=state.repository.clone();let key=state.key.clone();let id=local.workspace_id.clone();
 let head=tokio::task::spawn_blocking(move||remote::read(&repository,&id,&key)).await.map_err(|_|"远端检查未完成")??;
 if let Some(remote)=&head.snapshot{Ledger::validate_snapshot(remote)?;}
 if mode=="merge"&&state.baseline.is_none()&&empty_device(&local)&&head.snapshot.is_some(){mode="download";}
 if mode=="download"&&head.snapshot.is_none(){return Err("远端没有这本账本，未用空账本替换本机".into())}
 if mode!="reset"&&state.baseline.is_some()&&head.snapshot.is_none(){return Err("远端已确认的快照缺失，已停止同步；请检查仓库或恢复远端备份".into())}
 let upgrade=mode=="merge"&&head.snapshot.as_ref().map_or(local.generation=="legacy",|r|r.format<2);
 let target_generation=if mode=="reset"||upgrade{Some(uuid::Uuid::new_v4().to_string())}else{None};
 state.pending=Some(Plan{rules:2,local,head,backup_path,created_at:chrono::Local::now().to_rfc3339(),mode:mode.into(),target_generation});save(db,&state).await?;status(db).await
}
pub async fn commit(db:&Ledger,payload:&Value,preview:bool)->Result<Value>{
 let mut state=load(db).await?.ok_or("请先设置同步仓库")?;check_repo(&state.repository,preview)?;
 let plan=state.pending.clone().ok_or("请先检查并准备同步")?;let local=db.sync_snapshot().await?;
 if plan.local.digest()?!=local.digest()?{state.pending=None;save(db,&state).await?;return Err("准备同步后本地又有修改，请重新检查同步".into())}
 if plan.mode!="merge"&&payload["confirmReplacement"]!=true{return Err("请确认整本替换的方向和内容摘要".into())}
 let choices:BTreeMap<String,String>=serde_json::from_value(payload["choices"].clone()).map_err(|_|"冲突选择格式无效")?;
 let merged=result_for(&state,&plan,&choices)?;if !merged.conflicts.is_empty(){return Err("还有未处理的冲突，请逐项选择保留内容".into())}Ledger::validate_snapshot(&merged.snapshot)?;
 let repository=state.repository.clone();let key=state.key.clone();let id=local.workspace_id.clone();
 let current=tokio::task::spawn_blocking(move||remote::read(&repository,&id,&key)).await.map_err(|_|"远端检查未完成")??;
 if current.sha!=plan.head.sha{state.pending=None;save(db,&state).await?;return Err("远端刚有新版本，请重新检查同步；本次没有覆盖双方记录".into())}
 let upload=plan.mode!="download"&&current.snapshot.as_ref().map_or(true,|r|r.format<2||merged.snapshot.digest().ok()!=r.digest().ok());
 if upload&&!preview&&!state.first_upload_approved{if payload["confirmFirstUpload"]!=true{return Err("首次上传前，请确认内容摘要和目标私有仓库".into())}state.first_upload_approved=true;}
 let head=if upload{let repository=state.repository.clone();let key=state.key.clone();let head=plan.head.clone();let snapshot=merged.snapshot.clone();tokio::task::spawn_blocking(move||remote::publish(&repository,&head,&snapshot,&key)).await.map_err(|_|"远端发布尚未完成，请重新检查")??}else{current.sha};
 let summary=json!({"mode":plan.mode,"upload":if upload{changes(&current.snapshot.clone().unwrap_or_else(||Snapshot::empty(&local.workspace_id)),&merged.snapshot)}else{json!(null)},"download":changes(&local,&merged.snapshot),"totals":counts(&merged.snapshot)});
 state.replacement_pending=false;state.baseline=Some(merged.snapshot.clone());state.last_head=Some(head.clone());state.last_synced_at=Some(chrono::Local::now().to_rfc3339());state.pending=None;
 if plan.mode=="download"{db.replace_ledger(&merged.snapshot,Some(&json!(state)),&local.digest()?,Some(&json!({"mode":plan.mode,"remoteHead":head,"summary":summary}))).await?;}else{db.apply_sync(&merged.snapshot,&json!(state),&json!({"mode":plan.mode,"remoteHead":head,"summary":summary})).await?;}
 let mut result=status(db).await?;result["result"]=json!(if upload{"uploaded-and-confirmed"}else{"downloaded-or-already-current"});result["summary"]=summary;Ok(result)
}
pub async fn export_recovery(db:&Ledger)->Result<Value>{let state=load(db).await?.ok_or("尚未设置同步")?;Ok(json!({"format":"personal-order-desk.sync-recovery","version":1,"workspaceId":db.workspace_id()?,"repository":state.repository,"key":state.key,"keyId":protocol::key_id(&state.key)?}))}
pub async fn import_recovery(db:&Ledger,payload:&Value,preview:bool)->Result<Value>{
 let value:Value=serde_json::from_str(payload["recovery"].as_str().unwrap_or("")).map_err(|_|"恢复密钥文件内容格式无效")?;
 if value["format"]!="personal-order-desk.sync-recovery"||value["version"]!=1{return Err("恢复密钥版本不兼容".into())}
 let repo=value["repository"].as_str().ok_or("恢复密钥缺少仓库")?.to_string();check_repo(&repo,preview)?;let key=value["key"].as_str().ok_or("恢复密钥内容不完整")?.to_string();
 if value["keyId"]!=protocol::key_id(&key)?{return Err("恢复密钥校验不通过".into())}let id=value["workspaceId"].as_str().ok_or("恢复密钥缺少账本标识")?;
 db.backup().await?;db.adopt_sync_identity(id).await?;
 let mut state=SyncState::new(repo)?;state.key=key;save(db,&state).await?;status(db).await
}

pub async fn replace_local(db:&Ledger,payload:&Value)->Result<Value>{
 if payload["confirmReplace"]!=true{return Err("请明确确认替换当前账本".into())}
 let snapshot:Snapshot=serde_json::from_value(payload["snapshot"].clone()).map_err(|_|"迁入快照格式无效")?;
 let expected=payload["expectedDigest"].as_str().ok_or("缺少原账本摘要")?;
 let mut state=load(db).await?;
 if let Some(s)=state.as_mut(){s.pending=None;s.replacement_pending=true;}
 let serialized=state.as_ref().map(|s|json!(s));
 let backup=db.replace_ledger(&snapshot,serialized.as_ref(),expected,None).await?;
 Ok(json!({"replaced":true,"backupPath":backup,"counts":counts(&snapshot)}))
}
