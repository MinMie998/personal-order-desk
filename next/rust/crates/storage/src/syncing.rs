use crate::ledger::{Ledger,Order,MoneyEntry,Preferences};
use desk_sync::{protocol::{Snapshot,Result},vault};
use serde_json::{json,Value};
use sqlx::Row;
use std::collections::{BTreeMap,BTreeSet};
fn failure(_:sqlx::Error)->String{"同步本地事务未完成，原始数据保留，请检查磁盘空间".into()}
impl Ledger{
 pub async fn replace_ledger(&self,snapshot:&Snapshot,state:Option<&Value>,expected:&str,run:Option<&Value>)->Result<String>{
  Self::validate_snapshot(snapshot)?;
  if snapshot.workspace_id!=self.workspace_id()?{return Err("替换账本身份不一致".into())}
  if self.sync_snapshot().await?.digest()?!=expected{return Err("账本已有新修改，取消覆盖，请重新准备迁入".into())}
  let backup=self.backup().await?;
  let revisions=sqlx::query("SELECT id,revision FROM orders").fetch_all(&self.pool).await.map_err(failure)?.into_iter().map(|r|(r.get::<String,_>(0),r.get::<i64,_>(1))).collect::<BTreeMap<_,_>>();
  let mut tx=self.pool.begin().await.map_err(failure)?;
  sqlx::query("INSERT INTO ledger_meta VALUES(1,?) ON CONFLICT(id) DO UPDATE SET generation=excluded.generation").bind(&snapshot.generation).execute(&mut *tx).await.map_err(failure)?;
  sqlx::query("DELETE FROM money").execute(&mut *tx).await.map_err(failure)?;
  sqlx::query("DELETE FROM orders").execute(&mut *tx).await.map_err(failure)?;
  sqlx::query("DELETE FROM history").execute(&mut *tx).await.map_err(failure)?;
  for(id,value)in &snapshot.orders{let mut order:Order=serde_json::from_value(value.clone()).map_err(|_|"迁入订单格式无效")?;order.validate()?;order.revision=order.revision.max(*revisions.get(id).unwrap_or(&0)).checked_add(1).ok_or("订单版本超出范围")?;
   sqlx::query("INSERT INTO orders VALUES(?,?,?)").bind(id).bind(order.revision).bind(serde_json::to_string(&order).map_err(|_|"迁入订单编码失败")?).execute(&mut *tx).await.map_err(failure)?;
  }
  for(id,value)in &snapshot.money{let entry:MoneyEntry=serde_json::from_value(value.clone()).map_err(|_|"迁入流水无效")?;
   sqlx::query("INSERT INTO money VALUES(?,?,?,?)").bind(id).bind(&entry.order_id).bind(desk_domain::parse_cents(&entry.amount_cents).map_err(|_|"迁入金额无效")?).bind(value.to_string()).execute(&mut *tx).await.map_err(failure)?;
  }
  for(id,event)in &snapshot.history{sqlx::query("INSERT INTO history(event_id,entity_id,action,at,payload) VALUES(?,?,?,?,?)").bind(id).bind(event["entityId"].as_str()).bind(event["action"].as_str()).bind(event["at"].as_str()).bind(event["payload"].as_str()).execute(&mut *tx).await.map_err(failure)?;}
  sqlx::query("INSERT INTO preferences VALUES(1,?) ON CONFLICT(id) DO UPDATE SET body=excluded.body").bind(snapshot.preferences.to_string()).execute(&mut *tx).await.map_err(failure)?;
  if let Some(state)=state{let sealed=vault::seal(&serde_json::to_vec(state).map_err(|_|"同步状态编码失败")?)?;sqlx::query("INSERT INTO sync_state VALUES(1,?) ON CONFLICT(id) DO UPDATE SET sealed=excluded.sealed").bind(sealed).execute(&mut *tx).await.map_err(failure)?;}
  if let Some(details)=run{sqlx::query("INSERT INTO sync_runs VALUES(?,?,?,?)").bind(uuid::Uuid::new_v4().to_string()).bind(chrono::Local::now().to_rfc3339()).bind("confirmed").bind(details.to_string()).execute(&mut *tx).await.map_err(failure)?;}
  tx.commit().await.map_err(failure)?;Ok(backup)
 }
 pub async fn adopt_sync_identity(&self,id:&str)->Result<()>{
  uuid::Uuid::parse_str(id).map_err(|_|"恢复密钥的账本标识无效")?;if id==self.workspace_id()?{return Ok(())}
  let count:i64=sqlx::query_scalar("SELECT count(*) FROM orders").fetch_one(&self.pool).await.map_err(failure)?;
  if count!=0{return Err("当前账本已有项目，不能直接换成另一账本身份；请创建空目录后导入恢复密钥".into())}
  let path=self.root.join("workspace.json");let mut metadata:Value=serde_json::from_slice(&std::fs::read(&path).map_err(|_|"无法读取账本登记")?).map_err(|_|"账本登记损坏")?;
  metadata["workspaceId"]=json!(id);crate::location::replace_json(&path,&metadata)
 }
 pub async fn initialize_sync(&self)->Result<()>{
  sqlx::query("CREATE TABLE IF NOT EXISTS ledger_meta(id INTEGER PRIMARY KEY CHECK(id=1),generation TEXT NOT NULL)").execute(&self.pool).await.map_err(failure)?;
  sqlx::query("CREATE TABLE IF NOT EXISTS sync_state(id INTEGER PRIMARY KEY CHECK(id=1),sealed BLOB NOT NULL)").execute(&self.pool).await.map_err(failure)?;
  sqlx::query("CREATE TABLE IF NOT EXISTS sync_runs(id TEXT PRIMARY KEY,at TEXT NOT NULL,result TEXT NOT NULL,details TEXT NOT NULL)").execute(&self.pool).await.map_err(failure)?;
  let exists:i64=sqlx::query_scalar("SELECT count(*) FROM pragma_table_info('history') WHERE name='event_id'").fetch_one(&self.pool).await.map_err(failure)?;
  if exists==0{sqlx::query("ALTER TABLE history ADD COLUMN event_id TEXT").execute(&self.pool).await.map_err(failure)?;}
  sqlx::query("UPDATE history SET event_id=lower(hex(randomblob(16))) WHERE event_id IS NULL").execute(&self.pool).await.map_err(failure)?;
  sqlx::query("CREATE UNIQUE INDEX IF NOT EXISTS history_event_id ON history(event_id)").execute(&self.pool).await.map_err(failure)?;
  sqlx::query("CREATE TRIGGER IF NOT EXISTS history_identity AFTER INSERT ON history WHEN NEW.event_id IS NULL BEGIN UPDATE history SET event_id=lower(hex(randomblob(16))) WHERE id=NEW.id;END").execute(&self.pool).await.map_err(failure)?;
  Ok(())
 }
 pub async fn sync_snapshot(&self)->Result<Snapshot>{
  let mut tx=self.pool.begin().await.map_err(failure)?;let mut snapshot=Snapshot::empty(&self.workspace_id()?);snapshot.generation=sqlx::query_scalar::<_,String>("SELECT generation FROM ledger_meta WHERE id=1").fetch_optional(&mut *tx).await.map_err(failure)?.unwrap_or_else(desk_sync::protocol::legacy_generation);
  for table in ["orders","money"]{
   let query=format!("SELECT id,body FROM {table}");
   let rows=sqlx::query(&query).fetch_all(&mut *tx).await.map_err(failure)?;
   for row in rows{let value=serde_json::from_str(&row.get::<String,_>(1)).map_err(|_|"本机账本记录格式损坏")?;if table=="orders"{snapshot.orders.insert(row.get(0),value);}else{snapshot.money.insert(row.get(0),value);}}
  }
  let pref=sqlx::query_scalar::<_,String>("SELECT body FROM preferences WHERE id=1").fetch_optional(&mut *tx).await.map_err(failure)?;
  snapshot.preferences=match pref{Some(body)=>serde_json::from_str(&body).map_err(|_|"设置记录格式损坏")?,None=>serde_json::to_value(Preferences::default()).map_err(|_|"设置序列化失败")?};
  for row in sqlx::query("SELECT event_id,entity_id,action,at,payload FROM history ORDER BY event_id").fetch_all(&mut *tx).await.map_err(failure)?{
   snapshot.history.insert(row.get(0),json!({"entityId":row.get::<String,_>(1),"action":row.get::<String,_>(2),"at":row.get::<String,_>(3),"payload":row.get::<String,_>(4)}));
  }
  tx.commit().await.map_err(failure)?;Ok(snapshot)
 }
 pub async fn read_sync_state(&self)->Result<Option<Value>>{
  let row=sqlx::query_scalar::<_,Vec<u8>>("SELECT sealed FROM sync_state WHERE id=1").fetch_optional(&self.pool).await.map_err(failure)?;
  row.map(|bytes|serde_json::from_slice(&vault::unseal(&bytes)?).map_err(|_|"同步登记损坏，请使用恢复密钥重新连接".into())).transpose()
 }
 pub async fn save_sync_state(&self,state:&Value)->Result<()>{
  let bytes=serde_json::to_vec(state).map_err(|_|"同步状态编码失败")?;let sealed=vault::seal(&bytes)?;
  sqlx::query("INSERT INTO sync_state VALUES(1,?) ON CONFLICT(id) DO UPDATE SET sealed=excluded.sealed").bind(sealed).execute(&self.pool).await.map_err(failure)?;Ok(())
 }
 pub fn validate_snapshot(snapshot:&Snapshot)->Result<()>{
  if ![1,2].contains(&snapshot.format){return Err("同步账本格式不兼容".into())}if snapshot.generation!="legacy"{uuid::Uuid::parse_str(&snapshot.generation).map_err(|_|"账本重置标识无效")?;}uuid::Uuid::parse_str(&snapshot.workspace_id).map_err(|_|"账本标识无效")?;
  let mut parents=BTreeMap::new();
  for(id,value)in &snapshot.orders{
   let mut order:Order=serde_json::from_value(value.clone()).map_err(|_|"远端订单字段不完整")?;
   if order.id!=*id{return Err("远端订单标识不一致".into())}order.validate()?;parents.insert(id.clone(),order.parent_id);
  }
  for(id,parent)in &parents{if let Some(parent)=parent{if !parents.contains_key(parent){return Err("同步账本缺少追加单的原单".into())}let mut seen=BTreeSet::new();seen.insert(id.clone());let mut current=Some(parent.clone());while let Some(key)=current{if !seen.insert(key.clone()){return Err("追加单关联形成循环，请先整理关联关系".into())}current=parents.get(&key).cloned().flatten();}}}
  let mut balances:BTreeMap<String,(i128,i128)>=BTreeMap::new();
  for(id,value)in &snapshot.money{
   let entry:MoneyEntry=serde_json::from_value(value.clone()).map_err(|_|"远端收支字段不完整")?;
   if entry.id!=*id||!snapshot.orders.contains_key(&entry.order_id){return Err("同步流水的项目关联不完整".into())}uuid::Uuid::parse_str(id).map_err(|_|"流水标识无效")?;
   let cents=desk_domain::parse_cents(&entry.amount_cents).map_err(|_|"流水金额格式无效")?;if cents<=0{return Err("流水金额应大于零".into())}
   if !["receipt","refund","cost","cost-refund"].contains(&entry.kind.as_str()){return Err("流水类型无效".into())}if let Some(date)=&entry.date{chrono::NaiveDate::parse_from_str(date,"%Y-%m-%d").map_err(|_|"流水日期无效")?;}
   if entry.voided_at.is_some(){continue}
   let b=balances.entry(entry.order_id).or_default();match entry.kind.as_str(){"receipt"=>b.0+=cents as i128,"refund"=>b.0-=cents as i128,"cost"=>b.1+=cents as i128,"cost-refund"=>b.1-=cents as i128,_=>{}}
  }
  for(id,(received,cost))in &balances{if *received<0||*cost<0{return Err(format!("项目 {} 合并后存在退款超过收款或退回超过支出的情况，请核对并修正相关流水后重试",snapshot.orders[id]["number"].as_str().unwrap_or(id)))}}
  let _:Preferences=serde_json::from_value(snapshot.preferences.clone()).map_err(|_|"偏好设置格式不兼容")?;
  for(id,event)in &snapshot.history{if id.is_empty()||id.len()>100||!["entityId","action","at","payload"].iter().all(|key|event[*key].is_string()){return Err("历史事件格式不兼容".into())}}
  Ok(())
 }
 pub async fn apply_sync(&self,snapshot:&Snapshot,state:&Value,details:&Value)->Result<()>{
  Self::validate_snapshot(snapshot)?;if snapshot.workspace_id!=self.workspace_id()?{return Err("同步目标工作空间已变化".into())}
  let sealed=vault::seal(&serde_json::to_vec(state).map_err(|_|"同步状态编码失败")?)?;
  let mut tx=self.pool.begin().await.map_err(failure)?;
  sqlx::query("INSERT INTO ledger_meta VALUES(1,?) ON CONFLICT(id) DO UPDATE SET generation=excluded.generation").bind(&snapshot.generation).execute(&mut *tx).await.map_err(failure)?;
  for(id,value)in &snapshot.orders{
   let mut order:Order=serde_json::from_value(value.clone()).map_err(|_|"订单格式无效")?;
   let old=sqlx::query("SELECT revision,body FROM orders WHERE id=?").bind(id).fetch_optional(&mut *tx).await.map_err(failure)?;
   order.revision=if let Some(row)=old{let mut old_value:Value=serde_json::from_str(&row.get::<String,_>(1)).map_err(|_|"本地订单损坏")?;old_value.as_object_mut().ok_or("本地订单格式损坏")?.remove("revision");let mut new_value=value.clone();new_value.as_object_mut().ok_or("订单格式损坏")?.remove("revision");let rev=row.get::<i64,_>(0);if old_value==new_value{rev}else{rev.checked_add(1).ok_or("订单版本超出范围")?}}else{1};
   sqlx::query("INSERT INTO orders VALUES(?,?,?) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,body=excluded.body").bind(id).bind(order.revision).bind(serde_json::to_string(&order).map_err(|_|"订单编码失败")?).execute(&mut *tx).await.map_err(failure)?;
  }
  for(id,value)in &snapshot.money{let entry:MoneyEntry=serde_json::from_value(value.clone()).map_err(|_|"流水格式损坏")?;sqlx::query("INSERT INTO money VALUES(?,?,?,?) ON CONFLICT(id) DO UPDATE SET order_id=excluded.order_id,amount_cents=excluded.amount_cents,body=excluded.body").bind(id).bind(entry.order_id).bind(desk_domain::parse_cents(&entry.amount_cents).map_err(|_|"流水金额无效")?).bind(serde_json::to_string(value).map_err(|_|"流水编码失败")?).execute(&mut *tx).await.map_err(failure)?;}
  for(id,event)in &snapshot.history{sqlx::query("INSERT INTO history(event_id,entity_id,action,at,payload) VALUES(?,?,?,?,?) ON CONFLICT(event_id) DO NOTHING").bind(id).bind(event["entityId"].as_str()).bind(event["action"].as_str()).bind(event["at"].as_str()).bind(event["payload"].as_str()).execute(&mut *tx).await.map_err(failure)?;}
  sqlx::query("INSERT INTO preferences VALUES(1,?) ON CONFLICT(id) DO UPDATE SET body=excluded.body").bind(snapshot.preferences.to_string()).execute(&mut *tx).await.map_err(failure)?;
  sqlx::query("INSERT INTO sync_state VALUES(1,?) ON CONFLICT(id) DO UPDATE SET sealed=excluded.sealed").bind(sealed).execute(&mut *tx).await.map_err(failure)?;
  sqlx::query("INSERT INTO sync_runs VALUES(?,?,?,?)").bind(uuid::Uuid::new_v4().to_string()).bind(chrono::Local::now().to_rfc3339()).bind("confirmed").bind(details.to_string()).execute(&mut *tx).await.map_err(failure)?;
  tx.commit().await.map_err(failure)?;Ok(())
 }
 pub async fn sync_runs(&self)->Result<Vec<Value>>{let rows=sqlx::query("SELECT at,result,details FROM sync_runs ORDER BY rowid DESC LIMIT 20").fetch_all(&self.pool).await.map_err(failure)?;Ok(rows.into_iter().map(|r|json!({"at":r.get::<String,_>(0),"result":r.get::<String,_>(1),"details":r.get::<String,_>(2)})).collect())}
}

