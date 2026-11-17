use super::*;
fn book()->Snapshot{
 let mut s=Snapshot::empty("11111111-1111-4111-8111-111111111111");s.preferences=json!({});
 s.orders.insert("order".into(),json!({"id":"order","number":"SYNTHETIC","title":"合成账本","status":"doing","kind":"simulation","acceptedDate":"2026-11-16","dueDate":"2026-11-17","completedDate":null,"deletedAt":null,"quoteCents":"10000","tasks":[]}));s
}
fn fixture(local:Snapshot,remote:Snapshot,baseline:Option<Snapshot>,mode:&str)->(SyncState,Plan){
 let mut state=SyncState::new(TEST_REPO.into()).unwrap();state.baseline=baseline;
 let plan=Plan{rules:2,local,head:remote::Head{sha:"0".repeat(40),tree:"1".repeat(40),branch_exists:true,snapshot:Some(remote),snapshot_blob:Some("2".repeat(40))},backup_path:"synthetic-backup".into(),created_at:"2026-11-16T10:00:00+08:00".into(),mode:mode.into(),target_generation:None};(state,plan)
}
#[test]fn empty_device_gets_full_remote_but_does_not_upload_or_erase(){
 let remote=book();let local=Snapshot::empty(&remote.workspace_id);let (mut state,plan)=fixture(local,remote.clone(),None,"download");state.pending=Some(plan.clone());
 let result=result_for(&state,&plan,&BTreeMap::new()).unwrap();assert_eq!(result.snapshot.orders,remote.orders);
 let summary=plan_summary(&state).unwrap();assert_eq!(summary["initialDownload"],true);assert_eq!(summary["needsUpload"],false);assert_eq!(summary["merged"]["orders"],1);
}
#[test]fn stale_device_blocked_after_reset_and_explicit_download_discards_old_only_records(){
 let old=book();let mut current=old.clone();current.generation="22222222-2222-4222-8222-222222222222".into();current.orders.clear();current.orders.insert("real".into(),json!({"id":"real","number":"REAL","quoteCents":"5000","status":"done","deletedAt":null}));
 let (mut state,mut plan)=fixture(old.clone(),current.clone(),Some(old),"merge");state.pending=Some(plan.clone());assert!(result_for(&state,&plan,&BTreeMap::new()).is_err());assert_eq!(plan_summary(&state).unwrap()["resetDetected"],true);
 plan.mode="download".into();let result=result_for(&state,&plan,&BTreeMap::new()).unwrap();assert!(!result.snapshot.orders.contains_key("order"));assert_eq!(result.snapshot.orders,current.orders);
}
#[test]fn populated_unrelated_device_requires_explicit_direction(){
 let b=book();let (state,plan)=fixture(b.clone(),b,None,"merge");assert!(result_for(&state,&plan,&BTreeMap::new()).is_err());
}
#[test]fn reset_replaces_remote_instead_of_union_and_legacy_upgrade_keeps_business(){
 let local=book();let mut remote=local.clone();remote.orders.insert("old-test".into(),json!({"id":"old-test"}));
 let (state,mut plan)=fixture(local.clone(),remote,Some(local.clone()),"reset");plan.target_generation=Some("33333333-3333-4333-8333-333333333333".into());
 let result=result_for(&state,&plan,&BTreeMap::new()).unwrap();assert_eq!(result.snapshot.orders,local.orders);assert_eq!(result.snapshot.generation,plan.target_generation.clone().unwrap());
 let mut old=local.clone();old.format=1;let (state,mut plan)=fixture(local.clone(),old.clone(),Some(old),"merge");plan.target_generation=Some("44444444-4444-4444-8444-444444444444".into());let result=result_for(&state,&plan,&BTreeMap::new()).unwrap();assert_eq!(result.snapshot.format,2);assert_eq!(result.snapshot.orders.len(),1);assert_ne!(result.snapshot.generation,"legacy");
}
#[test]fn financial_preview_ignores_voided_and_trashed_and_keeps_integer_cents(){
 let mut s=book();s.money.insert("receipt".into(),json!({"orderId":"order","kind":"receipt","amountCents":"7000","voidedAt":null}));s.money.insert("cost".into(),json!({"orderId":"order","kind":"cost","amountCents":"3500","voidedAt":null}));s.money.insert("void".into(),json!({"orderId":"order","kind":"cost","amountCents":"9000","voidedAt":"2026-11-16"}));
 let c=counts(&s);assert_eq!(c["receivedCents"],"7000");assert_eq!(c["profitCents"],"3500");assert_eq!(c["remainingCents"],"3000");s.orders.get_mut("order").unwrap()["deletedAt"]=json!("2026-11-16");assert_eq!(counts(&s)["receivedCents"],"0");
}
#[test]fn independently_entered_similar_receipts_warn_but_both_are_retained(){
 let b=book();let mut l=b.clone();let mut r=b.clone();let entry=json!({"orderId":"order","kind":"receipt","amountCents":"500","date":null,"voidedAt":null});l.money.insert("local-entry".into(),entry.clone());r.money.insert("remote-entry".into(),entry);
 let (state,plan)=fixture(l,r,Some(b),"merge");assert_eq!(duplicate_warnings(&state,&plan).len(),1);let merged=result_for(&state,&plan,&BTreeMap::new()).unwrap();assert_eq!(merged.snapshot.money.len(),2);
}
