use serde::{Deserialize,Serialize};
use serde_json::{Value,json};
use std::collections::{BTreeMap,BTreeSet};
use sha2::{Digest,Sha256};
use base64::{Engine,engine::general_purpose::STANDARD as B64};
use aes_gcm::{Aes256Gcm,KeyInit,Nonce,aead::{Aead,Payload}};

pub type Result<T>=std::result::Result<T,String>;
#[derive(Clone,Debug,Serialize,Deserialize,PartialEq)]
#[serde(rename_all="camelCase",deny_unknown_fields)]
pub struct Snapshot{pub format:u32,#[serde(default="legacy_generation")]pub generation:String,pub workspace_id:String,pub orders:BTreeMap<String,Value>,pub money:BTreeMap<String,Value>,pub preferences:Value,pub history:BTreeMap<String,Value>}
pub fn legacy_generation()->String{"legacy".into()}
impl Snapshot{
 pub fn empty(id:&str)->Self{Self{format:2,generation:legacy_generation(),workspace_id:id.into(),orders:BTreeMap::new(),money:BTreeMap::new(),preferences:json!({}),history:BTreeMap::new()}}
 pub fn normalized(&self)->Self{let mut v=self.clone();v.format=2;for row in v.orders.values_mut(){if let Some(o)=row.as_object_mut(){o.remove("revision");o.entry("tasks").or_insert(json!([]));o.entry("historicalDates").or_insert(json!(false));}}v}
 pub fn digest(&self)->Result<String>{hash(&serde_json::to_value(self.normalized()).map_err(|_|"账本序列化失败")?)}
}
// Canonical profile: ASCII object keys, integers/strings/null/bool; no floating-point values.
pub fn canonical(v:&Value)->Result<String>{Ok(match v{
 Value::Null=>"null".into(),Value::Bool(b)=>b.to_string(),Value::String(s)=>serde_json::to_string(s).map_err(|_|"字符串编码失败")?,
 Value::Number(n)=>{if !n.is_i64()&&!n.is_u64(){return Err("同步合同不接受浮点数".into())}n.to_string()},
 Value::Array(a)=>format!("[{}]",a.iter().map(canonical).collect::<Result<Vec<_>>>()?.join(",")),
 Value::Object(o)=>{let sorted:BTreeMap<_,_>=o.iter().collect();let mut parts=vec![];for(k,v)in sorted{if !k.is_ascii(){return Err("同步字段名必须为ASCII".into())}parts.push(format!("{}:{}",serde_json::to_string(k).unwrap(),canonical(v)?))}format!("{{{}}}",parts.join(","))}
})}
pub fn hash(v:&Value)->Result<String>{Ok(Sha256::digest(canonical(v)?.as_bytes()).iter().map(|b|format!("{b:02x}")).collect())}
pub fn new_key()->Result<String>{let mut key=[0u8;32];getrandom::fill(&mut key).map_err(|_|"系统随机源不可用，未建立同步密钥")?;Ok(B64.encode(key))}
fn key_bytes(key:&str)->Result<Vec<u8>>{let bytes=B64.decode(key).map_err(|_|"恢复密钥格式无效")?;if bytes.len()!=32{return Err("恢复密钥长度无效".into())}Ok(bytes)}
pub fn key_id(key:&str)->Result<String>{Ok(Sha256::digest(key_bytes(key)?).iter().map(|b|format!("{b:02x}")).collect())}
fn aad(workspace:&str,id:&str)->String{format!("PersonalOrderDesk|snapshot|1|{workspace}|{id}")}
pub fn encrypt(snapshot:&Snapshot,key:&str)->Result<Value>{
 let plaintext=canonical(&serde_json::to_value(snapshot).map_err(|_|"账本序列化失败")?)?;
 if plaintext.len()>10*1024*1024{return Err("账本同步包超过10MiB，本地记录保留，请使用分块版本同步".into())}
 let id=key_id(key)?;let mut nonce=[0u8;12];getrandom::fill(&mut nonce).map_err(|_|"随机源不可用")?;
 let cipher=Aes256Gcm::new_from_slice(&key_bytes(key)?).map_err(|_|"同步密钥无效")?;
 let ciphertext=cipher.encrypt(Nonce::from_slice(&nonce),Payload{msg:plaintext.as_bytes(),aad:aad(&snapshot.workspace_id,&id).as_bytes()}).map_err(|_|"账本加密失败")?;
 Ok(json!({"format":"personal-order-desk.encrypted","version":1,"workspaceId":snapshot.workspace_id,"keyId":id,"nonce":B64.encode(nonce),"ciphertext":B64.encode(ciphertext)}))
}
pub fn decrypt(envelope:&Value,key:&str,workspace:&str)->Result<Snapshot>{
 if envelope["format"]!="personal-order-desk.encrypted"||envelope["version"]!=1||envelope["workspaceId"]!=workspace{return Err("远端同步包的格式或账本身份不匹配，未应用数据".into())}
 let id=key_id(key)?;if envelope["keyId"]!=id{return Err("远端使用其他同步密钥，请导入该账本的恢复密钥".into())}
 let decode=|name:&str|->Result<Vec<u8>>{B64.decode(envelope[name].as_str().ok_or("远端加密包缺少字段")?).map_err(|_|"远端加密包编码损坏".into())};
 let nonce=decode("nonce")?;if nonce.len()!=12{return Err("远端随机数长度无效".into())}let bytes=decode("ciphertext")?;
 if bytes.len()>10*1024*1024+16{return Err("远端加密包超过支持大小".into())}
 let cipher=Aes256Gcm::new_from_slice(&key_bytes(key)?).map_err(|_|"同步密钥无效")?;
 let plaintext=cipher.decrypt(Nonce::from_slice(&nonce),Payload{msg:&bytes,aad:aad(workspace,&id).as_bytes()}).map_err(|_|"远端加密包校验失败，密文或密钥不正确，未应用数据")?;
 let snapshot:Snapshot=serde_json::from_slice(&plaintext).map_err(|_|"远端账本合同不兼容")?;
 if ![1,2].contains(&snapshot.format)||snapshot.workspace_id!=workspace{return Err("远端账本身份不匹配".into())}Ok(snapshot)
}
#[derive(Clone,Debug,Serialize,Deserialize)]
#[serde(rename_all="camelCase")]
pub struct Conflict{pub id:String,pub collection:String,pub record_id:String,pub field:String,pub reason:String,pub baseline:Value,pub local:Value,pub remote:Value}
#[derive(Clone,Serialize,Deserialize)]pub struct Merge{pub snapshot:Snapshot,pub conflicts:Vec<Conflict>}
fn choose(base:&Value,local:&Value,remote:&Value,collection:&str,id:&str,field:&str,reason:&str,choices:&BTreeMap<String,String>,conflicts:&mut Vec<Conflict>)->Value{
 if local==remote{return local.clone()}if local==base{return remote.clone()}if remote==base{return local.clone()}
 let key=format!("{collection}/{id}/{field}");
 match choices.get(&key).map(String::as_str){Some("local")=>local.clone(),Some("remote")=>remote.clone(),_=>{conflicts.push(Conflict{id:key,collection:collection.into(),record_id:id.into(),field:field.into(),reason:reason.into(),baseline:base.clone(),local:local.clone(),remote:remote.clone()});base.clone()}}
}
fn extreme_time<'a>(values:impl Iterator<Item=&'a str>,latest:bool)->Result<Value>{
 let mut selected:Option<(chrono::DateTime<chrono::FixedOffset>,&str)>=None;
 for raw in values{let parsed=chrono::DateTime::parse_from_rfc3339(raw).map_err(|_|"任务实际时间无效")?;
  if selected.as_ref().is_none_or(|(old,_)|if latest{parsed>*old}else{parsed<*old}){selected=Some((parsed,raw));}
 }Ok(selected.map(|(_,raw)|json!(raw)).unwrap_or(Value::Null))
}
fn merge_tasks(base:&Value,local:&Value,remote:&Value,order_id:&str,choices:&BTreeMap<String,String>,conflicts:&mut Vec<Conflict>)->Result<Value>{
 let index=|v:&Value|->Result<BTreeMap<String,Value>>{let mut out=BTreeMap::new();if let Some(rows)=v.as_array(){for row in rows{let id=row["id"].as_str().ok_or("任务缺少标识")?;if out.insert(id.to_owned(),row.clone()).is_some(){return Err("任务标识重复".into())}}}else if !v.is_null(){return Err("任务列表格式无效".into())}Ok(out)};
 let mut b=index(base)?;let mut l=index(local)?;let mut r=index(remote)?;
 let groups:BTreeSet<String>=b.values().chain(l.values()).chain(r.values()).filter_map(|t|t["parentId"].as_str().map(str::to_owned)).collect();
 // These fields are derived for parents, so independent child edits must not conflict here.
 for map in [&mut b,&mut l,&mut r]{for id in &groups{if let Some(row)=map.get_mut(id){row["status"]=json!("todo");row["startedAt"]=Value::Null;row["completedAt"]=Value::Null;}}}
 let ids=|v:&Value|->Vec<String>{v.as_array().into_iter().flatten().filter_map(|row|row["id"].as_str().map(str::to_owned)).collect()};
 let bo=ids(base);let lo=ids(local);let ro=ids(remote);
 // Missing records in an older backup are not an intentional reorder or hard delete.
 let reordered=|side:&Vec<String>|{let actual:Vec<_>=side.iter().filter(|id|b.contains_key(*id)).collect();let expected:Vec<_>=bo.iter().filter(|id|side.contains(id)).collect();actual!=expected};
 let lr=reordered(&lo);let rr=reordered(&ro);
 let preferred=if lr&&rr&&lo!=ro{
  let chosen=choose(&json!(bo),&json!(lo),&json!(ro),"orders",order_id,"taskOrder","双方调整了任务顺序，请选择保留哪一种顺序",choices,conflicts);
  chosen.as_array().into_iter().flatten().filter_map(|v|v.as_str().map(str::to_owned)).collect::<Vec<_>>()
 }else if rr||lo==bo&&ro!=bo{ro.clone()}else{lo.clone()};
 let mut sequence=vec![];for id in preferred.iter().chain(lo.iter()).chain(ro.iter()).chain(bo.iter()){if !sequence.contains(id){sequence.push(id.clone())}}

 let mut result=vec![];
 for id in sequence{let bv=b.get(&id).unwrap_or(&Value::Null);let lv=l.get(&id).unwrap_or(bv);let rv=r.get(&id).unwrap_or(bv);let field=format!("task:{id}");
  let cancel_edit=(lv["status"]=="cancelled"&&bv["status"]!="cancelled"&&rv!=bv)||(rv["status"]=="cancelled"&&bv["status"]!="cancelled"&&lv!=bv);
  let row=if bv.is_null()||cancel_edit{choose(bv,lv,rv,"orders",order_id,&field,"任务取消与另一端编辑冲突，或同一标识被重复新建",choices,conflicts)}else{
   let mut fields=serde_json::Map::new();
   let names:BTreeSet<_>=bv.as_object().ok_or("任务格式无效")?.keys().chain(lv.as_object().ok_or("任务格式无效")?.keys()).chain(rv.as_object().ok_or("任务格式无效")?.keys()).cloned().collect();
   for name in names{if ["status","startedAt","completedAt"].contains(&name.as_str()){continue}fields.insert(name.clone(),choose(&bv[&name],&lv[&name],&rv[&name],"orders",order_id,&format!("task:{id}:{name}"),"双方修改了同一任务字段",choices,conflicts));}
   let part=|v:&Value|json!({"status":v["status"],"startedAt":v["startedAt"],"completedAt":v["completedAt"]});
   let chosen=choose(&part(bv),&part(lv),&part(rv),"orders",order_id,&format!("task:{id}:completion"),"双方修改了任务状态或完成记录",choices,conflicts);
   for name in ["status","startedAt","completedAt"]{fields.insert(name.into(),chosen[name].clone());}Value::Object(fields)
  };if !row.is_null(){result.push(row)}
 }
 // Normalize parent summaries after independently merging children.
 let children=result.clone();for task in &mut result{if task["parentId"].is_null(){let items:Vec<_>=children.iter().filter(|c|c["parentId"]==task["id"]).collect();if items.is_empty(){continue}let active:Vec<_>=items.iter().filter(|c|c["status"]!="cancelled").collect();let status=if active.is_empty(){"cancelled"}else if active.iter().all(|c|c["status"]=="done"){"done"}else if active.iter().all(|c|c["status"]=="waiting"){"waiting"}else if active.iter().any(|c|c["status"]!="todo"){"doing"}else{"todo"};task["status"]=json!(status);task["startedAt"]=extreme_time(items.iter().filter_map(|c|c["startedAt"].as_str()),false)?;task["completedAt"]=if status=="done"{extreme_time(active.iter().filter_map(|c|c["completedAt"].as_str()),true)?}else{Value::Null};}}
 Ok(json!(result))
}
pub fn merge(base:&Snapshot,local:&Snapshot,remote:&Snapshot,choices:&BTreeMap<String,String>)->Result<Merge>{
 if [base,local,remote].iter().any(|s|![1,2].contains(&s.format)||s.workspace_id!=base.workspace_id){return Err("合并账本身份不一致".into())}
 if local.generation!=remote.generation||base.generation!=remote.generation{return Err("账本已在另一端整本重置，请先接受新版本，不能合并旧副本".into())}
 let b=base.normalized();let l=local.normalized();let r=remote.normalized();let mut out=Snapshot::empty(&base.workspace_id);out.generation=remote.generation.clone();let mut conflicts=vec![];
 let keys:BTreeSet<_>=b.orders.keys().chain(l.orders.keys()).chain(r.orders.keys()).cloned().collect();
 for id in keys{
  let empty=Value::Null;let bv=b.orders.get(&id).unwrap_or(&empty);let lv=l.orders.get(&id).unwrap_or(bv);let rv=r.orders.get(&id).unwrap_or(bv);
  let changed_money=|side:&Snapshot|side.money.iter().any(|(key,v)|v["orderId"]==id&&b.money.get(key)!=Some(v));
  let delete_edit=(!lv["deletedAt"].is_null()&&bv["deletedAt"].is_null()&&(rv!=bv||changed_money(&r)))||(!rv["deletedAt"].is_null()&&bv["deletedAt"].is_null()&&(lv!=bv||changed_money(&l)));
  let mut row=if delete_edit&&lv!=rv{
   let key=format!("orders/{id}/record");match choices.get(&key).map(String::as_str){Some("local")=>lv.clone(),Some("remote")=>rv.clone(),_=>{conflicts.push(Conflict{id:key,collection:"orders".into(),record_id:id.clone(),field:"record".into(),reason:"删除项目与另一端修改或新增收支冲突".into(),baseline:bv.clone(),local:lv.clone(),remote:rv.clone()});bv.clone()}}
  }else if bv.is_null(){choose(bv,lv,rv,"orders",&id,"record","同一项目标识在两端新建了不同内容",choices,&mut conflicts)}else{
   let mut fields=serde_json::Map::new();
   let keys:BTreeSet<_>=bv.as_object().ok_or("项目格式无效")?.keys().chain(lv.as_object().ok_or("项目格式无效")?.keys()).chain(rv.as_object().ok_or("项目格式无效")?.keys()).cloned().collect();
   for field in keys{if ["status","completedDate","acceptedDate","dueDate","kind","tasks"].contains(&field.as_str()){continue}fields.insert(field.clone(),choose(&bv[&field],&lv[&field],&rv[&field],"orders",&id,&field,"双方修改了同一字段",choices,&mut conflicts));}
   fields.insert("tasks".into(),merge_tasks(&bv["tasks"],&lv["tasks"],&rv["tasks"],&id,choices,&mut conflicts)?);
   for(group,names)in[("completion",vec!["status","completedDate"]),("deadline",vec!["kind","acceptedDate","dueDate"]) ]{
    let part=|v:&Value|->Value{Value::Object(names.iter().map(|k|(k.to_string(),v[*k].clone())).collect())};
    let selected=choose(&part(bv),&part(lv),&part(rv),"orders",&id,group,"相关字段需要一起确认",choices,&mut conflicts);
    for name in names{fields.insert(name.into(),selected[name].clone());}
   }
   Value::Object(fields)
  };
  if !row.is_null(){row["revision"]=json!(0);out.orders.insert(id,row);}
 }
 for(collection,bm,lm,rm)in[("money",&b.money,&l.money,&r.money),("history",&b.history,&l.history,&r.history)]{
  let keys:BTreeSet<_>=bm.keys().chain(lm.keys()).chain(rm.keys()).cloned().collect();
  for id in keys{let empty=Value::Null;let bv=bm.get(&id).unwrap_or(&empty);let lv=lm.get(&id).unwrap_or(bv);let rv=rm.get(&id).unwrap_or(bv);
   if collection=="history"&&!lv.is_null()&&!rv.is_null()&&lv!=rv{return Err("同一历史事件出现不同内容，已拒绝覆盖，请保留双方备份排查".into())}
   let value=choose(bv,lv,rv,collection,&id,"record",if collection=="money"{"同一笔流水内容发生冲突，必须整笔选择"}else{"同一历史事件内容不一致"},choices,&mut conflicts);
   if !value.is_null(){if collection=="money"{out.money.insert(id,value);}else{out.history.insert(id,value);}}
  }
 }
 let names:BTreeSet<_>=b.preferences.as_object().into_iter().flat_map(|o|o.keys()).chain(l.preferences.as_object().into_iter().flat_map(|o|o.keys())).chain(r.preferences.as_object().into_iter().flat_map(|o|o.keys())).cloned().collect();
 let mut preferences=serde_json::Map::new();for name in names{preferences.insert(name.clone(),choose(&b.preferences[&name],&l.preferences[&name],&r.preferences[&name],"preferences","settings",&name,"双方修改了同一设置",choices,&mut conflicts));}out.preferences=Value::Object(preferences);
 Ok(Merge{snapshot:out,conflicts})
}
