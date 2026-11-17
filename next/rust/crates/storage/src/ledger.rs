use chrono::{Local,NaiveDate,Duration};
use serde::{Deserialize,Serialize};
use sqlx::{SqlitePool,Row,sqlite::{SqliteConnectOptions,SqlitePoolOptions}};
use std::path::{Path,PathBuf};
use desk_domain::parse_cents;

type Result<T>=std::result::Result<T,String>;
#[derive(Clone,Serialize,Deserialize)]
#[serde(rename_all="camelCase",default,deny_unknown_fields)]
pub struct Preferences{pub physical_reminder_days:u32,pub digital_reminder_days:u32,pub receipt_method:String,pub cost_method:String,pub money_animation:bool,pub entry_date_today:bool,pub default_kind:String}
impl Default for Preferences{fn default()->Self{Self{physical_reminder_days:4,digital_reminder_days:1,receipt_method:"微信".into(),cost_method:"微信".into(),money_animation:true,entry_date_today:false,default_kind:"simulation".into()}}}
fn db_error(_:sqlx::Error)->String{"本地账本操作失败，请检查数据目录和磁盘空间".into()}
fn stamp()->String{Local::now().to_rfc3339()}
fn today()->String{Local::now().format("%Y-%m-%d").to_string()}
fn date(value:&str)->Result<NaiveDate>{NaiveDate::parse_from_str(value,"%Y-%m-%d").map_err(|_|"日期格式无效，请使用年-月-日".into())}
fn optional_date(value:&Option<String>)->Result<()>{if let Some(v)=value{date(v)?;}Ok(())}
fn uuid(value:&str)->Result<()>{uuid::Uuid::parse_str(value).map(|_|()).map_err(|_|"记录标识无效".into())}
fn amount(value:&str)->Result<i64>{let cents=parse_cents(value).map_err(|_|"金额格式无效")?;if cents<0{return Err("金额不能小于零".into())}Ok(cents)}

