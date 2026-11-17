use sqlx::{sqlite::{SqliteConnectOptions,SqlitePoolOptions},SqlitePool,Row};
use std::path::Path;
pub mod ledger;
pub mod location;
pub mod syncing;
pub mod tasks;
/// R0 isolated note/outbox transaction probe. Not a production order or receipt store.
pub struct ProbeStore { pool:SqlitePool }
impl ProbeStore{
 pub async fn open(path:&Path)->Result<Self,sqlx::Error>{
  let options=SqliteConnectOptions::new().filename(path).create_if_missing(true).foreign_keys(true);
  let pool=SqlitePoolOptions::new().max_connections(1).connect_with(options).await?;
  sqlx::raw_sql("CREATE TABLE IF NOT EXISTS probe_items(id TEXT PRIMARY KEY,title TEXT NOT NULL,amount_cents INTEGER NOT NULL CHECK(amount_cents>=0));CREATE TABLE IF NOT EXISTS probe_operations(operation_id TEXT PRIMARY KEY,payload TEXT NOT NULL);").execute(&pool).await?;Ok(Self{pool})
 }
 pub async fn save(&self,operation:&str,id:&str,title:&str,cents:i64)->Result<bool,sqlx::Error>{
  let payload=serde_json::json!({"id":id,"title":title,"amount_cents":cents.to_string()}).to_string();
  let mut tx=self.pool.begin().await?;
  if let Some(row)=sqlx::query("SELECT payload FROM probe_operations WHERE operation_id=?").bind(operation).fetch_optional(&mut *tx).await?{
   if row.get::<String,_>(0)!=payload{return Err(sqlx::Error::Protocol("operation_payload_mismatch".into()))}return Ok(false)
  }
  sqlx::query("INSERT INTO probe_operations VALUES(?,?)").bind(operation).bind(payload).execute(&mut *tx).await?;
  sqlx::query("INSERT INTO probe_items VALUES(?,?,?) ON CONFLICT(id) DO UPDATE SET title=excluded.title,amount_cents=excluded.amount_cents").bind(id).bind(title).bind(cents).execute(&mut *tx).await?;
  tx.commit().await?;Ok(true)
 }
 pub async fn close(self){self.pool.close().await}
}
#[cfg(test)]mod tests{
 use super::*;
 #[tokio::test]async fn durable_exact_money_and_atomic_idempotency(){
  let dir=Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../../tmp/r0-storage");std::fs::create_dir_all(&dir).unwrap();let path=dir.join(format!("{}.db",uuid::Uuid::new_v4()));
  let db=ProbeStore::open(&path).await.unwrap();
  assert!(db.save("op1","item1","合成验证",9007199254740993).await.unwrap());
  assert!(!db.save("op1","item1","合成验证",9007199254740993).await.unwrap());
  assert!(db.save("op1","item1","changed",1).await.is_err());
  assert!(db.save("bad","bad","rollback",-1).await.is_err());
  let count:i64=sqlx::query_scalar("SELECT count(*) FROM probe_operations").fetch_one(&db.pool).await.unwrap();assert_eq!(count,1);
  db.close().await;let db=ProbeStore::open(&path).await.unwrap();
  let value:i64=sqlx::query_scalar("SELECT amount_cents FROM probe_items WHERE id='item1'").fetch_one(&db.pool).await.unwrap();assert_eq!(value,9007199254740993);
  db.close().await;std::fs::remove_file(path).unwrap();
 }
}