#[cfg(test)]mod tests{
 use super::*;
 fn synthetic_order(id:String)->Order{serde_json::from_value(json!({"id":id,"number":"SYNTHETIC","title":"合成账本","kind":"simulation","status":"done","acceptedDate":"2026-11-16","dueDate":null,"completedDate":null,"quoteCents":"10000","progress":"","nextStep":"","projectPath":"","notes":"","trackingNumber":"","carrier":"","shippedDate":null,"deliveredDate":null,"parentId":null,"revision":0,"deletedAt":null,"tasks":[],"historicalDates":true})).unwrap()}
 #[tokio::test]async fn whole_replacement_is_exact_revision_safe_and_old_backup_keeps_old_generation(){
  let root=std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../../tmp").join(format!("sync-replace-{}",uuid::Uuid::new_v4()));let db=Ledger::create(&root).await.unwrap();
  let a=db.save_order(synthetic_order(uuid::Uuid::new_v4().to_string())).await.unwrap();let b=db.save_order(synthetic_order(uuid::Uuid::new_v4().to_string())).await.unwrap();
  assert!(a.completed_date.is_none());assert!(a.due_date.is_none());
  db.add_money(MoneyEntry{id:uuid::Uuid::new_v4().to_string(),order_id:b.id.clone(),kind:"receipt".into(),amount_cents:"5000".into(),date:None,method:"合成方式".into(),note:"".into(),voided_at:None,void_reason:None}).await.unwrap();
  let original=db.sync_snapshot().await.unwrap();let original_backup=db.backup().await.unwrap();let mut replacement=original.clone();replacement.orders.remove(&b.id);replacement.orders.get_mut(&a.id).unwrap()["title"]=json!("远端新标题");replacement.money.clear();replacement.history.clear();replacement.generation=uuid::Uuid::new_v4().to_string();
  db.replace_ledger(&replacement,Some(&json!({"marker":"new-generation"})),&original.digest().unwrap(),Some(&json!({"mode":"download"}))).await.unwrap();let current=db.sync_snapshot().await.unwrap();
  assert_eq!(current.orders.len(),1);assert!(current.money.is_empty());assert_eq!(current.generation,replacement.generation);assert_eq!(current.orders[&a.id]["revision"],2);assert_eq!(db.sync_runs().await.unwrap().len(),1);assert!(db.save_order(a.clone()).await.is_err());assert!(db.save_order(b).await.is_err());
  assert!(db.replace_ledger(&original,None,&original.digest().unwrap(),None).await.is_err());assert_eq!(db.sync_snapshot().await.unwrap().digest().unwrap(),current.digest().unwrap());
  db.restore(std::path::Path::new(&original_backup).file_name().unwrap().to_str().unwrap()).await.unwrap();let restored=db.sync_snapshot().await.unwrap();assert_eq!(restored.generation,"legacy");assert_eq!(restored.orders.len(),2);assert_eq!(restored.money,original.money);assert_eq!(db.read_sync_state().await.unwrap().unwrap()["marker"],"new-generation");db.pool.close().await;
 }
 #[tokio::test]async fn protected_backup_restore_preserves_event_identity_and_cents(){
  let root=std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../../tmp").join(format!("sync-storage-{}",uuid::Uuid::new_v4()));
  let db=Ledger::create(&root).await.unwrap();let id=uuid::Uuid::new_v4().to_string();
  let order=Order{id:id.clone(),number:"SYNTHETIC-RESTORE".into(),title:"合成回溯订单".into(),kind:"simulation".into(),status:"doing".into(),accepted_date:"2026-11-15".into(),due_date:Some("2026-11-16".into()),completed_date:None,quote_cents:"9007199254740993".into(),progress:"最初进度".into(),next_step:"".into(),project_path:"".into(),notes:"".into(),tracking_number:"".into(),carrier:"".into(),shipped_date:None,delivered_date:None,parent_id:None,revision:0,deleted_at:None,historical_dates:false,tasks:vec![]};
  let mut saved=db.save_order(order).await.unwrap();
  db.add_money(MoneyEntry{id:uuid::Uuid::new_v4().to_string(),order_id:id.clone(),kind:"receipt".into(),amount_cents:"50000".into(),date:None,method:"合成方式".into(),note:"".into(),voided_at:None,void_reason:None}).await.unwrap();
  let before=db.sync_snapshot().await.unwrap();db.save_sync_state(&json!({"marker":"SYNTHETIC-SEALED-STATE"})).await.unwrap();let backup=db.backup().await.unwrap();saved.progress="后续修改".into();db.save_order(saved).await.unwrap();
  let name=std::path::Path::new(&backup).file_name().unwrap().to_str().unwrap();db.restore(name).await.unwrap();let after=db.sync_snapshot().await.unwrap();
  assert_eq!(after.orders[&id]["quoteCents"],"9007199254740993");assert_eq!(after.orders[&id]["progress"],"最初进度");assert_eq!(after.money,before.money);assert!(before.history.keys().all(|key|after.history.contains_key(key)));assert_eq!(after.history.len(),before.history.len()+1);assert_eq!(db.read_sync_state().await.unwrap().unwrap()["marker"],"SYNTHETIC-SEALED-STATE");
  db.pool.close().await;
 }
}