#[derive(Clone,Serialize,Deserialize)]
#[serde(rename_all="camelCase",deny_unknown_fields)]
pub struct Order {
 pub id:String,pub number:String,pub title:String,pub kind:String,pub status:String,
 pub accepted_date:String,pub due_date:Option<String>,pub completed_date:Option<String>,
 pub quote_cents:String,pub progress:String,pub next_step:String,pub project_path:String,pub notes:String,
 pub tracking_number:String,pub carrier:String,pub shipped_date:Option<String>,pub delivered_date:Option<String>,
 pub parent_id:Option<String>,pub revision:i64,pub deleted_at:Option<String>,
 #[serde(default)]pub historical_dates:bool,
 #[serde(default)]pub tasks:Vec<crate::tasks::WorkTask>,
}
impl Order{
 pub(crate) fn validate(&mut self)->Result<()>{
  uuid(&self.id)?;self.number=self.number.trim().into();self.title=self.title.trim().into();
  if self.number.is_empty()||self.title.is_empty(){return Err("请填写订单编号和项目名称".into())}
  if self.number.len()>120||self.title.len()>600||self.notes.len()>24000{return Err("填写内容过长".into())}
  if !["physical","simulation","schematic","code","other"].contains(&self.kind.as_str()){return Err("请选择有效的交付类型".into())}
  if !["todo","doing","waiting","done","cancelled"].contains(&self.status.as_str()){return Err("请选择有效工作状态".into())}
  let accepted=date(&self.accepted_date)?;
  if self.due_date.is_none()&&self.kind!="physical"&&!self.historical_dates{self.due_date=Some((accepted+Duration::days(1)).to_string())}
  optional_date(&self.due_date)?;optional_date(&self.completed_date)?;optional_date(&self.shipped_date)?;optional_date(&self.delivered_date)?;
  if self.status=="done"&&self.completed_date.is_none()&&!self.historical_dates{self.completed_date=Some(today())}
  if self.status!="done"{self.completed_date=None;}
  if self.delivered_date.is_some()&&self.shipped_date.is_none(){return Err("请先填写寄出日期，再填写收货日期".into())}
  if self.delivered_date.as_ref().zip(self.shipped_date.as_ref()).is_some_and(|(a,b)|a<b){return Err("收货日期不能早于寄出日期".into())}
  amount(&self.quote_cents)?;
  crate::tasks::validate(&mut self.tasks)?;
  if self.parent_id.as_deref()==Some(&self.id){return Err("追加单不能关联自身".into())}if let Some(parent)=&self.parent_id{uuid(parent)?;}
  Ok(())
 }
}
#[derive(Clone,Serialize,Deserialize)]
#[serde(rename_all="camelCase",deny_unknown_fields)]
pub struct MoneyEntry{
 pub id:String,pub order_id:String,pub kind:String,pub amount_cents:String,pub date:Option<String>,
 pub method:String,pub note:String,pub voided_at:Option<String>,pub void_reason:Option<String>,
}
#[derive(Clone)]pub struct Ledger{pub(crate) pool:SqlitePool,pub(crate) root:PathBuf}
impl Ledger{
 pub async fn open(root:&Path)->Result<Option<Self>>{
  let manifest=root.join("workspace.json");
  if !manifest.exists(){if root.join("ledger.db").exists(){return Err("账本登记文件缺失，已有数据库未被覆盖，请从备份恢复登记".into())}return Ok(None)}
  let metadata:serde_json::Value=serde_json::from_slice(&std::fs::read(&manifest).map_err(|_|"无法读取账本登记")?).map_err(|_|"账本登记损坏，请从备份恢复")?;
  if metadata["format"]!=1||metadata["database"]!="ledger.db"||!root.join("ledger.db").is_file(){return Err("已登记的账本缺失或格式不兼容，程序没有新建空库".into())}
  Self::connect(root,false).await.map(Some)
 }
 pub async fn create(root:&Path)->Result<Self>{
  std::fs::create_dir_all(root).map_err(|_|"无法创建账本目录")?;
  if root.join("workspace.json").exists()||root.join("ledger.db").exists(){return Err("目录内已有账本或待恢复文件，没有覆盖，请重新打开程序".into())}
  let ledger=Self::connect(root,true).await?;
  let registration=serde_json::json!({"format":1,"database":"ledger.db","workspaceId":uuid::Uuid::new_v4().to_string(),"createdAt":stamp()});
  use std::io::Write;
  let mut file=std::fs::OpenOptions::new().create_new(true).write(true).open(root.join("workspace.json")).map_err(|_|"账本已创建但登记失败，请保留数据目录并恢复登记")?;
  file.write_all(registration.to_string().as_bytes()).and_then(|_|file.sync_all()).map_err(|_|"账本登记保存失败")?;
  Ok(ledger)
 }
 async fn connect(root:&Path,create:bool)->Result<Self>{
  let options=SqliteConnectOptions::new().filename(root.join("ledger.db")).create_if_missing(create).foreign_keys(true).journal_mode(sqlx::sqlite::SqliteJournalMode::Wal).busy_timeout(std::time::Duration::from_secs(10));
  let pool=SqlitePoolOptions::new().max_connections(1).connect_with(options).await.map_err(db_error)?;
  sqlx::raw_sql("CREATE TABLE IF NOT EXISTS orders(id TEXT PRIMARY KEY,revision INTEGER NOT NULL,body TEXT NOT NULL);CREATE TABLE IF NOT EXISTS money(id TEXT PRIMARY KEY,order_id TEXT NOT NULL REFERENCES orders(id),amount_cents INTEGER NOT NULL CHECK(amount_cents>0),body TEXT NOT NULL);CREATE TABLE IF NOT EXISTS history(id INTEGER PRIMARY KEY AUTOINCREMENT,entity_id TEXT NOT NULL,action TEXT NOT NULL,at TEXT NOT NULL,payload TEXT NOT NULL);").execute(&pool).await.map_err(db_error)?;
  sqlx::query("CREATE TABLE IF NOT EXISTS preferences(id INTEGER PRIMARY KEY CHECK(id=1),body TEXT NOT NULL)").execute(&pool).await.map_err(db_error)?;
  let result=Self{pool,root:root.to_path_buf()};result.initialize_sync().await?;Ok(result)
 }
 pub async fn snapshot(&self)->Result<serde_json::Value>{
  let mut tx=self.pool.begin().await.map_err(db_error)?;
  let orders=sqlx::query_scalar::<_,String>("SELECT body FROM orders ORDER BY rowid DESC").fetch_all(&mut *tx).await.map_err(db_error)?;
  let money=sqlx::query_scalar::<_,String>("SELECT body FROM money ORDER BY rowid DESC").fetch_all(&mut *tx).await.map_err(db_error)?;
  tx.commit().await.map_err(db_error)?;
  let parse=|rows:Vec<String>|->Result<Vec<serde_json::Value>>{rows.into_iter().map(|r|serde_json::from_str(&r).map_err(|_|"账本记录格式损坏，请从备份恢复".into())).collect()};
  Ok(serde_json::json!({"orders":parse(orders)?,"money":parse(money)?,"preferences":self.preferences().await?,"workspaceId":self.workspace_id()?,"dataDirectory":self.root.to_string_lossy(),"remoteConfirmed":false}))
 }
 pub fn directory(&self)->&Path{&self.root}
 pub fn workspace_id(&self)->Result<String>{let value:serde_json::Value=serde_json::from_slice(&std::fs::read(self.root.join("workspace.json")).map_err(|_|"无法读取工作空间登记")?).map_err(|_|"工作空间登记损坏")?;value["workspaceId"].as_str().map(str::to_owned).ok_or("缺少工作空间标识".into())}
 pub async fn preferences(&self)->Result<Preferences>{match sqlx::query_scalar::<_,String>("SELECT body FROM preferences WHERE id=1").fetch_optional(&self.pool).await.map_err(db_error)?{Some(body)=>serde_json::from_str(&body).map_err(|_|"偏好设置格式不兼容".into()),None=>Ok(Preferences::default())}}
 pub async fn save_preferences(&self,value:Preferences)->Result<()>{
  if value.physical_reminder_days>90||value.digital_reminder_days>90||value.receipt_method.len()>100||value.cost_method.len()>100||!["physical","simulation","schematic","code","other"].contains(&value.default_kind.as_str()){return Err("提醒天数应为0至90天，其他设置请填写有效选项".into())}
  sqlx::query("INSERT INTO preferences VALUES(1,?) ON CONFLICT(id) DO UPDATE SET body=excluded.body").bind(serde_json::to_string(&value).map_err(|_|"设置格式无效")?).execute(&self.pool).await.map_err(db_error)?;Ok(())
 }
 pub async fn relocate(&self,target:&Path)->Result<Self>{
  if !target.is_absolute(){return Err("请选择完整的数据文件夹路径".into())}
  if target==self.root{return Err("这已经是当前数据目录".into())}
  if target.join("ledger.db").exists()||target.join("workspace.json").exists(){return Err("目标目录已有账本，请选择空目录，避免覆盖".into())}
  let protection=self.backup().await?;
  std::fs::create_dir_all(target).map_err(|_|"无法创建目标数据目录")?;
  use std::io::Write;
  let mut out=std::fs::OpenOptions::new().create_new(true).write(true).open(target.join("ledger.db")).map_err(|_|"目标目录不能写入，未切换工作空间")?;
  let bytes=std::fs::read(&protection).map_err(|_|"无法读取保护备份")?;out.write_all(&bytes).and_then(|_|out.sync_all()).map_err(|_|"目标数据库写入失败，原目录保持不变")?;drop(out);
  std::fs::copy(self.root.join("workspace.json"),target.join("workspace.json")).map_err(|_|"目标登记文件写入失败，原目录保持不变")?;
  // Keep all old files intact. The new directory receives its own protection backup.
  let result=Self::open(target).await?.ok_or("目标工作空间未就绪")?;result.backup().await?;Ok(result)
 }
 pub async fn save_order(&self,mut order:Order)->Result<Order>{
  order.validate()?;
  let mut tx=self.pool.begin().await.map_err(db_error)?;
  if let Some(parent)=&order.parent_id{if sqlx::query_scalar::<_,i64>("SELECT count(*) FROM orders WHERE id=?").bind(parent).fetch_one(&mut *tx).await.map_err(db_error)?==0{return Err("关联原单不存在".into())}}
  let current=sqlx::query("SELECT revision,body FROM orders WHERE id=?").bind(&order.id).fetch_optional(&mut *tx).await.map_err(db_error)?;
  match current{
   Some(row)=>{if row.get::<i64,_>(0)!=order.revision{return Err("此订单已在其他窗口更新，请刷新后重试".into())}
    let existing:Order=serde_json::from_str(&row.get::<String,_>(1)).map_err(|_|"原订单损坏")?;if existing.deleted_at.is_some(){return Err("回收站项目请先恢复再编辑".into())}order.deleted_at=existing.deleted_at;
    order.revision=order.revision.checked_add(1).ok_or("订单版本超出范围")?;
   },None=>{if order.revision!=0{return Err("原订单不存在，未新建替代记录".into())}order.revision=1;order.deleted_at=None;}
  }
  let body=serde_json::to_string(&order).map_err(|_|"订单格式无效")?;
  sqlx::query("INSERT INTO orders VALUES(?,?,?) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,body=excluded.body").bind(&order.id).bind(order.revision).bind(&body).execute(&mut *tx).await.map_err(db_error)?;
  sqlx::query("INSERT INTO history(entity_id,action,at,payload) VALUES(?,'save-order',?,?)").bind(&order.id).bind(stamp()).bind(body).execute(&mut *tx).await.map_err(db_error)?;
  tx.commit().await.map_err(db_error)?;Ok(order)
 }
 pub async fn trash(&self,id:&str,revision:i64,restore:bool)->Result<()>{
  let mut tx=self.pool.begin().await.map_err(db_error)?;
  let row=sqlx::query("SELECT revision,body FROM orders WHERE id=?").bind(id).fetch_optional(&mut *tx).await.map_err(db_error)?.ok_or("订单不存在")?;
  if row.get::<i64,_>(0)!=revision{return Err("订单已更新，请刷新后重试".into())}
  let mut order:Order=serde_json::from_str(&row.get::<String,_>(1)).map_err(|_|"订单格式无效")?;
  order.deleted_at=if restore{None}else{Some(stamp())};order.revision+=1;
  let body=serde_json::to_string(&order).map_err(|_|"订单格式无效")?;
  sqlx::query("UPDATE orders SET revision=?,body=? WHERE id=?").bind(order.revision).bind(&body).bind(id).execute(&mut *tx).await.map_err(db_error)?;
  sqlx::query("INSERT INTO history(entity_id,action,at,payload) VALUES(?,?,?,?)").bind(id).bind(if restore{"restore-order"}else{"trash-order"}).bind(stamp()).bind(body).execute(&mut *tx).await.map_err(db_error)?;
  tx.commit().await.map_err(db_error)?;Ok(())
 }
 pub async fn add_money(&self,mut entry:MoneyEntry)->Result<MoneyEntry>{
  uuid(&entry.id)?;uuid(&entry.order_id)?;
  if !["receipt","refund","cost","cost-refund"].contains(&entry.kind.as_str()){return Err("收支类型无效".into())}
  let cents=amount(&entry.amount_cents)?;if cents==0{return Err("请输入大于零的金额".into())}optional_date(&entry.date)?;
  if entry.method.len()>100||entry.note.len()>12000{return Err("填写内容过长".into())}
  entry.voided_at=None;entry.void_reason=None;
  let body=serde_json::to_string(&entry).map_err(|_|"流水格式无效")?;
  let mut tx=self.pool.begin().await.map_err(db_error)?;
  if let Some(previous)=sqlx::query_scalar::<_,String>("SELECT body FROM money WHERE id=?").bind(&entry.id).fetch_optional(&mut *tx).await.map_err(db_error)?{
   if previous==body{return Ok(entry)}return Err("这笔流水已经记录过，内容不一致，请刷新核对".into())
  }
  let order:Order=serde_json::from_str(&sqlx::query_scalar::<_,String>("SELECT body FROM orders WHERE id=?").bind(&entry.order_id).fetch_optional(&mut *tx).await.map_err(db_error)?.ok_or("请先保存订单")?).map_err(|_|"订单格式无效")?;
  if order.deleted_at.is_some(){return Err("回收站订单请先恢复再记账".into())}
  if entry.kind=="refund"||entry.kind=="cost-refund"{
   let rows=sqlx::query_scalar::<_,String>("SELECT body FROM money WHERE order_id=?").bind(&entry.order_id).fetch_all(&mut *tx).await.map_err(db_error)?;
   let mut net=0i128;
   for row in rows{let previous:MoneyEntry=serde_json::from_str(&row).map_err(|_|"已有流水格式损坏")?;if previous.voided_at.is_some(){continue}let value=amount(&previous.amount_cents)? as i128;
    if (entry.kind=="refund"&&previous.kind=="receipt")||(entry.kind=="cost-refund"&&previous.kind=="cost"){net+=value}
    if previous.kind==entry.kind{net-=value}
   }
   if cents as i128>net{return Err(if entry.kind=="refund"{"退款不能超过这单当前净收款"}else{"支出退回不能超过这单当前实际支出"}.into())}
  }
  sqlx::query("INSERT INTO money VALUES(?,?,?,?)").bind(&entry.id).bind(&entry.order_id).bind(cents).bind(&body).execute(&mut *tx).await.map_err(db_error)?;
  sqlx::query("INSERT INTO history(entity_id,action,at,payload) VALUES(?,'add-money',?,?)").bind(&entry.order_id).bind(stamp()).bind(body).execute(&mut *tx).await.map_err(db_error)?;
  tx.commit().await.map_err(db_error)?;Ok(entry)
 }
 pub async fn void_money(&self,id:&str,reason:&str)->Result<()>{
  if reason.trim().is_empty(){return Err("请说明作废原因".into())}
  let mut tx=self.pool.begin().await.map_err(db_error)?;
  let body=sqlx::query_scalar::<_,String>("SELECT body FROM money WHERE id=?").bind(id).fetch_optional(&mut *tx).await.map_err(db_error)?.ok_or("流水不存在")?;
  let mut entry:MoneyEntry=serde_json::from_str(&body).map_err(|_|"流水格式损坏")?;
  if entry.voided_at.is_some(){return Ok(())}entry.voided_at=Some(stamp());entry.void_reason=Some(reason.trim().into());
  let body=serde_json::to_string(&entry).map_err(|_|"流水格式无效")?;
  sqlx::query("UPDATE money SET body=? WHERE id=?").bind(&body).bind(id).execute(&mut *tx).await.map_err(db_error)?;
  sqlx::query("INSERT INTO history(entity_id,action,at,payload) VALUES(?,'void-money',?,?)").bind(&entry.order_id).bind(stamp()).bind(body).execute(&mut *tx).await.map_err(db_error)?;
  tx.commit().await.map_err(db_error)?;Ok(())
 }
 pub async fn history(&self,id:&str)->Result<Vec<serde_json::Value>>{
  let rows=sqlx::query("SELECT action,at FROM history WHERE entity_id=? ORDER BY id DESC LIMIT 100").bind(id).fetch_all(&self.pool).await.map_err(db_error)?;
  Ok(rows.into_iter().map(|r|serde_json::json!({"action":r.get::<String,_>(0),"at":r.get::<String,_>(1)})).collect())
 }
 pub async fn backup(&self)->Result<String>{
  let folder=self.root.join("backups");std::fs::create_dir_all(&folder).map_err(|_|"无法创建备份目录")?;
  let dest=folder.join(format!("ledger-{}-{}.db",Local::now().format("%Y%m%d-%H%M%S"),uuid::Uuid::new_v4()));
  sqlx::query("VACUUM INTO ?").bind(dest.to_string_lossy().as_ref()).execute(&self.pool).await.map_err(db_error)?;
  std::fs::copy(self.root.join("workspace.json"),dest.with_extension("workspace.json")).map_err(|_|"数据库备份已写入，但登记文件备份失败，请保留当前数据目录")?;
  Ok(dest.to_string_lossy().into_owned())
 }
 pub fn backups(&self)->Result<Vec<serde_json::Value>>{
  let folder=self.root.join("backups");if !folder.exists(){return Ok(vec![])}
  let mut result=vec![];
  for entry in std::fs::read_dir(folder).map_err(|_|"无法读取备份目录")?{
   let entry=entry.map_err(|_|"无法读取备份文件")?;let path=entry.path();
   if path.extension().is_some_and(|e|e=="db")&&path.with_extension("workspace.json").is_file(){let info=entry.metadata().map_err(|_|"无法读取备份信息")?;
    if info.is_file(){result.push(serde_json::json!({"name":entry.file_name().to_string_lossy(),"bytes":info.len()}))}
   }
  }
  result.sort_by(|a,b|b["name"].as_str().cmp(&a["name"].as_str()));Ok(result)
 }
 pub async fn restore(&self,name:&str)->Result<String>{
  if !name.starts_with("ledger-")||!name.ends_with(".db")||!name.bytes().all(|b|b.is_ascii_alphanumeric()||b==b'-'||b==b'.'){return Err("请选择本工作空间的备份文件".into())}
  let source=self.root.join("backups").join(name);
  let read_meta=|path:PathBuf|->Result<serde_json::Value>{serde_json::from_slice(&std::fs::read(path).map_err(|_|"备份登记文件缺失")?).map_err(|_|"备份登记格式损坏".into())};
  let metadata=read_meta(source.with_extension("workspace.json"))?;let current=read_meta(self.root.join("workspace.json"))?;
  if metadata["workspaceId"]!=current["workspaceId"]||metadata["format"]!=1{return Err("这不是当前工作空间的备份，未覆盖本机数据".into())}
  let options=SqliteConnectOptions::new().filename(&source).create_if_missing(false).read_only(true);
  let input=SqlitePoolOptions::new().max_connections(1).connect_with(options).await.map_err(|_|"无法打开所选备份")?;
  let check=sqlx::query_scalar::<_,String>("PRAGMA integrity_check").fetch_all(&input).await.map_err(|_|"无法检查备份完整性")?;
  if check!=vec!["ok"]{input.close().await;return Err("备份完整性检查未通过，当前数据保持不变".into())}
  let orders=sqlx::query("SELECT id,revision,body FROM orders").fetch_all(&input).await.map_err(|_|"备份不包含可恢复的项目表")?;
  let entries=sqlx::query("SELECT id,order_id,amount_cents,body FROM money").fetch_all(&input).await.map_err(|_|"备份不包含可恢复的流水表")?;
  let event_column:i64=sqlx::query_scalar("SELECT count(*) FROM pragma_table_info('history') WHERE name='event_id'").fetch_one(&input).await.map_err(db_error)?;
  let history=sqlx::query(if event_column>0{"SELECT id,entity_id,action,at,payload,event_id FROM history"}else{"SELECT id,entity_id,action,at,payload,NULL AS event_id FROM history"}).fetch_all(&input).await.map_err(|_|"备份不包含可恢复的历史表")?;
  let has_preferences=sqlx::query_scalar::<_,i64>("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='preferences'").fetch_one(&input).await.map_err(db_error)?;
  let preferences=if has_preferences>0{sqlx::query_scalar::<_,String>("SELECT body FROM preferences WHERE id=1").fetch_optional(&input).await.map_err(db_error)?}else{None};
  if let Some(body)=&preferences{serde_json::from_str::<Preferences>(body).map_err(|_|"备份偏好设置不兼容")?;}
  for row in &orders{let order:Order=serde_json::from_str(&row.get::<String,_>(2)).map_err(|_|"备份订单格式不兼容")?;if order.id!=row.get::<String,_>(0)||order.revision!=row.get::<i64,_>(1){return Err("备份订单标识不一致".into())}}
  for row in &entries{let entry:MoneyEntry=serde_json::from_str(&row.get::<String,_>(3)).map_err(|_|"备份流水格式不兼容")?;if entry.id!=row.get::<String,_>(0)||entry.order_id!=row.get::<String,_>(1)||amount(&entry.amount_cents)?!=row.get::<i64,_>(2){return Err("备份流水金额或标识不一致".into())}}
  let has_generation:i64=sqlx::query_scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='ledger_meta'").fetch_one(&input).await.map_err(db_error)?;
  let generation=if has_generation>0{sqlx::query_scalar::<_,String>("SELECT generation FROM ledger_meta WHERE id=1").fetch_optional(&input).await.map_err(db_error)?.unwrap_or_else(desk_sync::protocol::legacy_generation)}else{desk_sync::protocol::legacy_generation()};
  if generation!="legacy"{uuid::Uuid::parse_str(&generation).map_err(|_|"备份重置标识无效")?;}
  input.close().await;
  let current_revisions=sqlx::query("SELECT id,revision FROM orders").fetch_all(&self.pool).await.map_err(db_error)?.into_iter().map(|r|(r.get::<String,_>(0),r.get::<i64,_>(1))).collect::<std::collections::HashMap<_,_>>();
  let mut restored_orders=Vec::new();
  for row in orders{let mut order:Order=serde_json::from_str(&row.get::<String,_>(2)).map_err(|_|"备份订单格式不兼容")?;order.revision=order.revision.max(*current_revisions.get(&order.id).unwrap_or(&0)).checked_add(1).ok_or("订单版本超出范围")?;restored_orders.push(order);}
  let protection=self.backup().await?;
  let mut tx=self.pool.begin().await.map_err(db_error)?;
  sqlx::query("INSERT INTO ledger_meta VALUES(1,?) ON CONFLICT(id) DO UPDATE SET generation=excluded.generation").bind(&generation).execute(&mut *tx).await.map_err(db_error)?;
  for statement in ["DELETE FROM history","DELETE FROM money","DELETE FROM orders"]{sqlx::query(statement).execute(&mut *tx).await.map_err(db_error)?;}
  sqlx::query("DELETE FROM preferences").execute(&mut *tx).await.map_err(db_error)?;
  if let Some(body)=preferences{sqlx::query("INSERT INTO preferences VALUES(1,?)").bind(body).execute(&mut *tx).await.map_err(db_error)?;}
  for order in restored_orders{let body=serde_json::to_string(&order).map_err(|_|"无法恢复订单")?;sqlx::query("INSERT INTO orders VALUES(?,?,?)").bind(&order.id).bind(order.revision).bind(body).execute(&mut *tx).await.map_err(db_error)?;}
  for row in entries{sqlx::query("INSERT INTO money VALUES(?,?,?,?)").bind(row.get::<String,_>(0)).bind(row.get::<String,_>(1)).bind(row.get::<i64,_>(2)).bind(row.get::<String,_>(3)).execute(&mut *tx).await.map_err(db_error)?;}
  for row in history{
   let identity=row.get::<Option<String>,_>(5).unwrap_or(desk_sync::protocol::hash(&serde_json::json!({"entityId":row.get::<String,_>(1),"action":row.get::<String,_>(2),"at":row.get::<String,_>(3),"payload":row.get::<String,_>(4)}))?);
   sqlx::query("INSERT INTO history(id,entity_id,action,at,payload,event_id) VALUES(?,?,?,?,?,?)").bind(row.get::<i64,_>(0)).bind(row.get::<String,_>(1)).bind(row.get::<String,_>(2)).bind(row.get::<String,_>(3)).bind(row.get::<String,_>(4)).bind(identity).execute(&mut *tx).await.map_err(db_error)?;
  }
  sqlx::query("INSERT INTO history(entity_id,action,at,payload) VALUES('workspace','restore-workspace',?,?)").bind(stamp()).bind(serde_json::json!({"source":name,"protectionBackup":protection}).to_string()).execute(&mut *tx).await.map_err(db_error)?;
  tx.commit().await.map_err(db_error)?;Ok(protection)
 }
}
