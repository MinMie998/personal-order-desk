use jni::{JNIEnv, objects::{JByteArray, JClass}, sys::jbyteArray};
use std::panic::{catch_unwind, AssertUnwindSafe};

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_personalorderdesk_nativepreview_sync_NativeSync_dispatchNative(
    mut env: JNIEnv, _class: JClass, input: JByteArray,
) -> jbyteArray {
    let outcome = catch_unwind(AssertUnwindSafe(|| -> Result<jbyteArray, String> {
        let len = env.get_array_length(&input).map_err(|_| "无法读取请求")? as usize;
        if len > desk_sync_core::bridge::MAX_REQUEST_BYTES { return Err("请求超过16MiB限制".into()); }
        let bytes = env.convert_byte_array(&input).map_err(|_| "无法读取请求")?;
        let result = desk_sync_core::bridge::dispatch(&bytes);
        Ok(env.byte_array_from_slice(&result).map_err(|_| "无法分配返回数据")?.into_raw())
    }));
    match outcome {
        Ok(Ok(result)) => result,
        other => {
            let message = match other { Ok(Err(e)) => e, _ => "同步核心内部错误，未应用数据".into() };
            // Preserve a pending JVM exception (e.g. OutOfMemoryError).
            if !env.exception_check().unwrap_or(true) { let _ = env.throw_new("java/lang/IllegalStateException", message); }
            std::ptr::null_mut()
        }
    }
}
