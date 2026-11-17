//! Bounded, platform-neutral byte boundary. No filesystem, credentials or network.
use crate::protocol::{self, Snapshot};
use serde::Deserialize;
use serde_json::{json, Value};
use std::{collections::BTreeMap, panic::{catch_unwind, AssertUnwindSafe}};

pub const MAX_REQUEST_BYTES: usize = 16 * 1024 * 1024;
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Request {
    bridge_version: u32,
    operation: String,
    #[serde(default)] snapshot: Option<Snapshot>,
    #[serde(default)] base: Option<Snapshot>,
    #[serde(default)] local: Option<Snapshot>,
    #[serde(default)] remote: Option<Snapshot>,
    #[serde(default)] choices: BTreeMap<String, String>,
    #[serde(default)] key: Option<String>,
    #[serde(default)] envelope: Option<Value>,
    #[serde(default)] workspace_id: Option<String>,
}
fn snapshot(value: &Option<Snapshot>) -> Result<&Snapshot, String> {
    let s = value.as_ref().ok_or("缺少快照")?;
    if ![1, 2].contains(&s.format) { return Err("需要升级客户端：不支持的快照版本".into()); }
    if s.workspace_id.is_empty() { return Err("缺少账本身份".into()); }
    Ok(s)
}
fn run(bytes: &[u8]) -> Result<Value, String> {
    if bytes.len() > MAX_REQUEST_BYTES { return Err("请求超过16MiB限制".into()); }
    let r: Request = serde_json::from_slice(bytes).map_err(|_| "请求格式不兼容")?;
    if r.bridge_version != 1 { return Err("需要升级客户端：不支持的调用版本".into()); }
    match r.operation.as_str() {
        "keyInfo" => Ok(json!({"keyId":protocol::key_id(r.key.as_deref().ok_or("缺少恢复密钥")?)?})),
        "digest" => Ok(json!({"digest":snapshot(&r.snapshot)?.digest()?})),
        "normalize" => {
            let s = snapshot(&r.snapshot)?.normalized();
            Ok(json!({"snapshot":s,"canonical":protocol::canonical(&serde_json::to_value(&s).map_err(|_|"编码失败")?)?,"digest":s.digest()?}))
        }
        "merge" => {
            if r.choices.values().any(|s| !["local", "remote"].contains(&s.as_str())) { return Err("冲突选择无效".into()); }
            let m = protocol::merge(snapshot(&r.base)?, snapshot(&r.local)?, snapshot(&r.remote)?, &r.choices)?;
            Ok(json!({"snapshot":m.snapshot,"conflicts":m.conflicts,"digest":m.snapshot.digest()?}))
        }
        "encrypt" => protocol::encrypt(snapshot(&r.snapshot)?, r.key.as_deref().ok_or("缺少恢复密钥")?),
        "decrypt" => {
            let s = protocol::decrypt(r.envelope.as_ref().ok_or("缺少加密包")?, r.key.as_deref().ok_or("缺少恢复密钥")?, r.workspace_id.as_deref().ok_or("缺少账本身份")?)?;
            Ok(json!({"snapshot":s,"digest":s.digest()?}))
        }
        _ => Err("不支持的操作".into()),
    }
}
/// Never include input or key material in failures. JNI gets one consistent envelope.
pub fn dispatch(bytes: &[u8]) -> Vec<u8> {
    let response = match catch_unwind(AssertUnwindSafe(|| run(bytes))) {
        Ok(Ok(result)) => json!({"bridgeVersion":1,"ok":true,"result":result}),
        Ok(Err(error)) => json!({"bridgeVersion":1,"ok":false,"error":error}),
        Err(_) => json!({"bridgeVersion":1,"ok":false,"error":"同步核心内部错误，未应用数据"}),
    };
    protocol::canonical(&response).expect("fixed response schema").into_bytes()
}

#[cfg(test)] mod tests {
    use super::*;
    #[test] fn malformed_unknown_and_oversized_requests_fail_without_echo() {
        for bytes in [b"".to_vec(), b"not-json-secret".to_vec(), br#"{"bridgeVersion":99,"operation":"normalize"}"#.to_vec(), vec![b' ';MAX_REQUEST_BYTES+1]] {
            let v: Value = serde_json::from_slice(&dispatch(&bytes)).unwrap();
            assert_eq!(v["ok"],false); assert!(!v.to_string().contains("not-json-secret"));
        }
    }
    #[test] fn unknown_snapshot_version_is_not_normalized_away() {
        let mut s=Snapshot::empty("synthetic"); s.format=99;
        let v:Value=serde_json::from_slice(&dispatch(&serde_json::to_vec(&json!({"bridgeVersion":1,"operation":"normalize","snapshot":s})).unwrap())).unwrap();
        assert_eq!(v["ok"],false);
    }
    #[test] fn key_info_and_digest_do_not_return_key_material_or_duplicate_snapshot() {
        let key=protocol::new_key().unwrap();
        let output=dispatch(&serde_json::to_vec(&json!({"bridgeVersion":1,"operation":"keyInfo","key":key})).unwrap());
        let v:Value=serde_json::from_slice(&output).unwrap();assert_eq!(v["result"]["keyId"],protocol::key_id(&key).unwrap());assert!(!String::from_utf8(output).unwrap().contains(&key));
        let s=Snapshot::empty("synthetic");let v:Value=serde_json::from_slice(&dispatch(&serde_json::to_vec(&json!({"bridgeVersion":1,"operation":"digest","snapshot":s})).unwrap())).unwrap();
        assert_eq!(v["result"]["digest"],s.digest().unwrap());assert!(v["result"].get("snapshot").is_none());
    }
}
