use serde_json::{Value,json};
use base64::{Engine,engine::general_purpose::STANDARD};
const API:&str="https://api.github.com/repos/MinMie998/personal-order-desk/contents/updates/latest.json";
const RELEASES:&str="https://github.com/MinMie998/personal-order-desk/releases/";

pub fn valid_release_url(url:&str)->bool{url.starts_with(RELEASES)&&!url.chars().any(|c|c.is_control()||c.is_whitespace())}
#[tauri::command]
pub async fn check_public_update()->Result<Value,String>{
 tauri::async_runtime::spawn_blocking(||{
  let client=reqwest::blocking::Client::builder().timeout(std::time::Duration::from_secs(20)).connect_timeout(std::time::Duration::from_secs(10)).redirect(reqwest::redirect::Policy::none()).user_agent("PersonalOrderDesk-Updater").build().map_err(|_|"无法建立更新连接")?;
  let response=client.get(API).header("Accept","application/vnd.github+json").send().map_err(|_|"暂时无法检查更新，本机账本不受影响")?;
  if response.status().as_u16()==404{return Ok(json!({"available":false,"currentVersion":env!("CARGO_PKG_VERSION"),"message":"发行信息尚未发布"}))}
  if !response.status().is_success(){return Err("GitHub暂时无法提供更新，请稍后重试".to_string())}
  let object:Value=response.json().map_err(|_|"更新响应格式无效")?;
  let encoded=object["content"].as_str().ok_or("缺少更新信息")?;
  if encoded.len()>131072{return Err("更新信息过大".into())}
  let bytes=STANDARD.decode(encoded.split_whitespace().collect::<String>()).map_err(|_|"更新信息编码无效")?;
  let value:Value=serde_json::from_slice(&bytes).map_err(|_|"更新信息格式无效")?;
  if value["format"]!="personal-order-desk.update"||value["version"]!=1||value["product"]!="PersonalOrderDesk"{return Err("更新来源不兼容".into())}
  let asset=&value["windows"];
  if !valid_release_url(asset["url"].as_str().unwrap_or(""))||asset["sha256"].as_str().is_none_or(|s|s.len()!=64||!s.bytes().all(|b|b.is_ascii_hexdigit())){return Err("更新下载信息校验失败".into())}
  let latest=semver::Version::parse(value["appVersion"].as_str().ok_or("更新版本号缺失")?).map_err(|_|"更新版本号无效")?;
  let current=semver::Version::parse(env!("CARGO_PKG_VERSION")).map_err(|_|"本机版本号无效")?;
  Ok(json!({"available":latest>current,"currentVersion":env!("CARGO_PKG_VERSION"),"manifest":value}))
 }).await.map_err(|_|"更新检查未完成")?
}
#[tauri::command]
pub fn open_release_url(url:String)->Result<(),String>{if !valid_release_url(&url){return Err("只允许打开本项目GitHub发行页面或附件".into())}crate::github::open_fixed_url(&url)}
