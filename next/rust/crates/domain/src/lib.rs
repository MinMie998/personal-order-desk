use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};

/// Wire values remain strings: money is never parsed through a floating-point number.
pub fn parse_cents(value: &str) -> Result<i64, &'static str> {
    if value.is_empty() || value == "-0" || value.starts_with('+') { return Err("invalid_cents"); }
    let digits = value.strip_prefix('-').unwrap_or(value);
    if !digits.bytes().all(|b| b.is_ascii_digit()) || (digits.len() > 1 && digits.starts_with('0')) { return Err("invalid_cents"); }
    value.parse().map_err(|_| "cents_overflow")
}
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct Conflict { pub field: String, pub baseline: Option<String>, pub local: Option<String>, pub remote: Option<String> }
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct MergeResult { pub fields: BTreeMap<String,String>, pub conflicts: Vec<Conflict> }
/// Field merge prototype only. Financial transaction groups are not accepted by this API.
pub fn merge_fields(base: &BTreeMap<String,String>, local: &BTreeMap<String,String>, remote: &BTreeMap<String,String>) -> MergeResult {
    let keys: BTreeSet<_> = base.keys().chain(local.keys()).chain(remote.keys()).cloned().collect();
    let mut result = MergeResult { fields: BTreeMap::new(), conflicts: vec![] };
    for key in keys {
        let b=base.get(&key); let l=local.get(&key); let r=remote.get(&key);
        let chosen = if l == r { l } else if l == b { r } else if r == b { l } else {
            result.conflicts.push(Conflict {field:key.clone(),baseline:b.cloned(),local:l.cloned(),remote:r.cloned()});
            // A conflicting field is withheld; callers must not persist an unresolved result.
            continue;
        };
        if let Some(value)=chosen { result.fields.insert(key,value.clone()); }
    }
    result
}
#[cfg(test)] mod tests {
 use super::*;
 #[test] fn money_is_exact_and_checked() {
  assert_eq!(parse_cents("9007199254740993"),Ok(9007199254740993));
  for bad in ["", "01", "1.0", "+1", "-0", "9223372036854775808"] { assert!(parse_cents(bad).is_err(),"{bad}"); }
 }
 #[test] fn shared_merge_contracts() {
  let vectors: serde_json::Value=serde_json::from_str(include_str!("../../../../contracts/field-merge-vectors.json")).unwrap();
  for v in vectors.as_array().unwrap() {
   let b=serde_json::from_value(v["baseline"].clone()).unwrap();
   let l=serde_json::from_value(v["local"].clone()).unwrap();
   let r=serde_json::from_value(v["remote"].clone()).unwrap();
   let result=merge_fields(&b,&l,&r);
   assert_eq!(serde_json::to_value(&result.fields).unwrap(),v["expected_fields"],"{}",v["name"]);
   assert_eq!(result.conflicts.iter().map(|c| c.field.clone()).collect::<Vec<_>>(),serde_json::from_value::<Vec<String>>(v["expected_conflicts"].clone()).unwrap());
  }
 }
}

#[cfg(test)] mod crypto_vectors {
 use aes_gcm::{Aes256Gcm,KeyInit,Nonce,aead::{Aead,Payload}};
 #[test] fn shared_aes_gcm_vector_and_tamper() {
  let v:serde_json::Value=serde_json::from_str(include_str!("../../../../contracts/aes-gcm-vector.json")).unwrap();
  let decode=|key:&str|hex::decode(v[key].as_str().unwrap()).unwrap();
  let key=decode("key_hex");let nonce=decode("nonce_hex");let aad=decode("aad_hex");let plaintext=decode("plaintext_hex");let expected=decode("ciphertext_and_tag_hex");
  let cipher=Aes256Gcm::new_from_slice(&key).unwrap();
  assert_eq!(cipher.encrypt(Nonce::from_slice(&nonce),Payload{msg:&plaintext,aad:&aad}).unwrap(),expected);
  assert_eq!(cipher.decrypt(Nonce::from_slice(&nonce),Payload{msg:&expected,aad:&aad}).unwrap(),plaintext);
  let mut bad=expected.clone();bad[0]^=1;assert!(cipher.decrypt(Nonce::from_slice(&nonce),Payload{msg:&bad,aad:&aad}).is_err());
  assert!(cipher.decrypt(Nonce::from_slice(&nonce),Payload{msg:&expected,aad:b"wrong-book"}).is_err());
 }
}
