package com.personalorderdesk.nativepreview.sync

import org.json.JSONObject

/** Shared Windows/Android algorithm boundary; no HTTP or database writes here. */
object NativeSync {
    private val loaded by lazy {
        try { System.loadLibrary("desk_sync_jni");true }
        catch(e:LinkageError){throw IllegalStateException("当前设备无法加载同步核心，请更新包含本机架构的安装包；本地记录保留")}
    }
    @JvmStatic private external fun dispatchNative(input: ByteArray): ByteArray

    fun execute(request: JSONObject): JSONObject {
        check(loaded)
        request.put("bridgeVersion", 1)
        val bytes = request.toString().toByteArray(Charsets.UTF_8)
        require(bytes.size <= 16 * 1024 * 1024) { "请求超过16MiB限制" }
        val output=try{dispatchNative(bytes)}catch(e:LinkageError){throw IllegalStateException("同步核心接口不兼容，请更新客户端")}
        val response = JSONObject(String(output, Charsets.UTF_8))
        check(response.getInt("bridgeVersion") == 1) { "同步调用版本不兼容" }
        check(response.getBoolean("ok")) { response.optString("error", "同步核心未完成操作") }
        return response.getJSONObject("result")
    }
}
