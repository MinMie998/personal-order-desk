package com.personalorderdesk.nativepreview

import android.os.Bundle
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.saveable.Saver
import java.io.File
import androidx.compose.ui.Modifier
import androidx.compose.ui.Alignment
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import kotlinx.coroutines.launch
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.time.LocalDate
import java.time.temporal.ChronoUnit
import java.math.BigDecimal
import java.math.BigInteger

private val Blue=Color(0xff3567df)
private val Green=Color(0xff16845c)
private val Red=Color(0xffcf4145)
private val Grey=Color(0xff7b8494)
private val Pale=Color(0xfff5f6f8)

class MainActivity:ComponentActivity(){
 override fun onCreate(savedInstanceState:Bundle?){super.onCreate(savedInstanceState);setContent{
  MaterialTheme(colorScheme=lightColorScheme(primary=Blue,background=Pale,surface=Color.White),shapes=Shapes(extraSmall=RoundedCornerShape(4.dp),small=RoundedCornerShape(6.dp),medium=RoundedCornerShape(8.dp),large=RoundedCornerShape(12.dp),extraLarge=RoundedCornerShape(16.dp))){
   val context=LocalContext.current;val prefs=remember{context.getSharedPreferences("workbench-next",Context.MODE_PRIVATE)}
   var database by remember{mutableStateOf<WorkbenchDatabase?>(null)}
   var workspaceId by remember{mutableStateOf("")}
   var opening by remember{mutableStateOf(false)};var error by remember{mutableStateOf("")}
   var attempt by remember{mutableStateOf(0)};var createRequested by remember{mutableStateOf(false)}
   val exists=context.getDatabasePath("workbench-next.db").isFile
   val registered=prefs.getBoolean("initialized",false)
   LaunchedEffect(attempt,createRequested){
    if(!exists&&!createRequested)return@LaunchedEffect
    opening=true;error=""
    try{
     val opened=withContext(Dispatchers.IO){val db=WorkbenchDatabase.open(context);db.openHelper.writableDatabase;db to checkNotNull(db.dao().workspace()){"工作台信息缺失"}.workspaceId}
     check(prefs.edit().putBoolean("initialized",true).commit()){"无法保存账本登记"};workspaceId=opened.second;database=opened.first
    }catch(e:Exception){error=e.message?:"账本打开或升级失败。原数据保留，请勿卸载应用。"}finally{opening=false}
   }
   if(database!=null)Workbench(database!!,workspaceId)
   else Surface(Modifier.fillMaxSize(),color=Pale){Column(Modifier.verticalScroll(rememberScrollState()).padding(28.dp),verticalArrangement=Arrangement.Center){
    Text("接单工作台",fontSize=28.sp,fontWeight=FontWeight.SemiBold);Spacer(Modifier.height(16.dp))
    if(opening){CircularProgressIndicator();Spacer(Modifier.height(14.dp));Text("正在打开账本；旧版升级会先保存保护备份。",color=Grey)}
    else if(exists){Text("已有手机账本，打开失败时不会创建空库替代。",color=Grey);Button(onClick={attempt++}){Text("重新打开已有账本")}}
    else if(registered){Text("登记的账本文件暂时缺失。请保留应用数据与备份，不会自动创建空库。",color=Red)}
    else{Text("建立手机独立账本，离线记录订单、任务和收支。",color=Grey);Spacer(Modifier.height(24.dp));Button(onClick={createRequested=true}){Text("创建手机独立账本")}}
    if(!opening&&!exists)MissingLedgerRecovery{attempt++}
    if(error.isNotBlank()){Spacer(Modifier.height(14.dp));Text("打开未完成：$error",color=Red)}
   }}
  }
 }}
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable private fun Workbench(database:WorkbenchDatabase,initialWorkspaceId:String){
 val workspace by database.dao().observeWorkspace().collectAsState(initial=WorkspaceMeta(workspaceId=initialWorkspaceId))
 val workspaceId=workspace?.workspaceId?:initialWorkspaceId
 val orders by database.dao().observeOrders().collectAsState(initial=emptyList())
 val entries by database.dao().observeMoney().collectAsState(initial=emptyList())
 val context=LocalContext.current;val scope=rememberCoroutineScope();val snackbar=remember{SnackbarHostState()}
 var page by rememberSaveable{mutableStateOf("overview")};var query by rememberSaveable{mutableStateOf("")}
 val drafts=remember(context){ProjectDraftStore(File(context.filesDir,"project-drafts"))}
 val editorSaver=remember(workspaceId){Saver<WorkOrder?,String>(save={it?.id?:""},restore={id->runCatching{drafts.read(workspaceId,id)?.base}.getOrNull()})}
 var pendingDrafts by remember{mutableStateOf(emptyList<ProjectDraft>())}
 var editing by rememberSaveable(stateSaver=editorSaver){mutableStateOf<WorkOrder?>(null)};var payment by remember{mutableStateOf<Pair<WorkOrder,String>?>(null)}
 var trash by remember{mutableStateOf<WorkOrder?>(null)};var voiding by remember{mutableStateOf<WorkMoney?>(null)}
 var problem by remember{mutableStateOf("")};var busy by remember{mutableStateOf(false)};var copyPreview by remember{mutableStateOf<String?>(null)}
 LaunchedEffect(editing,workspaceId){if(editing==null)pendingDrafts=withContext(Dispatchers.IO){drafts.list(workspaceId)}}
 val uiPrefs=remember{context.getSharedPreferences("workbench-ui",Context.MODE_PRIVATE)}
 var physicalDays by remember{mutableStateOf(uiPrefs.getInt("physicalDays",4))};var digitalDays by remember{mutableStateOf(uiPrefs.getInt("digitalDays",1))}
 var physicalDraft by remember{mutableStateOf(physicalDays.toString())};var digitalDraft by remember{mutableStateOf(digitalDays.toString())}
 var moneyAnimation by remember{mutableStateOf(uiPrefs.getBoolean("moneyAnimation",true))}
 var cash by remember{mutableStateOf("")}
 fun perform(message:String,after:()->Unit={},block:()->Unit){if(busy)return;busy=true;problem="";scope.launch{try{withContext(Dispatchers.IO){block()};after();scope.launch{snackbar.showSnackbar(message)}}catch(e:Exception){problem=if(e is java.time.format.DateTimeParseException)"请使用年-月-日格式填写日期"else e.message?:"操作未完成，请检查填写内容"}finally{busy=false}}}
 fun copy(value:String){(context.getSystemService(Context.CLIPBOARD_SERVICE)as ClipboardManager).setPrimaryClip(ClipData.newPlainText("接单工作台",value));scope.launch{snackbar.showSnackbar("已复制")}}
 val live=orders.filter{it.deletedAt==null}
 val pending=live.filter{it.status!in listOf("done","cancelled")}.sortedBy{it.dueDate?:"9999"}
 val unpaid=live.filter{it.status!="cancelled"&&orderBalance(it,entries).remaining>BigInteger.ZERO}
 val received=live.fold(BigInteger.ZERO){a,o->a+orderBalance(o,entries).received}
 val costs=live.fold(BigInteger.ZERO){a,o->a+orderBalance(o,entries).cost}
 fun prepareCopy(){val result=linkedMapOf<String,BigInteger>();unpaid.forEach{result[it.number]=(result[it.number]?:BigInteger.ZERO)+orderBalance(it,entries).remaining};copyPreview=result.entries.joinToString("\n"){"${it.key} ${BigDecimal(it.value,2).toPlainString()}元"}}
 BackHandler(page!="overview"&&editing==null&&payment==null){page="overview";query=""}
 LaunchedEffect(cash){if(cash.isNotEmpty()){kotlinx.coroutines.delay(2000);cash=""}}
 val pages=linkedMapOf("overview" to "概览","orders" to "项目","money" to "收支","settings" to "我的")
 CompositionLocalProvider(LocalWorkPreferences provides (physicalDays to digitalDays)){
 Scaffold(containerColor=Pale,snackbarHost={SnackbarHost(snackbar)},topBar={Surface(color=Pale){Row(Modifier.fillMaxWidth().statusBarsPadding().padding(horizontal=20.dp,vertical=17.dp),verticalAlignment=Alignment.CenterVertically){
 Column(Modifier.weight(1f)){Text(pages[page]?:if(page=="shipping")"物流"else"回收站",fontSize=27.sp,fontWeight=FontWeight.SemiBold);Spacer(Modifier.height(5.dp));Text("手机本地账本 · 离线可用",fontSize=11.sp,color=Grey)}
 if(page!="settings")Button(onClick={editing=WorkOrder()},shape=RoundedCornerShape(7.dp),contentPadding=PaddingValues(horizontal=14.dp,vertical=8.dp)){Text("＋ 新增",fontSize=13.sp)}
 } }},bottomBar={WorkbenchNavigation(pages,page){page=it;query=""}}){padding->
  Box(Modifier.fillMaxSize().padding(padding)){
   Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal=18.dp),verticalArrangement=Arrangement.spacedBy(14.dp)){
    if(problem.isNotEmpty())Card(colors=CardDefaults.cardColors(containerColor=Color(0xffffefed))){Row(Modifier.padding(14.dp),verticalAlignment=Alignment.CenterVertically){Text(problem,Modifier.weight(1f),color=Red);TextButton(onClick={problem=""}){Text("关闭")}}}
    if(page=="overview"){
     pendingDrafts.take(3).forEach{draft->Panel{
      Text("未保存的项目草稿",fontSize=11.sp,color=Blue);Text(draft.order.title.ifBlank{"未命名项目"},fontWeight=FontWeight.SemiBold)
      Row{TextButton(onClick={val current=orders.find{it.id==draft.base.id};if(draft.base.revision==0L&&current==null)editing=draft.base else if(current!=null&&current.deletedAt==null)editing=current else problem="原项目不存在或已在回收站，草稿仍保留"}){Text("继续编辑")}
      TextButton(onClick={perform("草稿已丢弃",{pendingDrafts=pendingDrafts.filter{it.base.id!=draft.base.id}}){drafts.remove(workspaceId,draft.base.id)}}){Text("丢弃草稿")}}
     }}
     Row(horizontalArrangement=Arrangement.spacedBy(12.dp)){Metric("全部订单","${live.size} 单",Modifier.weight(1f));Metric("进行中","${pending.size} 单",Modifier.weight(1f))}
     Row(horizontalArrangement=Arrangement.spacedBy(12.dp)){Metric("净收款",yuan(received),Modifier.weight(1f),Green);Metric("实际利润",yuan(received-costs),Modifier.weight(1f),if(received>=costs)Green else Red)}
     Metric("待收款",yuan(unpaid.fold(BigInteger.ZERO){a,o->a+orderBalance(o,entries).remaining}),Modifier.fillMaxWidth(),Red)
     val urgentTasks=pending.flatMap{o->leafTasks(o.tasks).filter{taskDueLabel(it) in listOf("已逾期","今天截止","需要跟进","即将到期")}.map{o to it}}.sortedWith(compareBy({it.second.dueDate?:it.second.followUpDate?:"9999"},{it.first.number}))
     if(urgentTasks.isNotEmpty()){
      SectionTitle("任务提醒","${urgentTasks.size} 项需要关注")
      urgentTasks.take(4).forEach{(o,t)->Surface(color=Color(0xfffff5ed),shape=RoundedCornerShape(9.dp),modifier=Modifier.fillMaxWidth().clickable{editing=o}){Column(Modifier.padding(14.dp),verticalArrangement=Arrangement.spacedBy(5.dp)){Text("${o.number} · ${o.title}",fontSize=11.sp,color=Grey);Text(t.title,fontSize=14.sp,fontWeight=FontWeight.SemiBold);Text(taskDueLabel(t),fontSize=12.sp,color=if(taskDueLabel(t) in listOf("已逾期","今天截止"))Red else Color(0xffb57516))}}}
     }
     SectionTitle("接下来要做","按截止日期排列")
     if(pending.isEmpty())EmptyNote("手上的工作已完成","有新项目时，点击右上角新增。")
     pending.take(5).forEach{o->OrderCard(o,entries,onOpen={editing=o},onMoney={payment=o to "receipt"},onDone={perform("已标记完成"){database.save(o.copy(status="done",completedDate=LocalDate.now().toString()))}})}
     Row(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.SpaceBetween,verticalAlignment=Alignment.CenterVertically){SectionTitle("待收款");TextButton(enabled=unpaid.isNotEmpty(),onClick={prepareCopy()}){Text("复制清单")}}
     unpaid.take(5).forEach{o->MoneyCard(o,entries,{editing=o},{payment=o to "receipt"},{payment=o to "cost"})}
    }
    if(page in listOf("orders","shipping","trash")){
     OutlinedTextField(value=query,onValueChange={query=it},label={Text("搜索编号、项目或单号")},singleLine=true,modifier=Modifier.fillMaxWidth())
     val list=(if(page=="trash")orders.filter{it.deletedAt!=null}else live).filter{(page!="shipping"||it.kind=="physical")&&(query.isBlank()||"${it.number} ${it.title} ${it.trackingNumber}".contains(query,true))}
     if(list.isEmpty())EmptyNote(if(page=="trash")"回收站是空的"else"暂无匹配项目","从新增订单开始记录。")
     list.forEach{o->if(page=="trash")Panel{Text(o.number,color=Grey,fontSize=12.sp);Text(o.title,fontWeight=FontWeight.SemiBold);TextButton(enabled=!busy,onClick={perform("项目已恢复"){database.trash(o,true)}}){Text("恢复项目")}}
      else if(page=="shipping")Panel{Text(o.number,color=Grey,fontSize=12.sp);Text(o.title,fontWeight=FontWeight.SemiBold);Text(if(o.deliveredDate!=null)"已签收"else if(o.shippedDate!=null)"已寄出"else"待寄出",color=if(o.deliveredDate!=null)Green else Blue);Text("${o.carrier.ifBlank{"快递待补充"}} · ${o.trackingNumber.ifBlank{"单号待补充"}}",fontSize=13.sp);Text("寄出：${o.shippedDate?:"—"}   收货：${o.deliveredDate?:"—"}",fontSize=12.sp,color=Grey);Row{TextButton(onClick={editing=o}){Text("编辑物流")};if(o.trackingNumber.isNotBlank())TextButton(onClick={copy(o.trackingNumber)}){Text("复制单号")}}}
      else OrderCard(o,entries,{editing=o},{payment=o to "receipt"},{perform("已标记完成"){database.save(o.copy(status="done",completedDate=LocalDate.now().toString()))}})
     }
    }
    if(page=="money"){
     Row(horizontalArrangement=Arrangement.spacedBy(12.dp)){Metric("净收款",yuan(received),Modifier.weight(1f),Green);Metric("实际利润",yuan(received-costs),Modifier.weight(1f),if(received>=costs)Green else Red)}
     Metric("实际支出",yuan(costs),Modifier.fillMaxWidth())
     Button(enabled=unpaid.isNotEmpty(),onClick={prepareCopy()},modifier=Modifier.fillMaxWidth()){Text("复制未结款清单")}
     SectionTitle("按编号收支")
     live.forEach{o->MoneyCard(o,entries,{editing=o},{payment=o to "receipt"},{payment=o to "cost"})}
     SectionTitle("最近流水")
     entries.filter{e->live.any{it.id==e.orderId}}.take(50).forEach{e->Panel{
      Text(orders.find{it.id==e.orderId}?.number?:"",fontSize=12.sp,color=Grey)
      Row(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.SpaceBetween){Text("${moneyKinds[e.kind]} · ${e.method}");Text((if(e.kind in listOf("receipt","cost-refund"))"+"else"−")+yuan(e.amountCents),color=if(e.kind in listOf("receipt","cost-refund"))Green else MaterialTheme.colorScheme.onSurface)}
      Text("${e.date?:"日期未记"} · ${e.voidReason?:e.note}",fontSize=12.sp,color=Grey)
      if(e.voidedAt==null)TextButton(onClick={voiding=e}){Text("作废这笔记录")}else Text("已作废，原记录保留",fontSize=12.sp,color=Grey)
     }}
    }
    if(page=="settings"){
     Panel{SectionTitle("手机独立账本");Text("数据完整保存在手机，电脑关闭时也能继续使用。远端连接、下载与合并预览可在下方管理。",fontSize=13.sp,color=Grey);Text("${live.size} 个项目 · ${entries.size} 笔流水",fontSize=13.sp);Row{TextButton(onClick={page="shipping"}){Text("物流履约")};TextButton(onClick={page="trash"}){Text("回收站")}}}
     Panel{PhoneDataTools(database){message->scope.launch{snackbar.showSnackbar(message)}}}
     Panel{com.personalorderdesk.nativepreview.sync.GitHubAccountPanel()}
     Panel{com.personalorderdesk.nativepreview.sync.PhoneRemoteSyncPanel(database,workspaceId)}
     Panel{ProgramUpdates()}
     Panel{SectionTitle("提醒与显示");Field("实物提前提醒（天）",physicalDraft,{physicalDraft=it});Field("其他类型提前提醒（天）",digitalDraft,{digitalDraft=it})
 Row(Modifier.fillMaxWidth(),verticalAlignment=Alignment.CenterVertically){Text("收款显示加钱效果",Modifier.weight(1f),fontSize=13.sp);Switch(checked=moneyAnimation,onCheckedChange={moneyAnimation=it;uiPrefs.edit().putBoolean("moneyAnimation",it).apply()})}
 Button(onClick={val p=physicalDraft.toIntOrNull();val d=digitalDraft.toIntOrNull();if(p==null||d==null||p !in 0..90||d !in 0..90)problem="提醒天数应为0至90天"else{physicalDays=p;digitalDays=d;uiPrefs.edit().putInt("physicalDays",p).putInt("digitalDays",d).apply();scope.launch{snackbar.showSnackbar("提醒设置已保存")}}}){Text("保存提醒设置")}
 Text("非实物默认接单次日截止；已完成项目不显示逾期。",fontSize=11.sp,color=Grey)}
    }
    Spacer(Modifier.height(16.dp))
   }
   if(cash.isNotEmpty())Surface(Modifier.align(Alignment.Center),shape=MaterialTheme.shapes.large,color=Color(0xffeaf8ef),shadowElevation=16.dp){Column(Modifier.padding(30.dp),horizontalAlignment=Alignment.CenterHorizontally){Text(cash,fontSize=32.sp,color=Green,fontWeight=FontWeight.SemiBold);Text("收款成功",color=Green)}}
  }
 }
 editing?.let{o->OrderEditor(o,live,entries,busy,problem,workspaceId,drafts,onDismiss={editing=null;problem=""},onSave={saved,finished->perform("项目已保存",{finished();editing=null}){database.save(saved)}},onTrash={trash=o},onMoney={kind->payment=o to kind},onCopy={copy(it)},onAdditional={editing=WorkOrder(number=o.number,kind=o.kind,parentId=o.id,dueDate=if(o.kind=="physical")null else LocalDate.now().plusDays(1).toString())})}
 payment?.let{(o,kind)->PaymentEditor(o,kind,entries,busy,problem,onDismiss={payment=null;problem=""},onSave={entry->perform("${moneyKinds[entry.kind]}已记录",{payment=null;if(entry.kind=="receipt"&&moneyAnimation)cash="+ ${yuan(entry.amountCents)}"}){database.record(entry)}})}
 trash?.let{o->AlertDialog(onDismissRequest={if(!busy)trash=null},title={Text("移入回收站？")},text={Text("${o.number} · ${o.title}\n项目退出日常统计，收支和历史保留，可随时恢复。")},confirmButton={TextButton(enabled=!busy,onClick={perform("已移入回收站",{trash=null;editing=null}){database.trash(o)}}){Text("移入回收站",color=Red)}},dismissButton={TextButton(onClick={trash=null}){Text("取消")}})}
 voiding?.let{e->var reason by remember(e.id){mutableStateOf("")};AlertDialog(onDismissRequest={if(!busy)voiding=null},title={Text("作废这笔记录？")},text={Column{Text("${moneyKinds[e.kind]} ${yuan(e.amountCents)}");OutlinedTextField(reason,{reason=it},label={Text("作废原因")});if(problem.isNotEmpty())Text(problem,color=Red)}},confirmButton={TextButton(enabled=!busy,onClick={perform("流水已作废",{voiding=null}){database.void(e,reason)}}){Text("确认作废",color=Red)}},dismissButton={TextButton(onClick={voiding=null}){Text("取消")}})}
 copyPreview?.let{value->AlertDialog(onDismissRequest={copyPreview=null},title={Text("未结款清单")},text={Column{Text("同编号合计，一行一单。",color=Grey,fontSize=12.sp);OutlinedTextField(value,{copyPreview=it},modifier=Modifier.fillMaxWidth().heightIn(max=320.dp))}},confirmButton={TextButton(onClick={copy(copyPreview?:"")}){Text("复制清单")}},dismissButton={TextButton(onClick={copyPreview=null}){Text("关闭")}})}
 }
}

