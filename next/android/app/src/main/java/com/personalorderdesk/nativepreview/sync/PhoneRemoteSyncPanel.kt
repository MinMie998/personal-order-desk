package com.personalorderdesk.nativepreview.sync

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.personalorderdesk.nativepreview.WorkbenchDatabase
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject

@Composable internal fun PhoneRemoteSyncPanel(db:WorkbenchDatabase,workspaceId:String) {
    val context=LocalContext.current;val scope=rememberCoroutineScope()
    val repository=remember(db){PhoneSyncRepository(context.applicationContext,db,GitHubSnapshotRemote(PhoneGitHub.get(context)))}
    var status by remember{mutableStateOf<JSONObject?>(null)};var busy by remember{mutableStateOf(false)}
    var error by remember{mutableStateOf("")};var notice by remember{mutableStateOf("")}
    var recovery by remember{mutableStateOf<SyncRecovery?>(null)};var plan by remember{mutableStateOf<PhoneSyncPlan?>(null)}
    var confirmed by remember{mutableStateOf(false)}
    fun act(block:()->(()->Unit)){if(busy)return;busy=true;error="";notice="";scope.launch{try{val update=withContext(Dispatchers.IO){block()};update();status=withContext(Dispatchers.IO){repository.status()}}catch(e:Exception){error=e.message?:"远端检查未完成，本机数据保留"}finally{busy=false}}}
    LaunchedEffect(workspaceId){try{status=withContext(Dispatchers.IO){repository.status()}}catch(e:Exception){error=e.message?:"同步设置读取失败，请重新导入恢复密钥"}}
    val picker=rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()){uri->if(uri!=null){
        busy=true;error="";scope.launch{try{recovery=withContext(Dispatchers.IO){
            val bytes=checkNotNull(context.contentResolver.openInputStream(uri)){"无法打开恢复密钥文件"}.use{stream->
                val out=java.io.ByteArrayOutputStream();val buffer=ByteArray(1024);while(true){val n=stream.read(buffer);if(n<0)break;require(out.size()+n<=16384){"恢复文件超过16KiB，请选择电脑导出的恢复密钥文件"};out.write(buffer,0,n)};out.toByteArray()
            }
            try{SyncRecovery.parse(bytes.toString(Charsets.UTF_8))}catch(e:org.json.JSONException){error("恢复密钥文件格式无效，请重新选择")}
        }}catch(e:Exception){error=e.message?:"恢复密钥未导入"}finally{busy=false}}
    }}
    Column(verticalArrangement=Arrangement.spacedBy(10.dp)) {
        Text("账本远端同步",fontSize=18.sp,fontWeight=FontWeight.SemiBold)
        Text("先检查，再确认。日常使用检查并合并；首次连接先下载副本。每次提交前先备份并复查双方版本。",fontSize=12.sp,color=Color(0xff7b8494))
        if(status?.optBoolean("configured")==true){
            Text("已连接账本：${status!!.optString("repository")}",fontSize=13.sp,color=Color(0xff3567df))
            val at=status!!.optString("confirmedAt")
            Text(if(at.isBlank())"尚未从远端下载确认"else"上次远端确认：${at.take(19).replace('T',' ')}",fontSize=12.sp)
            Text(if(status!!.optBoolean("localChanges"))"本地已保存 · 有待确认的改动"else"本机与上次确认版本一致，远端新改动需手动检查",fontSize=11.sp,color=Color(0xff7b8494))
            if(status!!.optBoolean("hasBaseline")){
                Button(enabled=!busy,onClick={plan=null;confirmed=false;act{val p=repository.prepare("merge");{plan=p;confirmed=false}}}){Text("检查并合并同步")}
                Text("合并双方改动；冲突需要逐项选择，不会自动用一端覆盖另一端。",fontSize=11.sp,color=Color(0xff7b8494))
                TextButton(enabled=!busy,onClick={plan=null;confirmed=false;act{val p=repository.prepare("download");{plan=p;confirmed=false}}}){Text("高级：用远端完整副本替换手机")}
            }else{
                Button(enabled=!busy,onClick={plan=null;confirmed=false;act{val p=repository.prepare("download");{plan=p;confirmed=false}}}){Text("首次连接：下载远端副本")}
                Text("首次下载后才有共同基准，之后即可双向合并。",fontSize=11.sp,color=Color(0xff7b8494))
            }
        }else Text("先在电脑导出恢复密钥，将文件传到手机并导入。GitHub 登录不能代替账本恢复密钥。",fontSize=12.sp,color=Color(0xff7b8494))
        Text("电脑导出位置：设置 → 远端同步 → 恢复密钥与新设备连接 → 导出恢复密钥（.deskkey）。",fontSize=11.sp,color=Color(0xff7b8494))
        Row(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
            OutlinedButton(enabled=!busy,onClick={picker.launch(arrayOf("*/*"))}){Text("导入电脑恢复密钥")}
            TextButton(enabled=!busy,onClick={act{{}}}){Text("刷新状态")}
        }
        if(busy)Text("正在处理，请稍候…",fontSize=12.sp)
        if(error.isNotBlank())Text(error,color=Color(0xffcf4145),fontSize=12.sp)
        if(notice.isNotBlank())Text(notice,color=Color(0xff16845c),fontSize=12.sp)
        val runs=status?.optJSONArray("runs")
        if(runs!=null&&runs.length()>0){
            var showRuns by remember{mutableStateOf(false)}
            TextButton(onClick={showRuns=!showRuns}){Text(if(showRuns)"收起同步记录"else"最近同步记录")}
            if(showRuns)for(i in 0 until runs.length()){
                val r=runs.getJSONObject(i);val label=when(r.optString("phase")){"confirmed"->"已确认";"remote-confirmed-local-pending"->"远端已更新，手机待确认";"reconciled"->"后续同步已处理";else->"需要重新检查"}
                Text("${r.optString("at").take(19).replace('T',' ')} · $label",fontSize=11.sp,color=Color(0xff7b8494))
            }
        }
        plan?.let{p->
            HorizontalDivider()
            Text(if(p.mode=="download")"本次下载预览"else"本次合并预览",fontSize=16.sp,fontWeight=FontWeight.SemiBold)
            Summary("当前手机账本",p.local);Summary("远端完整账本",p.remote.snapshot)
            val conflicts=p.result.getJSONArray("conflicts")
            if(p.mode=="merge"){
                if(conflicts.length()==0)Summary("准备提交的合并结果",p.result.getJSONObject("snapshot"))
                else Text("还有 ${conflicts.length()} 项冲突待选择。",color=Color(0xffb57516),fontSize=12.sp)
                duplicateMoneyWarnings(p).forEach{Text(it,fontSize=12.sp,color=Color(0xffb57516))}
                for(i in 0 until p.allConflicts.length()){
                    val conflict=p.allConflicts.getJSONObject(i);val id=conflict.getString("id")
                    ConflictChoice(conflict,p,p.choices.optString(id),!busy){side->
                        act{val choices=JSONObject(p.choices.toString());if(side.isBlank())choices.remove(id)else choices.put(id,side);val next=repository.resolve(p,choices);{plan=next}}
                    }
                }
                Button(enabled=!busy&&conflicts.length()==0,onClick={act{val result=repository.confirmMerge(p);{
                    plan=null;confirmed=false;notice=if(result.getBoolean("uploaded"))"双方改动已合并，远端与手机已确认"else"远端改动已合入手机，已确认一致"
                    if(!result.optBoolean("journalSaved",true))notice+="；本机回溯日志未保存，请保留保护备份"
                }}}){Text("备份并确认合并")}

            }
            Text("本机保护备份已生成。远端版本：${p.remote.head.take(10)}",fontSize=11.sp,color=Color(0xff7b8494))
            if(p.mode=="download"){
                Text("下载会完整替换手机当前账本，不把手机独有记录混入远端。本机旧数据可从保护备份恢复。",fontSize=12.sp)
                Row(verticalAlignment=androidx.compose.ui.Alignment.CenterVertically){Checkbox(confirmed,{confirmed=it},enabled=!busy);Text("我已核对，确认用远端副本替换手机账本",fontSize=12.sp,modifier=Modifier.weight(1f))}
                Button(enabled=confirmed&&!busy,onClick={act{repository.confirmDownload(p);{plan=null;confirmed=false;notice="手机副本已更新，本机旧账本和远端密文已保护保存"}}}){Text("备份并确认下载")}
            }
            TextButton(enabled=!busy,onClick={plan=null;confirmed=false}){Text("取消本次预览")}
        }
    }
    recovery?.let{r->AlertDialog(onDismissRequest={if(!busy)recovery=null},title={Text("连接这个远端账本？")},text={Column(verticalArrangement=Arrangement.spacedBy(8.dp)){
        Text(r.repository);Text("工作台：${r.workspaceId}",fontSize=11.sp)
        if(error.isNotBlank())Text(error,color=Color(0xffcf4145),fontSize=12.sp)
        Text("只保存加密连接信息，不会立即下载或替换项目。实际同步目标以这份恢复文件为准。",fontSize=12.sp)
    }},confirmButton={TextButton(enabled=!busy,onClick={act{repository.importRecovery(r);{recovery=null;plan=null;notice="恢复密钥已导入，请检查远端并预览下载"}}}){Text("确认连接")}},dismissButton={TextButton(enabled=!busy,onClick={recovery=null}){Text("取消")}})}
}

