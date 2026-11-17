#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
use std::{process::{Command,Stdio},sync::{Arc,Mutex},time::Duration};
mod control;
mod github;
mod files;
mod updates;
use serde_json::{json,Value};
const BASE:&str="http://127.0.0.1:15059";
#[derive(Default)] struct Service { token:Option<String> }
fn client()->Result<reqwest::blocking::Client,String>{reqwest::blocking::Client::builder().no_proxy().timeout(Duration::from_secs(2)).build().map_err(|e|e.to_string())}
fn probe()->Result<Value,String>{
 let v:Value=client()?.get(format!("{BASE}/healthz")).send().map_err(|_|"本机后台未启动或连接超时，请点击启动后台。".to_string())?.error_for_status().map_err(|e|e.to_string())?.json().map_err(|e|e.to_string())?;
 if v["product"]!="PersonalOrderDesk"||v["stage"]!="R1"||v["version"]!=env!("CARGO_PKG_VERSION")||v["dataAccess"]!=true{return Err("15059端口的服务身份或版本不匹配，未结束其他进程。".into())} Ok(v)
}
// Rebind after a controlled backend restart only when the current process proves
// possession of the controller credential already protected for this Windows user.
fn connected_record()->Result<control::Record,String>{
 let _lock=control::lock()?;
 let mut record=control::read()?.ok_or("后台连接凭据缺失，请点击重新打开以连接后台")?;
 let health=probe()?;
 if health["instanceId"]!=record.instance{
  let response=client()?.post(format!("{BASE}/r0/control-check")).header("X-Desk-Control",&record.token).send().map_err(|_|"后台正在重新连接，请稍后重试")?;
  if !response.status().is_success(){return Err(format!("后台控制校验失败（HTTP {}，登记实例 {}，后台实例 {}，凭据目录 {}）。已有账本未被修改",response.status(),record.instance,health["instanceId"].as_str().unwrap_or("unknown"),std::env::var("LOCALAPPDATA").unwrap_or_default()))}
  record.instance=health["instanceId"].as_str().ok_or("后台实例标识缺失")?.to_owned();control::save(&record)?;
 }
 Ok(record)
}
#[tauri::command] async fn service_status()->Result<Value,String>{tauri::async_runtime::spawn_blocking(probe).await.map_err(|_|"后台检查未完成")?}
#[tauri::command] async fn start_service(state:tauri::State<'_,Arc<Mutex<Service>>>)->Result<Value,String>{let state=state.inner().clone();tauri::async_runtime::spawn_blocking(move||start_service_inner(&state)).await.map_err(|_|"后台启动未完成")?}
fn start_service_inner(state:&Mutex<Service>)->Result<Value,String>{
 let mut state=state.lock().map_err(|_|"服务状态不可用")?;
 let _lock=control::lock()?;
 if let Ok(v)=probe(){
  state.token=None;
  if let Some(mut record)=control::read()?{
   let response=client()?.post(format!("{BASE}/r0/control-check")).header("X-Desk-Control",&record.token).send().map_err(|e|e.to_string())?;
   if response.status().is_success(){if v["instanceId"]!=record.instance{record.instance=v["instanceId"].as_str().ok_or("后台实例标识缺失")?.to_owned();control::save(&record)?;}state.token=Some(record.token);}
  }
  return Ok(json!({"status":"reused","health":v,"canStop":state.token.is_some()}))
 }
 if std::net::TcpStream::connect_timeout(&"127.0.0.1:15059".parse().unwrap(),Duration::from_millis(300)).is_ok(){return Err("15059端口已被其他服务占用，没有结束进程或另建账本。".into())}
 let path=std::env::current_exe().map_err(|e|e.to_string())?.parent().ok_or("缺少程序目录")?.join("desk-server.exe");
 let pending=path.parent().ok_or("缺少程序目录")?.join(".update").join("desk-server.exe");
 if pending.is_file(){
  // Apply a staged update only after no backend is listening. Never stop a
  // running process to update it; Windows rename replaces the file atomically.
  std::fs::rename(&pending,&path).map_err(|_|"后台更新尚未完成，请先在设置中退出后台，再关闭并重新打开工作台")?;
  if let Some(folder)=pending.parent(){let _=std::fs::remove_dir(folder);}
 }
 if !path.is_file(){return Err("程序文件不完整，请重新解压完整Windows程序包。".into())}
 let token=uuid::Uuid::new_v4().to_string();let mut command=Command::new(path);
 command.env("DESK_R0_SHUTDOWN_TOKEN",&token).stdout(Stdio::null()).stderr(Stdio::null());
 #[cfg(windows)]{use std::os::windows::process::CommandExt;command.creation_flags(0x08000000);}
 let mut child=command.spawn().map_err(|e|e.to_string())?;
 for _ in 0..50 {
  if let Some(status)=child.try_wait().map_err(|e|e.to_string())?{return Err(format!("本机后台提前退出：{status}"))}
  if let Ok(v)=probe(){control::save(&control::Record{instance:v["instanceId"].as_str().ok_or("后台缺少实例标识")?.to_string(),token:token.clone()})?;state.token=Some(token);return Ok(json!({"status":"started","health":v,"canStop":true}))}
  std::thread::sleep(Duration::from_millis(100));
 }
 Err("本机后台启动超时；未强制结束进程，请检查端口与日志。".into())
}
#[tauri::command] async fn stop_service(state:tauri::State<'_,Arc<Mutex<Service>>>)->Result<Value,String>{let state=state.inner().clone();tauri::async_runtime::spawn_blocking(move||stop_service_inner(&state)).await.map_err(|_|"后台退出未完成")?}
fn stop_service_inner(state:&Mutex<Service>)->Result<Value,String>{
 let mut state=state.lock().map_err(|_|"服务状态不可用")?;
 let _lock=control::lock()?;
 let token=state.token.as_ref().ok_or("当前窗口尚未验证控制权限，请先点击启动或复用后台。")?;
 probe()?;
 client()?.post(format!("{BASE}/r0/shutdown")).header("X-Desk-Control",token).send().map_err(|e|e.to_string())?.error_for_status().map_err(|e|e.to_string())?;
 control::clear()?;state.token=None;Ok(json!({"status":"shutdown-requested"}))
}
fn main(){
 if std::env::args().nth(1).as_deref()==Some("--repair-connection"){
  let result=(||->Result<Value,String>{
   let path=std::env::args().nth(2).ok_or("缺少加密凭据交接文件")?;
   let bytes=std::fs::read(path).map_err(|_|"无法读取加密凭据交接文件")?;
   if bytes.len()>65536{return Err("凭据交接文件过大".into())}
   let record:control::Record=serde_json::from_slice(&control::unseal(&bytes)?).map_err(|_|"凭据交接内容无效")?;
   let health=probe()?;
   if health["instanceId"]!=record.instance{return Err("交接凭据对应的后台已变化，没有修改本机登记".into())}
   let response=client()?.post(format!("{BASE}/r0/control-check")).header("X-Desk-Control",&record.token).send().map_err(|_|"后台控制检查未完成")?;
   if !response.status().is_success(){return Err("交接凭据未通过当前后台校验，没有修改本机登记".into())}
   let _lock=control::lock()?;control::save(&record)?;
   Ok(json!({"repaired":true,"instance":record.instance}))
  })();
  if let Some(path)=std::env::args().nth(3){let _=std::fs::write(path,json!({"result":result}).to_string());}return;
 }
 if std::env::args().nth(1).as_deref()==Some("--diagnose-connection"){
  let mut result=json!({"localAppData":std::env::var("LOCALAPPDATA").ok(),"userProfile":std::env::var("USERPROFILE").ok(),"cwd":std::env::current_dir().ok(),"exe":std::env::current_exe().ok()});
  match control::read(){Ok(Some(record))=>{result["registeredInstance"]=json!(record.instance);match probe(){Ok(health)=>{result["health"]=health;result["controlStatus"]=json!(client().and_then(|c|c.post(format!("{BASE}/r0/control-check")).header("X-Desk-Control",record.token).send().map(|r|r.status().as_u16()).map_err(|e|e.to_string())));},Err(e)=>result["probeError"]=json!(e)}},Ok(None)=>result["recordMissing"]=json!(true),Err(e)=>result["recordError"]=json!(e)}
  if let Some(path)=std::env::args().nth(2){let _=std::fs::write(path,result.to_string());}return;
 }
 tauri::Builder::default().plugin(tauri_plugin_dialog::init()).manage(Arc::new(Mutex::new(Service::default()))).invoke_handler(tauri::generate_handler![service_status,start_service,stop_service,github::github_auth,github::open_github_login,desk_request,open_workbench_web,open_project_folder,files::choose_folder,files::save_export,updates::check_public_update,updates::open_release_url]).run(tauri::generate_context!()).expect("Tauri窗口启动失败");}

