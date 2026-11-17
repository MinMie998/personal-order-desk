use crate::{auth,protocol::{Result,Snapshot,decrypt,encrypt}};
use serde::{Deserialize,Serialize};
use serde_json::{Value,json};
use base64::{Engine,engine::general_purpose::STANDARD as B64};
pub const BRANCH:&str="desk-sync-v1";
#[derive(Clone,Serialize,Deserialize)]pub struct Head{pub sha:String,pub tree:String,pub branch_exists:bool,pub snapshot:Option<Snapshot>,pub snapshot_blob:Option<String>}
pub fn valid_repository(repo:&str)->bool{let parts=repo.split('/').collect::<Vec<_>>();parts.len()==2&&parts.iter().all(|p|!p.is_empty()&&*p!="."&&*p!=".."&&p.bytes().all(|b|b.is_ascii_alphanumeric()||[b'-',b'_',b'.'].contains(&b)))}
fn api(repo:&str,path:&str,method:&str,body:Option<Value>)->Result<(u16,Value)>{if !valid_repository(repo){return Err("仓库请填写所有者/仓库名".into())}let path=if path.is_empty(){format!("repos/{repo}")}else{format!("repos/{repo}/{path}")};auth::request(method,&path,body)}
fn need(repo:&str,path:&str,method:&str,body:Option<Value>)->Result<Value>{let(status,value)=api(repo,path,method,body)?;if !(200..300).contains(&status){return Err(match status{403=>"GitHub权限不足或请求频率受限，请检查指定仓库授权并稍后重试".into(),404=>"找不到仓库或分支，请检查GitHub App是否安装到该私有仓库".into(),_=>format!("GitHub操作未完成（HTTP {status}），本地数据保留")})}Ok(value)}
fn sha(value:&Value,key:&str)->Result<String>{let s=value[key].as_str().ok_or("GitHub缺少对象标识")?;if s.len()!=40||!s.bytes().all(|b|b.is_ascii_hexdigit()){return Err("GitHub对象标识无效".into())}Ok(s.into())}
pub fn read(repo:&str,workspace:&str,key:&str)->Result<Head>{
 let metadata=need(repo,"","GET",None)?;if metadata["private"]!=true{return Err("同步只允许使用私有仓库，未读取或上传账本".into())}
 let(status,reference)=api(repo,&format!("git/ref/heads/{BRANCH}"),"GET",None)?;
 let(branch_exists,head)=if status==404{
  let branch=metadata["default_branch"].as_str().ok_or("私有仓库尚未初始化，请先创建README")?;
  if !branch.bytes().all(|b|b.is_ascii_alphanumeric()||[b'-',b'_',b'/',b'.'].contains(&b)){return Err("仓库默认分支名暂不支持".into())}
  (false,need(repo,&format!("git/ref/heads/{branch}"),"GET",None)?)
 }else if status==200{(true,reference)}else{return Err(format!("无法读取同步分支（HTTP {status}）"))};
 let head=sha(&head["object"],"sha")?;
 let commit=need(repo,&format!("git/commits/{head}"),"GET",None)?;let tree=sha(&commit["tree"],"sha")?;
 let listing=need(repo,&format!("git/trees/{tree}?recursive=1"),"GET",None)?;
 if listing["truncated"]==true{return Err("仓库目录响应不完整，请使用独立的同步数据仓库".into())}
 let path=format!("workspaces/{workspace}/snapshot.enc.json");
 let entry=listing["tree"].as_array().ok_or("GitHub目录格式无效")?.iter().find(|entry|entry["path"]==path);
 let snapshot=if let Some(entry)=entry{
  if entry["type"]!="blob"||entry["size"].as_u64().unwrap_or(u64::MAX)>15*1024*1024{return Err("远端快照类型或大小不支持".into())}
  let blob_sha=sha(entry,"sha")?;let blob=need(repo,&format!("git/blobs/{blob_sha}"),"GET",None)?;
  if blob["encoding"]!="base64"{return Err("GitHub快照编码不支持".into())}
  let encoded=blob["content"].as_str().ok_or("远端快照缺少内容")?.replace(['\n','\r'],"");
  let bytes=B64.decode(encoded).map_err(|_|"远端快照编码损坏")?;
  let envelope:Value=serde_json::from_slice(&bytes).map_err(|_|"远端快照格式损坏")?;
  Some(decrypt(&envelope,key,workspace)?)
 }else{None};
 Ok(Head{sha:head,tree,branch_exists,snapshot,snapshot_blob:entry.map(|v|sha(v,"sha")).transpose()?})
}
// A conflicting update never forces the Git ref. The caller re-reads and prepares a new merge.
pub fn publish(repo:&str,head:&Head,snapshot:&Snapshot,key:&str)->Result<String>{
 let envelope=encrypt(snapshot,key)?;
 let content=serde_json::to_vec(&envelope).map_err(|_|"快照编码失败")?;
 let blob=need(repo,"git/blobs","POST",Some(json!({"encoding":"base64","content":B64.encode(content)})))?;let blob=sha(&blob,"sha")?;
 let mut entries=vec![json!({"path":format!("workspaces/{}/snapshot.enc.json",snapshot.workspace_id),"mode":"100644","type":"blob","sha":blob})];
 if let Some(previous)=&head.snapshot_blob{entries.push(json!({"path":format!("workspaces/{}/backups/{}.enc.json",snapshot.workspace_id,head.sha),"mode":"100644","type":"blob","sha":previous}));}
 let tree=need(repo,"git/trees","POST",Some(json!({"base_tree":head.tree,"tree":entries})))?;
 let commit=need(repo,"git/commits","POST",Some(json!({"message":"Update encrypted workbench snapshot","tree":sha(&tree,"sha")?,"parents":[head.sha]})))?;let new_head=sha(&commit,"sha")?;
 let(status,_)=if head.branch_exists{api(repo,&format!("git/refs/heads/{BRANCH}"),"PATCH",Some(json!({"sha":new_head,"force":false})))?}else{api(repo,"git/refs","POST",Some(json!({"ref":format!("refs/heads/{BRANCH}"),"sha":new_head})))?};
 if [409,422].contains(&status){return Err("远端刚被其他设备更新，本次未覆盖；请重新检查并合并".into())}
 if !(200..300).contains(&status){return Err(format!("远端确认尚未完成（HTTP {status}），请重新检查同步状态"))}Ok(new_head)
}
