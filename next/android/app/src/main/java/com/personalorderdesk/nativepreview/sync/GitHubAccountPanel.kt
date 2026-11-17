package com.personalorderdesk.nativepreview.sync

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.delay
import org.json.JSONObject

@Composable internal fun GitHubAccountPanel() {
    val context=LocalContext.current;val scope=rememberCoroutineScope();val account=remember{PhoneGitHub.get(context)}
    val preferences=remember{context.getSharedPreferences("github-remote",Context.MODE_PRIVATE)}
    var state by remember{mutableStateOf<JSONObject?>(null)};var busy by remember{mutableStateOf(false)}
    var error by remember{mutableStateOf("")};var notice by remember{mutableStateOf("")};var pollAttempt by remember{mutableStateOf(0)}
    var repository by rememberSaveable{mutableStateOf(preferences.getString("repository","")?:"")}
    var checked by remember{mutableStateOf("")};var remove by remember{mutableStateOf(false)}
    fun act(block:()->JSONObject){if(busy)return;busy=true;error="";notice="";scope.launch{try{state=withContext(Dispatchers.IO){block()}}catch(e:Exception){error=e.message?:"操作未完成，本地记录保留"}finally{busy=false}}}
    LaunchedEffect(Unit){try{state=withContext(Dispatchers.IO){account.status()}}catch(e:Exception){error=e.message?:"登录状态读取失败"}}
    val pending=state?.optJSONObject("pending")
    LaunchedEffect(pending!=null,pollAttempt){
        if(pending!=null){
            try{while(true){delay(1000);val next=withContext(Dispatchers.IO){account.poll()};state=next;if(next.optJSONObject("pending")==null)break}}
            catch(e:kotlinx.coroutines.CancellationException){throw e}
            catch(e:Exception){error=e.message?:"授权检查暂时中断，请重试"}
        }
    }
    Column(verticalArrangement=Arrangement.spacedBy(10.dp)) {
        Text("GitHub 账号与远端仓库",fontSize=17.sp,fontWeight=FontWeight.SemiBold)
        Text("此处仅登录和检查仓库。账本下载与合并预览在下方单独操作，登录不会自动同步订单。",fontSize=12.sp,color=Color(0xff7b8494))
        val login=state?.optString("login","")?:""
        Text(if(login.isNotBlank())"已连接 GitHub · $login"else if(state?.optBoolean("saved")==true)"已保存登录，可检查连接"else"尚未登录",color=Color(0xff3567df))
        if(pending!=null){
            Text("在 GitHub 官方页面输入授权码，确认应用名称后授权。",fontSize=12.sp)
            Text(pending.optString("userCode"),fontSize=25.sp,fontWeight=FontWeight.SemiBold)
            Text("剩余约 ${pending.optLong("expiresIn")} 秒",fontSize=11.sp,color=Color(0xff7b8494))
            Row(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
                OutlinedButton(onClick={(context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager).setPrimaryClip(ClipData.newPlainText("GitHub 授权码",pending.optString("userCode")));notice="授权码已复制"}){Text("复制授权码")}
                Button(onClick={runCatching{context.startActivity(Intent(Intent.ACTION_VIEW,Uri.parse(GitHubAccount.LOGIN_URL)))}.onFailure{error="无法打开浏览器，请访问 ${GitHubAccount.LOGIN_URL}"}}){Text("打开授权页")}
            }
            Row{TextButton(enabled=!busy,onClick={act{account.cancel()}}){Text("取消本次授权")};TextButton(onClick={error="";pollAttempt++}){Text("继续检查授权")}}
        }else Button(enabled=!busy,onClick={act{account.begin()}}){Text(if(state?.optBoolean("saved")==true)"重新授权"else"连接 GitHub")}
        Row(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
            OutlinedButton(enabled=!busy&&state?.optBoolean("saved")==true,onClick={act{account.checkAccount()}}){Text("检查连接与续期")}
            TextButton(enabled=!busy,onClick={remove=true}){Text("移除本机登录")}
        }
        HorizontalDivider()
        OutlinedTextField(repository,{repository=it;checked=""},label={Text("私有数据仓库")},placeholder={Text("用户名/仓库名")},singleLine=true,modifier=Modifier.fillMaxWidth())
        OutlinedButton(enabled=!busy&&state?.optBoolean("saved")==true,onClick={
            val candidate=repository.trim()
            act{val result=account.checkRepository(candidate);check(preferences.edit().putString("repository",result.getString("repository")).commit()){"无法保存仓库设置"};account.status().put("checkedRepository",result.getString("repository"))}
        }){Text("检查并保存仓库")}
        LaunchedEffect(state?.optString("checkedRepository")){state?.optString("checkedRepository")?.takeIf{it.isNotBlank()}?.let{checked=it}}
        if(checked.isNotBlank())Text("私有仓库可访问：$checked",fontSize=12.sp,color=Color(0xff16845c))
        if(busy)Text("正在连接 GitHub…",fontSize=12.sp)
        if(error.isNotBlank())Text(error,color=Color(0xffcf4145),fontSize=12.sp)
        if(notice.isNotBlank())Text(notice,color=Color(0xff16845c),fontSize=12.sp)
        Text("登录凭据由 Android Keystore 保护，不包含在账本备份中。登录成功与账本同步成功分开显示。",fontSize=11.sp,color=Color(0xff7b8494))
    }
    if(remove)AlertDialog(onDismissRequest={remove=false},title={Text("移除手机上的 GitHub 登录？")},text={Text("仅清除本机凭据，不删除账本、远端仓库，也不撤销 GitHub 网站上的授权。")},confirmButton={TextButton(onClick={remove=false;checked="";act{account.remove()}}){Text("移除")}},dismissButton={TextButton(onClick={remove=false}){Text("取消")}})
}
