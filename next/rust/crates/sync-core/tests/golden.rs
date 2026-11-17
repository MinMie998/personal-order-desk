use serde_json::Value;
#[test]
fn shared_windows_android_vectors() {
    let vectors:Vec<Value>=serde_json::from_str(include_str!("../../../../contracts/sync-core-vectors.json")).unwrap();
    for v in vectors {
        let response:Value=serde_json::from_slice(&desk_sync_core::bridge::dispatch(&serde_json::to_vec(&v["request"]).unwrap())).unwrap();
        assert_eq!(response["ok"],true,"{}",v["name"]);
        assert_eq!(response["result"],v["expected"],"{}",v["name"]);
    }
}