@Composable private fun NavGlyph(name:String,color:Color=Grey){androidx.compose.foundation.Canvas(Modifier.size(20.dp)){
 val unit=size.width/24;val stroke=androidx.compose.ui.graphics.drawscope.Stroke(width=1.7f*unit)
 fun box(x:Float,y:Float,w:Float,h:Float)=drawRect(color,androidx.compose.ui.geometry.Offset(x*unit,y*unit),androidx.compose.ui.geometry.Size(w*unit,h*unit),style=stroke)
 when(name){"overview"->{box(3f,3f,7f,7f);box(14f,3f,7f,7f);box(3f,14f,7f,7f);box(14f,14f,7f,7f)};"money"->{box(3f,5f,18f,15f);box(15f,10f,6f,6f)};"shipping"->{box(3f,6f,18f,15f);drawLine(color,androidx.compose.ui.geometry.Offset(12*unit,6*unit),androidx.compose.ui.geometry.Offset(12*unit,21*unit),1.7f*unit)};"settings"->{drawCircle(color,8*unit,style=stroke);drawCircle(color,3*unit,style=stroke)};else->{box(5f,4f,14f,18f);box(8f,2f,8f,4f);drawLine(color,androidx.compose.ui.geometry.Offset(8*unit,11*unit),androidx.compose.ui.geometry.Offset(16*unit,11*unit),1.7f*unit)}}
}}
@Composable private fun Panel(content:@Composable ColumnScope.()->Unit){Surface(color=Color.White,shape=RoundedCornerShape(9.dp),border=BorderStroke(1.dp,Color(0xffe8ecf1)),modifier=Modifier.fillMaxWidth()){Column(Modifier.padding(16.dp),verticalArrangement=Arrangement.spacedBy(9.dp),content=content)}}
@Composable private fun Metric(label:String,value:String,modifier:Modifier=Modifier,color:Color=MaterialTheme.colorScheme.onSurface){Surface(modifier,color=Color.White,shape=RoundedCornerShape(8.dp),border=BorderStroke(1.dp,Color(0xffe8ecf1))){Column(Modifier.padding(16.dp),verticalArrangement=Arrangement.spacedBy(8.dp)){Text(label,fontSize=11.sp,color=Grey);Text(value,fontSize=24.sp,fontWeight=FontWeight.SemiBold,color=color)}}}
@Composable private fun SectionTitle(title:String,subtitle:String=""){Column{Text(title,fontSize=17.sp,fontWeight=FontWeight.SemiBold);if(subtitle.isNotEmpty())Text(subtitle,fontSize=11.sp,color=Grey)}}
@Composable private fun EmptyNote(title:String,subtitle:String){Panel{Text(title,fontWeight=FontWeight.Medium);Text(subtitle,color=Grey,fontSize=13.sp)}}
private fun urgency(order:WorkOrder,physicalDays:Int=4,digitalDays:Int=1):Pair<String,Color>{if(order.status=="done")return "已完成" to Green;if(order.status=="cancelled")return "已取消" to Grey;val due=order.dueDate?:return "待补截止日期" to Color(0xffba791e);val days=ChronoUnit.DAYS.between(LocalDate.now(),LocalDate.parse(due));return when{days<0->"逾期 ${-days} 天" to Red;days==0L->"今天截止" to Red;days<=if(order.kind=="physical")physicalDays else digitalDays->"还剩 $days 天" to Color(0xffba791e);else->"$days 天后截止" to Blue}}
@Composable private fun OrderCard(order:WorkOrder,entries:List<WorkMoney>,onOpen:()->Unit,onMoney:()->Unit,onDone:()->Unit){
 val b=orderBalance(order,entries);val prefs=LocalWorkPreferences.current;val u=urgency(order,prefs.first,prefs.second)
 Surface(color=Color.White,shape=RoundedCornerShape(5.dp),modifier=Modifier.fillMaxWidth().clickable(onClick=onOpen)){
  Column(Modifier.padding(horizontal=15.dp,vertical=13.dp),verticalArrangement=Arrangement.spacedBy(6.dp)){
   Row(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.SpaceBetween){Text(order.number,fontSize=11.sp,color=Grey);Text(u.first,fontSize=11.sp,color=u.second)}
   Text(order.title,fontSize=15.sp,fontWeight=FontWeight.SemiBold)
   Text("${workKinds[order.kind]} · ${workStatuses[order.status]}",fontSize=11.sp,color=Grey)
   if(order.tasks.isNotEmpty()){
    val leaves=leafTasks(order.tasks);val next=leaves.firstOrNull{it.status!="done"}
    Text("任务 ${leaves.count{it.status=="done"}}/${leaves.size} 完成${next?.let{" · ${it.title}"}?:""}",fontSize=12.sp,color=Color(0xff536174))
   }else{
    if(order.progress.isNotBlank())Text(order.progress,fontSize=12.sp,color=Color(0xff536174))
    if(order.nextStep.isNotBlank())Text("下一步：${order.nextStep}",fontSize=12.sp,color=Grey)
   }
   HorizontalDivider(color=Color(0xfff0f2f5),modifier=Modifier.padding(top=4.dp))
   Row(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.SpaceBetween,verticalAlignment=Alignment.CenterVertically){
    Text(if(b.remaining>BigInteger.ZERO)"待收 ${yuan(b.remaining)}"else"已收齐",fontSize=12.sp,color=if(b.remaining>BigInteger.ZERO)Red else Green)
    Row{if(b.remaining>BigInteger.ZERO&&order.status!="cancelled")TextButton(onClick=onMoney,contentPadding=PaddingValues(horizontal=8.dp,vertical=0.dp)){Text("收款",fontSize=12.sp)}
     if(order.status !in listOf("done","cancelled"))TextButton(onClick=onDone,contentPadding=PaddingValues(horizontal=8.dp,vertical=0.dp)){Text("完成",fontSize=12.sp)}
    }
   }
  }
 }
}
@Composable private fun MoneyCard(order:WorkOrder,entries:List<WorkMoney>,onOpen:()->Unit,onReceipt:()->Unit,onCost:()->Unit){val b=orderBalance(order,entries)
 Surface(color=Color.White,shape=RoundedCornerShape(5.dp),modifier=Modifier.fillMaxWidth()){Column(Modifier.padding(15.dp),verticalArrangement=Arrangement.spacedBy(8.dp)){
  Row(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.SpaceBetween){Text(order.number,fontSize=11.sp,color=Grey);Text("待收 ${yuan(b.remaining)}",fontSize=12.sp,color=if(b.remaining>BigInteger.ZERO)Red else Grey)}
  Text(order.title,fontSize=14.sp,fontWeight=FontWeight.SemiBold,modifier=Modifier.clickable(onClick=onOpen))
  Row(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.SpaceBetween){Text("已收 ${yuan(b.received)}",fontSize=12.sp,color=Green);Text("利润 ${yuan(b.profit)}",fontSize=12.sp,color=if(b.profit>=BigInteger.ZERO)Green else Red)}
  Row(Modifier.fillMaxWidth(),verticalAlignment=Alignment.CenterVertically){Text("支出 ${yuan(b.cost)}",Modifier.weight(1f),fontSize=11.sp,color=Grey);TextButton(onClick=onReceipt,contentPadding=PaddingValues(horizontal=8.dp,vertical=0.dp)){Text("记收款",fontSize=12.sp)};TextButton(onClick=onCost,contentPadding=PaddingValues(horizontal=8.dp,vertical=0.dp)){Text("记支出",fontSize=12.sp)}}
 }}
}
@Composable private fun Choice(label:String,value:String,options:Map<String,String>,onChange:(String)->Unit){
 var expanded by remember{mutableStateOf(false)}
 Column{
  Text(label,fontSize=12.sp,color=Grey)
  Box{
   OutlinedButton(onClick={expanded=true},modifier=Modifier.fillMaxWidth()){Text(options[value]?:value,Modifier.weight(1f));Text("⌄")}
   DropdownMenu(expanded,onDismissRequest={expanded=false}){
    options.forEach{(key,text)->DropdownMenuItem(text={Text(text)},onClick={expanded=false;onChange(key)})}
   }
  }
 }
}
@Composable private fun Field(label:String,value:String,onChange:(String)->Unit,decimal:Boolean=false){
 val context=LocalContext.current
 Column(verticalArrangement=Arrangement.spacedBy(5.dp)){
  Text(label,fontSize=11.sp,color=Grey)
  Row(verticalAlignment=Alignment.CenterVertically){
   OutlinedTextField(value,onValueChange=onChange,modifier=Modifier.weight(1f),shape=RoundedCornerShape(7.dp),keyboardOptions=KeyboardOptions(keyboardType=if(decimal)KeyboardType.Decimal else KeyboardType.Text),singleLine=true,textStyle=LocalTextStyle.current.copy(fontSize=14.sp))
   if(label.contains("日期"))TextButton(onClick={val day=runCatching{LocalDate.parse(value)}.getOrDefault(LocalDate.now());android.app.DatePickerDialog(context,{_,y,m,d->onChange(LocalDate.of(y,m+1,d).toString())},day.year,day.monthValue-1,day.dayOfMonth).show()}){Text("日历",fontSize=12.sp)}
  }
 }
}

