use axum::{routing::{get,post},http::{HeaderMap,StatusCode},extract::{State,Path},Json,Router};
use serde_json::{json,Value};
use std::{sync::{Arc,Mutex},path::PathBuf};
use desk_storage::ledger::{Ledger,Order,MoneyEntry};
mod sync_service;
#[derive(Clone)]struct Control{token:Option<String>,stop:Arc<Mutex<Option<tokio::sync::oneshot::Sender<()>>>>,instance:String,root:PathBuf,registration_root:PathBuf,ledger:Arc<tokio::sync::RwLock<Option<Ledger>>>,startup_error:Option<String>,operations:Arc<tokio::sync::Mutex<()>>}
type ApiResult=Result<Json<Value>,(StatusCode,Json<Value>)>;
fn error(message:impl Into<String>)->(StatusCode,Json<Value>){(StatusCode::BAD_REQUEST,Json(json!({"error":message.into()})))}
fn allowed(c:&Control,h:&HeaderMap)->bool{
 if c.token.as_deref().is_some_and(|t|h.get("X-Desk-Control").and_then(|v|v.to_str().ok())==Some(t)){return true}
 let value=|name:&str|h.get(name).and_then(|v|v.to_str().ok()).unwrap_or("");
 value("Host")=="127.0.0.1:15059"&&value("Sec-Fetch-Site")=="same-origin"&&value("X-Desk-Request")=="1"&&(value("Origin").is_empty()||value("Origin")=="http://127.0.0.1:15059")
}
fn authorize(c:&Control,h:&HeaderMap)->Result<(),(StatusCode,Json<Value>)>{if allowed(c,h){Ok(())}else{Err((StatusCode::UNAUTHORIZED,Json(json!({"error":"请求未通过本机访问校验，请从工作台重新打开"}))))}}
#[tokio::main]async fn main()->Result<(),Box<dyn std::error::Error>>{
 let root=std::env::var_os("DESK_NEXT_DATA_DIR").map(PathBuf::from).unwrap_or_else(||PathBuf::from(std::env::var_os("LOCALAPPDATA").unwrap_or_default()).join("PersonalOrderDesk.Next"));
 let registration_root=root.clone();
 let (root,ledger,startup_error)=match desk_storage::location::configured(&root){Ok(selected)=>{let (book,error)=match Ledger::open(&selected).await{Ok(v)=>(v,None),Err(e)=>(None,Some(e))};(selected,book,error)},Err(e)=>(root,None,Some(e))};
 let (tx,rx)=tokio::sync::oneshot::channel();
 let state=Control{token:std::env::var("DESK_R0_SHUTDOWN_TOKEN").ok(),stop:Arc::new(Mutex::new(Some(tx))),instance:uuid::Uuid::new_v4().to_string(),root,registration_root,ledger:Arc::new(tokio::sync::RwLock::new(ledger)),startup_error,operations:Arc::new(tokio::sync::Mutex::new(()))};
 let beside=std::env::current_exe()?.parent().unwrap().join("web");let web=if beside.is_dir(){beside}else{PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../../web/dist")};
 let app=Router::new().route("/healthz",get(health)).route("/api/v3/capabilities",get(capabilities)).route("/api/v3/workspace",get(workspace)).route("/api/v3/snapshot",get(snapshot)).route("/api/v3/backups",get(backups)).route("/api/v3/action",post(action)).route("/api/v3/history/{id}",get(history)).route("/r0/control-check",post(control_check)).route("/r0/shutdown",post(shutdown)).fallback_service(tower_http::services::ServeDir::new(web)).with_state(state);
 let port=std::env::var("DESK_SERVER_PORT").unwrap_or_else(|_|"15059".into()).parse::<u16>()?;if port<1024{return Err("Server port is outside the supported range".into())}
 let listener=tokio::net::TcpListener::bind(("127.0.0.1",port)).await?;
 axum::serve(listener,app).with_graceful_shutdown(async{tokio::select!{_=tokio::signal::ctrl_c()=>{},_=rx=>{}}}).await?;Ok(())
}
async fn health(State(c):State<Control>)->Json<Value>{Json(json!({"product":"PersonalOrderDesk","version":env!("CARGO_PKG_VERSION"),"stage":"R1","dataAccess":true,"instanceId":c.instance}))}
async fn capabilities()->Json<Value>{Json(json!({"apiVersion":3,"moneyEncoding":"decimal-string","remoteAuthoritative":true,"ledgerWrites":true,"syncImplemented":true,"syncFormat":2,"syncRules":2,"supportedSnapshotFormats":[1,2],"taskWorkflow":1}))}
async fn workspace(State(c):State<Control>,h:HeaderMap)->ApiResult{authorize(&c,&h)?;let _gate=c.operations.lock().await;Ok(Json(json!({"initialized":c.ledger.read().await.is_some(),"dataDirectory":c.ledger.read().await.as_ref().map(|d|d.directory()).unwrap_or(&c.root).to_string_lossy(),"error":if c.ledger.read().await.is_some(){None}else{c.startup_error.clone()}})))}
async fn snapshot(State(c):State<Control>,h:HeaderMap)->ApiResult{authorize(&c,&h)?;let _gate=c.operations.lock().await;if c.ledger.read().await.is_none(){if let Some(e)=&c.startup_error{return Err(error(e))}}let db=c.ledger.read().await.clone().ok_or_else(||error("请先创建本地工作空间"))?;let mut value=db.snapshot().await.map_err(error)?;value["taskWorkflow"]=json!(1);value["previewMode"]=json!(std::env::var("DESK_PREVIEW_MODE").ok().as_deref()==Some("1"));Ok(Json(value))}
async fn history(State(c):State<Control>,h:HeaderMap,Path(id):Path<String>)->ApiResult{authorize(&c,&h)?;let _gate=c.operations.lock().await;let db=c.ledger.read().await.clone().ok_or_else(||error("请先创建工作空间"))?;Ok(Json(json!(db.history(&id).await.map_err(error)?)))}
async fn action(State(c):State<Control>,h:HeaderMap,Json(request):Json<Value>)->ApiResult{
 authorize(&c,&h)?;let _gate=c.operations.lock().await;
 let name=request["action"].as_str().unwrap_or("");let p=&request["payload"];
 if let Some(current)=c.ledger.read().await.as_ref(){if request["workspaceId"].as_str()!=Some(current.workspace_id().map_err(error)?.as_str()){return Err(error("工作空间已经变化，请刷新页面后重试"))}}
 if name=="change-directory"{
  let target=PathBuf::from(p["directory"].as_str().ok_or_else(||error("请选择数据目录"))?);
  if !target.is_absolute(){return Err(error("请填写完整文件夹路径"))}
  let mode=p["mode"].as_str().unwrap_or("");
  let previous=c.ledger.read().await.clone();
  let next=match mode{
   "relocate"=>previous.as_ref().ok_or_else(||error("没有可迁移的工作空间"))?.relocate(&target).await.map_err(error)?,
   "open"=>{if let Some(book)=&previous{book.backup().await.map_err(error)?;}Ledger::open(&target).await.map_err(error)?.ok_or_else(||error("所选目录没有完整账本，没有创建空库"))?},
   "create"=>{if previous.is_some(){return Err(error("当前已有账本，请使用迁移目录或打开已有账本"))}Ledger::create(&target).await.map_err(error)?},
   _=>return Err(error("请选择目录操作类型")),
  };
  desk_storage::location::register(&c.registration_root,&target).map_err(error)?;
  *c.ledger.write().await=Some(next);return Ok(Json(json!({"changed":true})))
 }
 if c.ledger.read().await.is_none(){if let Some(e)=&c.startup_error{return Err(error(e))}}
 if name=="create-workspace"{let mut guard=c.ledger.write().await;if guard.is_some(){return Err(error("工作空间已经存在，没有重复创建"))}*guard=Some(Ledger::create(&c.root).await.map_err(error)?);return Ok(Json(json!({"created":true})))}
 let db=c.ledger.read().await.clone().ok_or_else(||error("请先创建本地工作空间"))?;
 if name.starts_with("sync-"){
  let preview=std::env::var("DESK_PREVIEW_MODE").ok().as_deref()==Some("1");
  let result=match name{
   "sync-status"=>sync_service::status(&db).await,
   "sync-configure"=>sync_service::configure(&db,p,preview).await,
   "sync-prepare"=>sync_service::prepare(&db,p,preview).await,
   "sync-commit"=>sync_service::commit(&db,p,preview).await,
   "sync-cancel"=>sync_service::cancel(&db).await,
   "sync-export-recovery"=>sync_service::export_recovery(&db).await,
   "sync-import-recovery"=>sync_service::import_recovery(&db,p,preview).await,
   _=>Err("未知同步操作".into()),
  };
  return result.map(Json).map_err(error)
 }
 match name{
  "replace-ledger"=>sync_service::replace_local(&db,p).await.map(Json).map_err(error),
  "ledger-digest"=>Ok(Json(json!({"digest":db.sync_snapshot().await.map_err(error)?.digest().map_err(error)?}))),
  "save-preferences"=>{let value:desk_storage::ledger::Preferences=serde_json::from_value(p.clone()).map_err(|_|error("设置内容无效"))?;db.save_preferences(value).await.map_err(error)?;Ok(Json(json!({"saved":true})))},
  "save-order"=>{let order:Order=serde_json::from_value(p.clone()).map_err(|_|error("订单字段不完整，请刷新重试"))?;Ok(Json(json!(db.save_order(order).await.map_err(error)?)))},
  "trash-order"|"restore-order"=>{db.trash(p["id"].as_str().ok_or_else(||error("缺少订单标识"))?,p["revision"].as_i64().ok_or_else(||error("缺少订单版本"))?,name=="restore-order").await.map_err(error)?;Ok(Json(json!({"saved":true})))},
  "add-money"=>{let entry:MoneyEntry=serde_json::from_value(p.clone()).map_err(|_|error("收支字段不完整"))?;Ok(Json(json!(db.add_money(entry).await.map_err(error)?)))},
  "void-money"=>{db.void_money(p["id"].as_str().ok_or_else(||error("缺少流水标识"))?,p["reason"].as_str().unwrap_or("")).await.map_err(error)?;Ok(Json(json!({"saved":true})))},
  "restore-backup"=>Ok(Json(json!({"protectionBackup":db.restore(p["name"].as_str().ok_or_else(||error("请选择备份"))?).await.map_err(error)?}))),
  "backup"=>Ok(Json(json!({"path":db.backup().await.map_err(error)?}))),
  _=>Err(error("未知操作")),
 }
}
async fn shutdown(State(c):State<Control>,headers:HeaderMap)->StatusCode{
 if !is_controller(&c,&headers){return StatusCode::UNAUTHORIZED}
 match c.stop.lock(){Ok(mut tx)=>{if let Some(tx)=tx.take(){let _=tx.send(());}StatusCode::ACCEPTED},Err(_)=>StatusCode::INTERNAL_SERVER_ERROR}
}
fn is_controller(c:&Control,h:&HeaderMap)->bool{c.token.as_deref().is_some_and(|t|h.get("X-Desk-Control").and_then(|v|v.to_str().ok())==Some(t))}
async fn control_check(State(c):State<Control>,headers:HeaderMap)->StatusCode{if is_controller(&c,&headers){StatusCode::OK}else{StatusCode::UNAUTHORIZED}}

async fn backups(State(c):State<Control>,h:HeaderMap)->ApiResult{authorize(&c,&h)?;let _gate=c.operations.lock().await;let db=c.ledger.read().await.clone().ok_or_else(||error("请先创建工作空间"))?;Ok(Json(json!(db.backups().map_err(error)?)))}