@Composable private fun Summary(label:String,snapshot:JSONObject) {
    val result by produceState<Result<JSONObject>?>(initialValue=null,snapshot){value=withContext(Dispatchers.Default){runCatching{PhoneSnapshotCodec.summary(snapshot)}}}
    Column(verticalArrangement=Arrangement.spacedBy(4.dp)) {
        Text(label,fontSize=13.sp,fontWeight=FontWeight.SemiBold)
        val v=result?.getOrNull()
        if(result==null)Text("正在整理摘要…",fontSize=12.sp)
        else if(v==null)Text("摘要无法计算，账本格式需要检查",fontSize=12.sp,color=Color(0xffcf4145))
        else {Text("${v.getInt("orders")} 个项目 · ${v.getInt("money")} 笔流水",fontSize=12.sp)
            Text("已收 ${v.getString("received")} · 利润 ${v.getString("profit")}",fontSize=12.sp,color=Color(0xff16845c))
            Text("待收 ${v.getString("remaining")}",fontSize=12.sp,color=Color(0xffcf4145))}
    }
}

@Composable private fun ConflictChoice(conflict:JSONObject,plan:PhoneSyncPlan,selected:String,enabled:Boolean,onSelect:(String)->Unit){
    val names=remember(conflict,plan){
        val map=mutableMapOf<String,String>();for(snapshot in listOf(plan.remote.snapshot,plan.local)){
            val tasks=snapshot.optJSONObject("orders")?.optJSONObject(conflict.optString("recordId"))?.optJSONArray("tasks")
            if(tasks!=null)for(i in 0 until tasks.length()){val t=tasks.getJSONObject(i);map[t.getString("id")]=t.getString("title")}
        };map
    }
    Surface(shape=androidx.compose.foundation.shape.RoundedCornerShape(9.dp),color=Color(0xfffffaf1),border=androidx.compose.foundation.BorderStroke(1.dp,Color(0xffedddbd))){
        Column(Modifier.padding(12.dp),verticalArrangement=Arrangement.spacedBy(8.dp)){
            Text(conflictTitle(conflict,plan),fontWeight=FontWeight.SemiBold,fontSize=13.sp)
            Text("共同基准："+describeConflictValue(conflict.opt("baseline"),conflict.getString("field"),names),fontSize=11.sp,color=Color(0xff7b8494))
            for((side,label) in listOf("local" to "手机记录","remote" to "远端记录")){
                Text(label+"：\n"+describeConflictValue(conflict.opt(side),conflict.getString("field"),names),fontSize=12.sp)
                OutlinedButton(enabled=enabled,onClick={onSelect(side)}){Text(if(selected==side)"✓ 已选择$label"else"选择$label")}
            }
            if(selected.isNotBlank())TextButton(enabled=enabled,onClick={onSelect("")}){Text("撤销本项选择")}
        }
    }
}