@Composable private fun OrderEditor(original:WorkOrder,all:List<WorkOrder>,allEntries:List<WorkMoney>,busy:Boolean,problem:String,workspaceId:String,drafts:ProjectDraftStore,onDismiss:()->Unit,onSave:(WorkOrder,()->Unit)->Unit,onTrash:()->Unit,onMoney:(String)->Unit,onCopy:(String)->Unit,onAdditional:()->Unit){
 val context=LocalContext.current
 val restored=remember(original.id){runCatching{drafts.read(workspaceId,original.id)}}
 val stored=restored.getOrNull()
 val currentRevision=all.firstOrNull{it.id==original.id}?.revision
 val stale=stored!=null&&(stored.base.revision!=original.revision||currentRevision!=null&&stored.base.revision!=currentRevision)
 val session=remember(original.id){ProjectDraftSession(drafts,workspaceId,original.id)}
 var draft by remember(original.id){mutableStateOf(stored?.order?:original)}
 var quote by remember(original.id){mutableStateOf(stored?.quote?:BigDecimal.valueOf(original.quoteCents,2).toPlainString())}
 var tab by remember(original.id){mutableStateOf(stored?.tab?:if(original.revision>0L)"tasks"else"basic")}
 var localError by remember(original.id){mutableStateOf(restored.exceptionOrNull()?.message?:"")}
 val draftBlocked=stale||restored.isFailure
 if(!draftBlocked)KeepProjectDraft(session,ProjectDraft(workspaceId,original,draft,quote,tab)){localError=it}
 fun finished(){runCatching{session.finish()}.onFailure{android.widget.Toast.makeText(context,"项目已保存，草稿清理失败，旧草稿不会自动覆盖项目",android.widget.Toast.LENGTH_LONG).show()}}
 fun discardAndClose(){runCatching{session.finish()}.onSuccess{onDismiss()}.onFailure{localError=it.message?:"无法丢弃草稿"}}
 var records by remember(original.id){mutableStateOf(emptyList<WorkHistory>())}
 var discard by remember{mutableStateOf(false)}
 val dirty=draft!=original || quote!=BigDecimal.valueOf(original.quoteCents,2).toPlainString()
 fun close(){if(!busy){if(dirty||stored!=null||draftBlocked)discard=true else discardAndClose()}}
 fun money(kind:String){if(dirty)localError="请先保存项目修改，再记录收支"else onMoney(kind)}

 var dueAutomatic by remember(original.id){mutableStateOf(original.revision==0L)}
 LaunchedEffect(original.id,original.revision,allEntries.size){if(original.revision>0)records=withContext(Dispatchers.IO){WorkbenchDatabase.open(context).dao().history(original.id)}}

 Dialog(onDismissRequest={close()},properties=DialogProperties(usePlatformDefaultWidth=false,decorFitsSystemWindows=false)){
  Surface(Modifier.fillMaxSize().systemBarsPadding().imePadding(),color=Pale,shape=RoundedCornerShape(0.dp)){Column{
   Row(Modifier.fillMaxWidth().padding(18.dp),horizontalArrangement=Arrangement.SpaceBetween,verticalAlignment=Alignment.CenterVertically){SectionTitle(if(original.revision==0L)"新增订单"else"项目详情",original.number);TextButton(enabled=!busy,onClick={close()}){Text("返回")}}
   if(original.revision>0L){val balance=orderBalance(original,allEntries);Row(Modifier.fillMaxWidth().background(Color.White).padding(horizontal=18.dp,vertical=12.dp),horizontalArrangement=Arrangement.SpaceBetween){Text("已收 ${yuan(balance.received)}",color=Green,fontSize=12.sp);Text("利润 ${yuan(balance.profit)}",color=if(balance.profit>=BigInteger.ZERO)Green else Red,fontSize=12.sp);Text("待收 ${yuan(balance.remaining)}",color=if(balance.remaining>BigInteger.ZERO)Red else Grey,fontSize=12.sp)}}
   Row(Modifier.horizontalScroll(rememberScrollState()).padding(horizontal=12.dp)){
    listOf("tasks" to "任务流程","basic" to "基本信息","shipping" to "物流","records" to "收支历史").filter{(it.first!="shipping"||draft.kind=="physical")&&(it.first!="records"||original.revision>0L)}.forEach{(key,label)->TextButton(onClick={tab=key}){Text(label,color=if(tab==key)Blue else Grey,fontWeight=if(tab==key)FontWeight.SemiBold else FontWeight.Normal)}}
    if(original.revision>0L){TextButton(onClick={money("receipt")}){Text("收款")};TextButton(onClick={money("cost")}){Text("支出")}}
   }
   Column(Modifier.weight(1f,false).verticalScroll(rememberScrollState()).padding(horizontal=18.dp),verticalArrangement=Arrangement.spacedBy(12.dp)){
    if(problem.isNotEmpty()||localError.isNotEmpty())Text(localError.ifEmpty{problem},color=Red,fontSize=13.sp)
    if(draftBlocked)Text("原项目已更新或草稿不兼容。已阻止保存，请保留内容或丢弃草稿后重新打开。",color=Red,fontSize=12.sp)
    Text("草稿自动暂存于本机；只有底部保存项目才会更新账本。",color=Grey,fontSize=11.sp)
    if(tab=="tasks"){
     TaskFlowEditor(draft.kind,draft.tasks){draft=draft.copy(tasks=it)}
     val leaves=leafTasks(draft.tasks)
     if(leaves.isNotEmpty()&&leaves.all{it.status=="done"}&&draft.status!="done")OutlinedButton(onClick={draft=draft.copy(status="done",completedDate=LocalDate.now().toString())}){Text("任务已完成，确认项目完成")}
    }else if(tab=="basic"){
     Field("订单编号 *",draft.number,{draft=draft.copy(number=it)});Field("项目名称 *",draft.title,{draft=draft.copy(title=it)})
     Choice("交付类型",draft.kind,workKinds){kind->draft=draft.copy(kind=kind,dueDate=if(!dueAutomatic)draft.dueDate else if(kind=="physical")null else runCatching{LocalDate.parse(draft.acceptedDate)}.getOrDefault(LocalDate.now()).plusDays(1).toString())}
     Choice("工作状态",draft.status,workStatuses){draft=draft.copy(status=it)}
     Field("接单日期",draft.acceptedDate,{value->val nextDue=if(dueAutomatic&&draft.kind!="physical")runCatching{LocalDate.parse(value).plusDays(1).toString()}.getOrDefault(draft.dueDate)else draft.dueDate;draft=draft.copy(acceptedDate=value,dueDate=nextDue)})
     Field("截止日期（可留空）",draft.dueDate?:"",{dueAutomatic=false;draft=draft.copy(dueDate=it.ifBlank{null})})
     Row{listOf(1L,3L,7L).forEach{days->TextButton(onClick={dueAutomatic=false;draft=draft.copy(dueDate=runCatching{LocalDate.parse(draft.acceptedDate)}.getOrDefault(LocalDate.now()).plusDays(days).toString())}){Text("接单后${days}天",fontSize=11.sp)}}}
     Text(if(draft.historicalDates)"历史未知日期保持留空，请按实际补充"else if(draft.kind=="physical")"实物提前${LocalWorkPreferences.current.first}天提醒，请填写实际截止日期"else"不填截止日期时默认为接单后一天",fontSize=11.sp,color=Grey)
     Field("订单金额（元）",quote,{quote=it},true)
     if(draft.status=="done")Field(if(draft.historicalDates)"完成日期（历史未知可留空）"else"完成日期（不填记今天）",draft.completedDate?:"",{draft=draft.copy(completedDate=it.ifBlank{null})})
     Field("当前做到哪里",draft.progress,{draft=draft.copy(progress=it)});Field("下一步做什么",draft.nextStep,{draft=draft.copy(nextStep=it)})
     Field("工程路径",draft.projectPath,{draft=draft.copy(projectPath=it)});if(draft.projectPath.isNotBlank())TextButton(onClick={onCopy(draft.projectPath)}){Text("复制工程路径")}
     Choice("关联原单（追加项目可选）",draft.parentId?:"",linkedMapOf("" to "不关联").apply{all.filter{it.id!=draft.id}.forEach{put(it.id,"${it.number} · ${it.title}")}}){draft=draft.copy(parentId=it.ifBlank{null})}
     OutlinedTextField(draft.notes,{draft=draft.copy(notes=it)},label={Text("备注")},modifier=Modifier.fillMaxWidth(),minLines=2)
    }else if(tab=="shipping"){
     Field("快递公司",draft.carrier,{draft=draft.copy(carrier=it)});Field("快递单号",draft.trackingNumber,{draft=draft.copy(trackingNumber=it)})
     Field("寄出日期（年-月-日）",draft.shippedDate?:"",{draft=draft.copy(shippedDate=it.ifBlank{null})});Field("收货日期（年-月-日）",draft.deliveredDate?:"",{draft=draft.copy(deliveredDate=it.ifBlank{null})})
     Text("工作完成、收款、快递分开跟进。",fontSize=12.sp,color=Grey)
    }else{
     val b=orderBalance(original,allEntries);Row(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.SpaceBetween){Text("净收款 ${yuan(b.received)}",color=Green,fontSize=12.sp);Text("利润 ${yuan(b.profit)}",color=if(b.profit>=BigInteger.ZERO)Green else Red,fontSize=12.sp)}
     allEntries.filter{it.orderId==original.id}.forEach{entry->Column(Modifier.fillMaxWidth().background(Color.White).padding(14.dp)){Row(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.SpaceBetween){Text("${moneyKinds[entry.kind]} · ${entry.method}",fontSize=13.sp);Text(yuan(entry.amountCents),color=if(entry.kind in listOf("receipt","cost-refund"))Green else Grey)};Text("${entry.date?:"日期未记"}${if(entry.voidedAt!=null)" · 已作废"else""}",fontSize=11.sp,color=Grey)}}
     SectionTitle("项目历史")
     val labels=mapOf("save-order" to "保存项目","add-money" to "记录收支","void-money" to "作废流水","trash-order" to "移入回收站","restore-order" to "恢复项目")
     records.forEach{item->Column(Modifier.fillMaxWidth().padding(vertical=8.dp)){Text(labels[item.action]?:item.action,fontSize=13.sp);Text(item.at.take(19).replace('T',' '),fontSize=11.sp,color=Grey);HorizontalDivider(color=Color(0xffe4e8ef),modifier=Modifier.padding(top=10.dp))}}
    }
    if(original.revision>0L)Row{TextButton(enabled=!busy,onClick={if(dirty)localError="请先保存当前项目，再追加一单"else{finished();onAdditional()}}){Text("追加一单")};TextButton(enabled=!busy,onClick=onTrash){Text("移入回收站",color=Red)}}
    Spacer(Modifier.height(4.dp))
   }
   Row(Modifier.fillMaxWidth().padding(18.dp),horizontalArrangement=Arrangement.End){TextButton(enabled=!busy,onClick={close()}){Text("取消")};Button(enabled=!busy&&!draftBlocked,onClick={try{localError="";onSave(draft.copy(quoteCents=inputCents(quote),tasks=normalizeTasks(draft.tasks)),{finished()})}catch(e:Exception){localError=e.message?:"请检查填写内容"}}){Text(if(busy)"保存中…"else"保存项目")}}
  }}
  BackHandler(enabled=!busy){close()}
 }
 if(discard)AlertDialog(onDismissRequest={discard=false},title={Text("放弃未保存的项目修改？")},text={Text("任务、日期与基本信息的修改尚未保存。")},confirmButton={TextButton(onClick={discardAndClose()}){Text("放弃修改")}},dismissButton={TextButton(onClick={discard=false}){Text("继续编辑")}})
}
@Composable private fun PaymentEditor(order:WorkOrder,initialKind:String,entries:List<WorkMoney>,busy:Boolean,problem:String,onDismiss:()->Unit,onSave:(WorkMoney)->Unit){
 var kind by remember{mutableStateOf(initialKind)};val context=LocalContext.current;val prefs=remember{context.getSharedPreferences("workbench-methods",Context.MODE_PRIVATE)}
 var method by remember{mutableStateOf(prefs.getString(initialKind,"微信")?:"微信")};var custom by remember{mutableStateOf("")};var amount by remember{mutableStateOf(if(initialKind=="receipt")BigDecimal(orderBalance(order,entries).remaining,2).toPlainString()else"")}
 var date by remember{mutableStateOf("")};var note by remember{mutableStateOf("")};var localError by remember{mutableStateOf("")};val id=remember{java.util.UUID.randomUUID().toString()}
 Dialog(onDismissRequest={if(!busy)onDismiss()},properties=DialogProperties(usePlatformDefaultWidth=false)){
  Surface(Modifier.fillMaxWidth().padding(18.dp),shape=MaterialTheme.shapes.large){Column(Modifier.padding(20.dp).verticalScroll(rememberScrollState()),verticalArrangement=Arrangement.spacedBy(12.dp)){
   SectionTitle(moneyKinds[kind]?:"记录收支","${order.number} · ${order.title}")
   if(problem.isNotEmpty()||localError.isNotEmpty())Text(localError.ifBlank{problem},color=Red,fontSize=13.sp)
   Choice("类型",kind,moneyKinds){kind=it};Field("金额（元）",amount,{amount=it},true)
   Choice("收支方式",method,linkedMapOf("微信" to "微信","支付宝" to "支付宝","银行卡" to "银行卡","现金" to "现金","其他" to "其他")){method=it}
   if(method=="其他")Field("自定义方式",custom,{custom=it});Field("日期（可不填）",date,{date=it});Row{TextButton(onClick={date=LocalDate.now().toString()}){Text("今天")};TextButton(onClick={date=""}){Text("日期未知")}};Field("备注（可选）",note,{note=it})
   Row(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.End){TextButton(enabled=!busy,onClick=onDismiss){Text("取消")};Button(enabled=!busy,onClick={try{localError="";val value=inputCents(amount);require(value>0){"请输入大于零的金额"};date.ifBlank{null}?.let{LocalDate.parse(it)};prefs.edit().putString(kind,method).apply();onSave(WorkMoney(id=id,orderId=order.id,kind=kind,amountCents=value,date=date.ifBlank{null},method=if(method=="其他")custom.ifBlank{"其他"}else method,note=note))}catch(e:Exception){localError=e.message?:"请检查填写内容"}}){Text(if(busy)"保存中…"else"确认${moneyKinds[kind]}")}}
  }}
 }
}

private val LocalWorkPreferences=staticCompositionLocalOf{4 to 1}
@Composable private fun WorkbenchNavigation(pages:Map<String,String>,selected:String,onSelect:(String)->Unit){
 Surface(color=Color.White,shadowElevation=0.dp){Column{
  HorizontalDivider(color=Color(0xffe4e8ef))
  Row(Modifier.fillMaxWidth().navigationBarsPadding().padding(top=8.dp,bottom=6.dp)){
   pages.forEach{(id,label)->Column(Modifier.weight(1f).clickable{onSelect(id)}.padding(vertical=5.dp),horizontalAlignment=Alignment.CenterHorizontally,verticalArrangement=Arrangement.spacedBy(5.dp)){
    NavGlyph(id,if(id==selected)Blue else Grey)
    Text(label,fontSize=10.sp,color=if(id==selected)Blue else Grey,fontWeight=if(id==selected)FontWeight.SemiBold else FontWeight.Normal)
   }}
  }
 }}
}
