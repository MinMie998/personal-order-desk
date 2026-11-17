package com.personalorderdesk.nativepreview

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File

@Composable internal fun MissingLedgerRecovery(onRecovered:()->Unit) {
    val context=LocalContext.current;val scope=rememberCoroutineScope()
    var content by remember{mutableStateOf<String?>(null)};var preview by remember{mutableStateOf<PhoneArchive?>(null)}
    var busy by remember{mutableStateOf(false)};var error by remember{mutableStateOf("")}
    var files by remember{mutableStateOf(emptyList<File>())};var show by remember{mutableStateOf(false)}
    LaunchedEffect(Unit){files=withContext(Dispatchers.IO){File(context.filesDir,"backups").listFiles()?.filter{it.extension=="json"}?.sortedByDescending{it.lastModified()}?.take(8)?:emptyList()}}
    fun load(read:()->String){busy=true;error="";scope.launch{try{val pair=withContext(Dispatchers.IO){val text=read();text to readPhoneArchive(text)};content=pair.first;preview=pair.second}catch(e:Exception){error=e.message?:"备份不可用，本机数据未修改"}finally{busy=false}}}
    val picker=rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()){uri->if(uri!=null)load{readBoundedBackup(checkNotNull(context.contentResolver.openInputStream(uri)){"无法读取备份"})}}
    Column(verticalArrangement=Arrangement.spacedBy(8.dp)) {
        OutlinedButton(enabled=!busy,onClick={picker.launch(arrayOf("application/json","application/octet-stream","text/plain"))}){Text("选择备份恢复账本")}
        if(files.isNotEmpty()){
            TextButton(enabled=!busy,onClick={show=!show}){Text("本机保护备份（${files.size}）")}
            if(show)files.forEach{file->TextButton(enabled=!busy,onClick={load{file.inputStream().use{readBoundedBackup(it)}}}){Text(file.name,fontSize=10.sp)}}
        }
        if(busy)Text("正在处理…",fontSize=12.sp)
        if(error.isNotBlank())Text(error,color=Color(0xffcf4145),fontSize=12.sp)
    }
    preview?.let{a->AlertDialog(onDismissRequest={if(!busy){preview=null;content=null}},title={Text("从备份恢复手机账本？")},text={Column(verticalArrangement=Arrangement.spacedBy(8.dp)){
        Text("${a.orders.size} 个项目 · ${a.money.size} 笔收支")
        Text("先在临时数据库中恢复和校验，成功后才作为本机账本打开。来源备份会另存到应用私有目录。",fontSize=13.sp)
        if(error.isNotBlank())Text(error,color=Color(0xffcf4145))
    }},confirmButton={TextButton(enabled=!busy,onClick={busy=true;error="";scope.launch{try{withContext(Dispatchers.IO){LedgerRecovery.restoreMissing(context,checkNotNull(content))};preview=null;content=null;onRecovered()}catch(e:Exception){error=e.message?:"恢复失败，来源备份保留"}finally{busy=false}}}){Text("确认恢复")}},dismissButton={TextButton(enabled=!busy,onClick={preview=null;content=null}){Text("取消")}})}
}
