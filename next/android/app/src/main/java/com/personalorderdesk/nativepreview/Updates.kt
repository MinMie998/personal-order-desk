package com.personalorderdesk.nativepreview

import android.content.Intent
import android.net.Uri
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.net.URL
import javax.net.ssl.HttpsURLConnection

internal data class AndroidUpdate(val version:String,val code:Int,val url:String,val sha256:String,val bytes:Long)
internal fun parseAndroidUpdate(value:JSONObject,applicationId:String):AndroidUpdate {
    require(value.getString("format")=="personal-order-desk.update"&&value.getInt("version")==1&&value.getString("product")=="PersonalOrderDesk"){"更新来源不兼容"}
    val android=value.getJSONObject("android")
    require(android.getString("applicationId")==applicationId){"安装包不属于当前应用"}
    val url=android.getString("url");val hash=android.getString("sha256")
    require(url.startsWith("https://github.com/MinMie998/personal-order-desk/releases/download/")&&!url.any{it.isWhitespace()||it.isISOControl()}){"更新下载地址无效"}
    require(Regex("[a-fA-F0-9]{64}").matches(hash)&&android.getLong("bytes")>0&&android.getInt("versionCode")>0){"更新文件信息无效"}
    return AndroidUpdate(value.getString("appVersion"),android.getInt("versionCode"),url,hash,android.getLong("bytes"))
}
private fun fetchAndroidUpdate():AndroidUpdate? {
    val c=URL("https://api.github.com/repos/MinMie998/personal-order-desk/contents/updates/latest.json").openConnection() as HttpsURLConnection
    try {
        c.connectTimeout=10000;c.readTimeout=20000;c.instanceFollowRedirects=false
        c.setRequestProperty("Accept","application/vnd.github+json");c.setRequestProperty("User-Agent","PersonalOrderDesk-Updater")
        if(c.responseCode==404)return null
        check(c.responseCode in 200..299){"GitHub暂时无法提供更新，请稍后重试"}
        val out=java.io.ByteArrayOutputStream();c.inputStream.use{input->val buffer=ByteArray(4096);while(true){val n=input.read(buffer);if(n<0)break;check(out.size()+n<=131072){"更新信息过大"};out.write(buffer,0,n)}}
        val response=JSONObject(out.toString("UTF-8"));val encoded=response.getString("content").replace("\n","").replace("\r","")
        val manifest=JSONObject(java.util.Base64.getDecoder().decode(encoded).toString(Charsets.UTF_8))
        return parseAndroidUpdate(manifest,BuildConfig.APPLICATION_ID)
    }finally{c.disconnect()}
}
@Composable fun ProgramUpdates() {
    val context=LocalContext.current;val scope=rememberCoroutineScope()
    var busy by remember{mutableStateOf(false)};var update by remember{mutableStateOf<AndroidUpdate?>(null)};var message by remember{mutableStateOf("")}
    Column(verticalArrangement=Arrangement.spacedBy(9.dp)) {
        Text("版本与更新",fontSize=17.sp);Text("当前版本 ${BuildConfig.VERSION_NAME}",fontSize=12.sp,color=Color(0xff7b8494))
        OutlinedButton(enabled=!busy,onClick={busy=true;message="";scope.launch{try{update=withContext(Dispatchers.IO){fetchAndroidUpdate()};message=when{update==null->"发行信息尚未发布";update!!.code>BuildConfig.VERSION_CODE->"有新版本 ${update!!.version}，约 ${"%.2f".format(update!!.bytes/1048576.0)} MiB";else->"当前已是最新发布版本"}}catch(e:Exception){message="暂时无法检查更新，请稍后重试；本地记录不受影响"}finally{busy=false}}}){Text(if(busy)"正在检查…"else"检查 GitHub 更新")}
        if(message.isNotBlank())Text(message,fontSize=12.sp)
        update?.takeIf{it.code>BuildConfig.VERSION_CODE}?.let{item->Button(onClick={runCatching{context.startActivity(Intent(Intent.ACTION_VIEW,Uri.parse(item.url)))}.onFailure{message="无法打开浏览器，请在 GitHub Releases 下载新版"}}){Text("下载新版安装包")}}
        Text("从本项目 GitHub Releases 下载；覆盖安装保留数据。安装与签名由 Android 系统校验，不会自动清空账本。",fontSize=11.sp,color=Color(0xff7b8494))
    }
}
