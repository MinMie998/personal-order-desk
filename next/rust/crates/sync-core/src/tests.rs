use crate::protocol::*;
use serde_json::json;
use std::collections::BTreeMap;
fn task()->serde_json::Value{json!({"id":"task-1","parentId":null,"title":"焊接","status":"doing","notes":"原备注","dueDate":"2026-11-17","startedAt":"2026-11-16T10:00:00+08:00","completedAt":null})}
#[test]fn task_distinct_fields_merge_but_same_field_conflicts(){
 let mut b=book();b.orders.get_mut("order").unwrap()["tasks"]=json!([task()]);let mut l=b.clone();let mut r=b.clone();
 l.orders.get_mut("order").unwrap()["tasks"][0]["notes"]=json!("本机焊接记录");r.orders.get_mut("order").unwrap()["tasks"][0]["dueDate"]=json!("2026-11-18");
 let m=merge(&b,&l,&r,&BTreeMap::new()).unwrap();assert!(m.conflicts.is_empty());assert_eq!(m.snapshot.orders["order"]["tasks"][0]["notes"],"本机焊接记录");assert_eq!(m.snapshot.orders["order"]["tasks"][0]["dueDate"],"2026-11-18");
 r.orders.get_mut("order").unwrap()["tasks"][0]["notes"]=json!("另一段记录");let m=merge(&b,&l,&r,&BTreeMap::new()).unwrap();assert_eq!(m.conflicts.len(),1);assert_eq!(m.conflicts[0].field,"task:task-1:notes");
}
#[test]fn task_cancel_edit_conflict_and_parent_summaries_are_derived(){
 let mut b=book();b.orders.get_mut("order").unwrap()["tasks"]=json!([task()]);let mut l=b.clone();let mut r=b.clone();l.orders.get_mut("order").unwrap()["tasks"][0]["status"]=json!("cancelled");r.orders.get_mut("order").unwrap()["tasks"][0]["notes"]=json!("已经完成焊接");
 assert_eq!(merge(&b,&l,&r,&BTreeMap::new()).unwrap().conflicts[0].field,"task:task-1");
 let mut parent=task();parent["id"]=json!("parent");let mut a=task();a["parentId"]=json!("parent");let mut c=a.clone();c["id"]=json!("task-2");b.orders.get_mut("order").unwrap()["tasks"]=json!([parent,a,c]);l=b.clone();r=b.clone();
 l.orders.get_mut("order").unwrap()["tasks"][1]["status"]=json!("done");r.orders.get_mut("order").unwrap()["tasks"][2]["status"]=json!("done");
 let m=merge(&b,&l,&r,&BTreeMap::new()).unwrap();assert!(m.conflicts.is_empty());assert_eq!(m.snapshot.orders["order"]["tasks"][0]["status"],"done");
}
#[test]fn different_reset_generations_cannot_merge_and_legacy_is_readable(){
 let b=book();let mut r=b.clone();r.generation="22222222-2222-4222-8222-222222222222".into();assert!(merge(&b,&b,&r,&BTreeMap::new()).is_err());
 let mut old=serde_json::to_value(&b).unwrap();old["format"]=json!(1);old.as_object_mut().unwrap().remove("generation");let parsed:Snapshot=serde_json::from_value(old).unwrap();assert_eq!(parsed.generation,"legacy");assert_eq!(parsed.digest().unwrap(),b.digest().unwrap());
}
fn book()->Snapshot{let mut s=Snapshot::empty("11111111-1111-4111-8111-111111111111");s.orders.insert("order".into(),json!({"id":"order","title":"合成项目","progress":"原始","quoteCents":"9007199254740993","revision":1,"deletedAt":null,"status":"doing","completedDate":null,"kind":"simulation","acceptedDate":"2026-11-15","dueDate":"2026-11-16"}));s}
#[test]fn parent_time_summary_compares_instants_across_offsets(){
 let mut b=book();let mut parent=task();parent["id"]=json!("parent");
 let mut a=task();a["parentId"]=json!("parent");a["status"]=json!("done");a["startedAt"]=json!("2026-11-17T08:00:00+08:00");a["completedAt"]=json!("2026-11-17T09:00:00+08:00");
 let mut c=a.clone();c["id"]=json!("task-2");c["startedAt"]=json!("2026-11-17T01:00:00+00:00");c["completedAt"]=json!("2026-11-17T02:00:00+00:00");
 b.orders.get_mut("order").unwrap()["tasks"]=json!([parent,a,c]);
 let result=merge(&b,&b,&b,&BTreeMap::new()).unwrap();let p=&result.snapshot.orders["order"]["tasks"][0];
 assert_eq!(p["startedAt"],"2026-11-17T08:00:00+08:00");assert_eq!(p["completedAt"],"2026-11-17T02:00:00+00:00");
}
#[test]fn remote_task_order_is_received_and_concurrent_orders_need_choice(){
 let mut b=book();let a=task();let mut c=task();c["id"]=json!("task-2");let mut d=task();d["id"]=json!("task-3");
 b.orders.get_mut("order").unwrap()["tasks"]=json!([a.clone(),c.clone(),d.clone()]);let mut l=b.clone();let mut r=b.clone();
 l.orders.get_mut("order").unwrap()["progress"]=json!("手机编辑进度");r.orders.get_mut("order").unwrap()["tasks"]=json!([d.clone(),a.clone(),c.clone()]);
 let result=merge(&b,&l,&r,&BTreeMap::new()).unwrap();assert!(result.conflicts.is_empty());assert_eq!(result.snapshot.orders["order"]["tasks"][0]["id"],"task-3");
 l.orders.get_mut("order").unwrap()["tasks"]=json!([c.clone(),a.clone(),d.clone()]);let result=merge(&b,&l,&r,&BTreeMap::new()).unwrap();assert!(result.conflicts.iter().any(|c|c.field=="taskOrder"));
 let result=merge(&b,&l,&r,&BTreeMap::from([("orders/order/taskOrder".into(),"remote".into())])).unwrap();assert!(result.conflicts.is_empty());assert_eq!(result.snapshot.orders["order"]["tasks"][0]["id"],"task-3");
 l.orders.get_mut("order").unwrap()["tasks"]=json!([a,d]);let result=merge(&b,&l,&b,&BTreeMap::new()).unwrap();assert!(result.conflicts.is_empty());assert_eq!(result.snapshot.orders["order"]["tasks"].as_array().unwrap().len(),3);
}
#[test]fn distinct_fields_merge_and_revision_does_not_loop(){let b=book();let mut l=b.clone();let mut r=b.clone();l.orders.get_mut("order").unwrap()["progress"]=json!("本地进度");r.orders.get_mut("order").unwrap()["title"]=json!("远端名称");r.orders.get_mut("order").unwrap()["revision"]=json!(50);let m=merge(&b,&l,&r,&BTreeMap::new()).unwrap();assert!(m.conflicts.is_empty());assert_eq!(m.snapshot.orders["order"]["progress"],"本地进度");assert_eq!(m.snapshot.orders["order"]["title"],"远端名称");let mut applied=m.snapshot.clone();applied.orders.get_mut("order").unwrap()["revision"]=json!(100);assert_eq!(applied.digest().unwrap(),m.snapshot.digest().unwrap());}
#[test]fn same_field_and_completion_group_need_explicit_choice(){let b=book();let mut l=b.clone();let mut r=b.clone();l.orders.get_mut("order").unwrap()["progress"]=json!("L");r.orders.get_mut("order").unwrap()["progress"]=json!("R");l.orders.get_mut("order").unwrap()["status"]=json!("done");l.orders.get_mut("order").unwrap()["completedDate"]=json!("2026-11-15");r.orders.get_mut("order").unwrap()["status"]=json!("waiting");let unresolved=merge(&b,&l,&r,&BTreeMap::new()).unwrap();assert_eq!(unresolved.conflicts.len(),2);let choices=unresolved.conflicts.iter().map(|c|(c.id.clone(),"remote".into())).collect();let m=merge(&b,&l,&r,&choices).unwrap();assert!(m.conflicts.is_empty());assert_eq!(m.snapshot.orders["order"]["progress"],"R");assert_eq!(m.snapshot.orders["order"]["status"],"waiting");assert!(m.snapshot.orders["order"]["completedDate"].is_null());}
#[test]fn independent_money_is_unioned_and_same_uuid_is_atomic(){let b=book();let mut l=b.clone();let mut r=b.clone();l.money.insert("l".into(),json!({"orderId":"order","amountCents":"500","kind":"receipt"}));r.money.insert("r".into(),json!({"orderId":"order","amountCents":"700","kind":"receipt"}));let m=merge(&b,&l,&r,&BTreeMap::new()).unwrap();assert_eq!(m.snapshot.money.len(),2);assert!(m.conflicts.is_empty());r.money.insert("l".into(),json!({"orderId":"order","amountCents":"900","kind":"receipt"}));let m=merge(&b,&l,&r,&BTreeMap::new()).unwrap();assert!(m.conflicts.iter().any(|c|c.collection=="money"&&c.field=="record"));}
#[test]fn deleting_while_other_side_records_money_is_not_silent(){let b=book();let mut l=b.clone();let mut r=b.clone();l.orders.get_mut("order").unwrap()["deletedAt"]=json!("2026-11-15T12:00:00+08:00");r.money.insert("new".into(),json!({"orderId":"order","amountCents":"100","kind":"receipt"}));let m=merge(&b,&l,&r,&BTreeMap::new()).unwrap();assert!(m.conflicts.iter().any(|c|c.collection=="orders"&&c.field=="record"));assert!(m.snapshot.money.contains_key("new"));}
#[test]fn restoring_old_backup_does_not_hard_delete_known_records(){let b=book();let mut l=b.clone();l.orders.clear();let m=merge(&b,&l,&b,&BTreeMap::new()).unwrap();assert!(m.snapshot.orders.contains_key("order"));let mut r=b.clone();l.history.insert("event".into(),json!({"payload":"original"}));r.history.insert("event".into(),json!({"payload":"changed"}));assert!(merge(&b,&l,&r,&BTreeMap::new()).is_err());}
#[test]fn authenticated_encryption_nonce_tamper_and_identity(){let b=book();let key=new_key().unwrap();let encrypted=encrypt(&b,&key).unwrap();let another=encrypt(&b,&key).unwrap();assert_ne!(encrypted["nonce"],another["nonce"]);assert_eq!(decrypt(&encrypted,&key,&b.workspace_id).unwrap(),b);assert!(decrypt(&encrypted,&new_key().unwrap(),&b.workspace_id).is_err());assert!(decrypt(&encrypted,&key,"other-workspace").is_err());let mut tampered=encrypted.clone();tampered["ciphertext"]=json!("AAAA");assert!(decrypt(&tampered,&key,&b.workspace_id).is_err());assert!(!encrypted.to_string().contains("合成项目"));assert_eq!(canonical(&json!({"b":2,"a":"9007199254740993"})).unwrap(),"{\"a\":\"9007199254740993\",\"b\":2}");assert!(canonical(&json!(0.25)).is_err());}