#[tauri::command]fn open_workbench_web()->Result<(),String>{github::open_fixed_url("http://127.0.0.1:15059/")}
#[tauri::command]fn open_project_folder(path:String)->Result<(),String>{let folder=std::path::Path::new(&path);if !folder.is_absolute()||!folder.is_dir(){return Err("工程目录不存在，请填写完整文件夹路径".into())}github::open_fixed_url(&path)}

#[tauri::command]
async fn desk_request(path:String,body:Option<Value>)->Result<Value,String>{
 tauri::async_runtime::spawn_blocking(move||{
  if !["/api/v3/workspace","/api/v3/snapshot","/api/v3/backups","/api/v3/action"].contains(&path.as_str())&&!path.strip_prefix("/api/v3/history/").is_some_and(|id|uuid::Uuid::parse_str(id).is_ok()){return Err("请求路径不允许".into())}
  let record=connected_record()?;
  let http=reqwest::blocking::Client::builder().no_proxy().timeout(Duration::from_secs(300)).build().map_err(|_|"无法建立本机连接")?;
  let response=match body{Some(body)=>http.post(format!("{BASE}{path}")).header("X-Desk-Control",record.token).json(&body).send(),None=>http.get(format!("{BASE}{path}")).header("X-Desk-Control",record.token).send()}.map_err(|_|"无法连接本机后台，请重新打开工作台")?;
  let success=response.status().is_success();let value:Value=response.json().map_err(|_|"本机后台返回格式无效")?;
  if !success{return Err(value["error"].as_str().unwrap_or("操作未完成").into())}Ok(value)
 }).await.map_err(|_|"本机操作未完成")?
}
