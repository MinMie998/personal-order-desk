package com.personalorderdesk.nativepreview

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.launch
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.format.DateTimeFormatter
import java.util.UUID
import java.io.File
import java.io.FileOutputStream
import java.io.ByteArrayOutputStream
import android.content.Context

@Composable fun PhoneDataTools(database:WorkbenchDatabase,recoveryMode:Boolean=false,onRecovered:()->Unit={},onNotice:(String)->Unit){
 val context=LocalContext.current;val scope=rememberCoroutineScope();var busy by remember{mutableStateOf(false)}
 var error by remember{mutableStateOf("")};var pending by remember{mutableStateOf<PhoneArchive?>(null)}
 var exportKind by rememberSaveable{mutableStateOf("orders")};var protection by remember{mutableStateOf("")}
 var localBackups by remember{mutableStateOf(emptyList<File>())}
 var showBackups by remember{mutableStateOf(false)}
 LaunchedEffect(protection,busy){if(!busy)localBackups=withContext(Dispatchers.IO){File(context.filesDir,"backups").listFiles()?.filter{it.extension=="json"}?.sortedByDescending{it.lastModified()}?.take(8)?:emptyList()}}
 fun workspaceId():String=database.dao().workspace()?.workspaceId?:error("工作台信息缺失，请保留本机数据")
 val backup=rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/json")){uri->if(uri!=null){busy=true;scope.launch{try{withContext(Dispatchers.IO){val text=database.archive(workspaceId());val bytes=text.toByteArray(Charsets.UTF_8);require(bytes.size<=50*1024*1024){"备份超过50MB，请使用后续大账本备份工具"};context.contentResolver.openOutputStream(uri,"wt").use{output->checkNotNull(output){"无法打开备份位置"};output.write(bytes);output.flush()}};onNotice("完整手机备份已导出")}catch(e:Exception){error=e.message?:"备份未完成"}finally{busy=false}}}}
 val export=rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("text/csv")){uri->if(uri!=null){busy=true;scope.launch{try{withContext(Dispatchers.IO){val csv=database.csv(exportKind);context.contentResolver.openOutputStream(uri,"wt").use{output->checkNotNull(output){"无法打开导出位置"};output.write(csv.toByteArray(Charsets.UTF_8));output.flush()}};onNotice("表格已导出")}catch(e:Exception){error=e.message?:"导出未完成"}finally{busy=false}}}}
 val open=rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()){uri->if(uri!=null){busy=true;scope.launch{try{pending=withContext(Dispatchers.IO){val input=checkNotNull(context.contentResolver.openInputStream(uri)){"无法读取备份文件"};val content=input.use{stream->val output=ByteArrayOutputStream();val buffer=ByteArray(8192);var count=stream.read(buffer);while(count>=0){if(count>0)output.write(buffer,0,count);require(output.size()<=50*1024*1024){"备份超过50MB，未修改当前数据"};count=stream.read(buffer)};output.toString("UTF-8")};readPhoneArchive(content)}}catch(e:Exception){error=e.message?:"备份文件不可用"}finally{busy=false}}}}
 Column(verticalArrangement=Arrangement.spacedBy(11.dp)){
  Text(if(recoveryMode)"从备份恢复"else"备份与导出",fontSize=17.sp,color=Color(0xff1d2939))
  Text("备份包含项目、收支和历史；CSV适合用Excel查看。文件由你选择保存位置。",fontSize=12.sp,color=Color(0xff7b8494))
  Row(horizontalArrangement=Arrangement.spacedBy(9.dp)){if(!recoveryMode)Button(enabled=!busy,onClick={error="";backup.launch("接单工作台-手机备份-${LocalDate.now()}.json")}){Text("导出完整备份",fontSize=12.sp)};OutlinedButton(enabled=!busy,onClick={error="";open.launch(arrayOf("application/json","application/octet-stream","text/plain"))}){Text("选择备份文件",fontSize=12.sp)}}
  if(!recoveryMode)Row(horizontalArrangement=Arrangement.spacedBy(9.dp)){TextButton(enabled=!busy,onClick={exportKind="orders";export.launch("接单订单-${LocalDate.now()}.csv")}){Text("导出订单表")};TextButton(enabled=!busy,onClick={exportKind="money";export.launch("接单流水-${LocalDate.now()}.csv")}){Text("导出收支表")}}
  if(localBackups.isNotEmpty()){
   TextButton(onClick={showBackups=!showBackups}){Text(if(showBackups)"收起保护备份"else"查看自动保护备份（${localBackups.size}）")}
   if(showBackups)localBackups.forEach{file->OutlinedButton(enabled=!busy,onClick={busy=true;error="";scope.launch{try{pending=withContext(Dispatchers.IO){require(file.length()<=50*1024*1024){"备份过大"};readPhoneArchive(file.readText())}}catch(e:Exception){error=e.message?:"保护备份无法读取"}finally{busy=false}}},modifier=Modifier.fillMaxWidth()){Text(file.name,fontSize=10.sp)}}
  }
  if(busy)Text("正在处理，请稍候…",fontSize=12.sp,color=Color(0xff3567df))
  if(protection.isNotEmpty())Text("恢复前保护备份：$protection",fontSize=11.sp,color=Color(0xff7b8494))
  if(error.isNotEmpty())Text(error,fontSize=12.sp,color=Color(0xffcf4145))
 }
 pending?.let{archive->AlertDialog(onDismissRequest={if(!busy)pending=null},title={Text("恢复这份手机备份？")},text={Column(verticalArrangement=Arrangement.spacedBy(10.dp)){Text("${archive.orders.size} 个项目 · ${archive.money.size} 笔收支");Text("当前手机账本将被替换。程序会先保存恢复前的保护备份，恢复失败时数据库事务回滚。");if(error.isNotEmpty())Text(error,color=Color(0xffcf4145))}},dismissButton={TextButton(enabled=!busy,onClick={pending=null}){Text("取消")}},confirmButton={TextButton(enabled=!busy,onClick={busy=true;error="";scope.launch{try{protection=withContext(Dispatchers.IO){val folder=File(context.filesDir,"backups");check(folder.isDirectory||folder.mkdirs()){"无法创建保护备份目录"};val file=File(folder,"恢复前-${LocalDateTime.now().format(DateTimeFormatter.ofPattern("yyyyMMdd-HHmmss"))}-${UUID.randomUUID()}.json");database.runInTransaction{val text=database.archive(workspaceId());FileOutputStream(file).use{stream->stream.write(text.toByteArray(Charsets.UTF_8));stream.fd.sync()};database.restoreArchive(archive,file)};file.absolutePath};pending=null;onRecovered();onNotice("已恢复备份，恢复前的数据已保护保存")}catch(e:Exception){error=e.message?:"恢复未完成，原数据保持不变"}finally{busy=false}}}){Text(if(busy)"恢复中…"else"备份当前数据并恢复",color=Color(0xffcf4145))}})}
}
