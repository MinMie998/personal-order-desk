package com.personalorderdesk.nativepreview

import android.app.DatePickerDialog
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.Saver
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import java.time.LocalDate
import java.time.OffsetDateTime

private val Ink=Color(0xff243249)
private val Blue=Color(0xff3567df)
private val Muted=Color(0xff7b8494)
private val Green=Color(0xff16845c)
private val Red=Color(0xffcf4145)
private val WorkTaskSaver=Saver<WorkTask,String>(save={TaskConverters().encode(listOf(it))},restore={TaskConverters().decode(it).single()})
private val NullableWorkTaskSaver=Saver<WorkTask?,String>(save={it?.let{t->TaskConverters().encode(listOf(t))}?:""},restore={if(it.isEmpty())null else TaskConverters().decode(it).single()})

@Composable fun TaskFlowEditor(kind:String,tasks:List<WorkTask>,onChange:(List<WorkTask>)->Unit) {
    var editing by rememberSaveable(stateSaver=NullableWorkTaskSaver){mutableStateOf<WorkTask?>(null)}
    var filter by remember{mutableStateOf("all")}
    var collapsed by rememberSaveable{mutableStateOf(emptyList<String>())}
    var error by remember{mutableStateOf("")}
    val leaves=leafTasks(tasks);val done=leaves.count{it.status=="done"}
    fun apply(items:List<WorkTask>) { try { onChange(normalizeTasks(items));error="" }catch(e:Exception){error=e.message?:"任务未更新"} }
    Column(verticalArrangement=Arrangement.spacedBy(12.dp)) {
        Text("制作任务",fontSize=22.sp,fontWeight=FontWeight.SemiBold,color=Ink)
        Text("$done / ${leaves.size} 项完成 · 修改后点击底部保存项目",fontSize=12.sp,color=Muted)
        LinearProgressIndicator(progress={if(leaves.isEmpty())0f else done.toFloat()/leaves.size},modifier=Modifier.fillMaxWidth(),color=Green)
        Row(Modifier.horizontalScroll(rememberScrollState()),horizontalArrangement=Arrangement.spacedBy(8.dp)) {
            OutlinedButton(onClick={editing=WorkTask(title="新任务",reminderDays=if(kind=="physical")4 else 1)}){Text("＋ 任务")}
            if(tasks.isEmpty())Button(onClick={apply(taskTemplate(kind))}){Text("使用${workKinds[kind]?:"项目"}模板")}
        }
        Row(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
            listOf("all" to "全部","open" to "未完成","urgent" to "需关注").forEach{(key,label)->FilterChip(selected=filter==key,onClick={filter=key},label={Text(label)})}
        }
        if(error.isNotEmpty())Text(error,color=Red,fontSize=12.sp)
        if(tasks.isEmpty())Text("简单项目可不拆任务。实物项目可先套用流程，再补每一步的日期。",fontSize=13.sp,color=Muted)
        val displayed=tasks.filter{ t -> when(filter){"open"->t.status !in listOf("done","cancelled");"urgent"->taskDueLabel(t) in listOf("已逾期","今天截止","需要跟进","即将到期");else->true} }.map{it.id}.toSet()
        if(tasks.isNotEmpty() && displayed.isEmpty())Text("当前筛选没有任务",color=Muted)
        tasks.filter{it.parentId==null}.forEach { parent ->
            val children=tasks.filter{it.parentId==parent.id}
            if(parent.id in displayed || children.any{it.id in displayed}) {
                Surface(color=Color.White,shape=RoundedCornerShape(10.dp),border=BorderStroke(1.dp,Color(0xffdfe5ef))) {
                    Column {
                        TaskRow(parent,tasks,children.isNotEmpty(),onEdit={editing=parent},onStatus={status->apply(tasks.map{if(it.id==parent.id)it.copy(status=status)else it})},onChild={editing=WorkTask(parentId=parent.id,title="新子任务",reminderDays=if(kind=="physical")4 else 1)},onMove={apply(moveTaskWithinSiblings(tasks,parent.id,it))},onToggle=if(children.isEmpty())null else ({collapsed=if(parent.id in collapsed)collapsed-parent.id else collapsed+parent.id}),collapsed=parent.id in collapsed)
                        children.filter{it.id in displayed&&parent.id !in collapsed}.forEach { child ->
                            HorizontalDivider(color=Color(0xffe8edf4))
                            Box(Modifier.padding(start=16.dp).background(Color(0xffdbe5f6)).padding(start=3.dp)) {
                                TaskRow(child,tasks,false,onEdit={editing=child},onStatus={status->apply(tasks.map{if(it.id==child.id)it.copy(status=status)else it})},onMove={apply(moveTaskWithinSiblings(tasks,child.id,it))})
                            }
                        }
                    }
                }
            }
        }
    }
    editing?.let { initial -> TaskEditor(initial,tasks,onClose={editing=null},onApply={updated ->
        try { val found=tasks.any{it.id==updated.id};onChange(normalizeTasks(if(found)tasks.map{if(it.id==updated.id)updated else it}else tasks+updated));editing=null;error="" }
        catch(e:Exception){error=e.message?:"任务未更新";throw e}
    }) }
}

@Composable private fun TaskRow(task:WorkTask,all:List<WorkTask>,isGroup:Boolean,onEdit:()->Unit,onStatus:(String)->Unit,onChild:(()->Unit)?=null,onMove:((Int)->Unit)?=null,onToggle:(()->Unit)?=null,collapsed:Boolean=false) {
    val label=taskDueLabel(task)
    val tone=when(label){"已逾期","今天截止"->Red;"需要跟进","即将到期"->Color(0xffb57516);"已完成"->Green;else->Muted}
    Column(Modifier.fillMaxWidth().background(if(isGroup)Color(0xffedf3fc)else Color.White).padding(14.dp),verticalArrangement=Arrangement.spacedBy(6.dp)) {
        Row(verticalAlignment=Alignment.CenterVertically) {
            if(!isGroup)Checkbox(checked=task.status=="done",onCheckedChange={onStatus(if(it)"done"else"todo")})
            Column(Modifier.weight(1f)) {
                Text(task.title,fontWeight=FontWeight.SemiBold,color=Ink,fontSize=15.sp)
                Text(if(isGroup){val leaves=leafTasks(all.filter{it.parentId==task.id});"阶段 · ${leaves.count{it.status=="done"}}/${leaves.size} 完成"}else workStatuses[task.status]?:task.status,fontSize=11.sp,color=Muted)
            }
            Text(label,fontSize=11.sp,color=tone)
        }
        if(task.waitingReason.isNotBlank())Text("等待：${task.waitingReason}",fontSize=12.sp,color=Muted)
        if(task.notes.isNotBlank())Text(task.notes,fontSize=12.sp,color=Muted,maxLines=3)
        task.dependsOn?.let{id->all.find{it.id==id}?.let{Text("前置：${it.title}${if(it.status=="done")" · 已完成"else" · 尚未完成"}",fontSize=11.sp,color=Muted)}}
        if(task.startedAt!=null)Text("开始 ${task.startedAt.take(16).replace('T',' ')}${task.completedAt?.let{" · 完成 ${it.take(16).replace('T',' ')}"}?:""}",fontSize=10.sp,color=Muted)
        Row(Modifier.horizontalScroll(rememberScrollState())) {
            TextButton(onClick=onEdit){Text("编辑")}
            if(!isGroup && task.status !in listOf("done","cancelled")) {
                TextButton(onClick={onStatus(if(task.status=="doing")"waiting"else"doing")}){Text(if(task.status=="doing")"等待反馈"else if(task.status=="waiting")"继续做"else"开始做")}
            }
            onChild?.let{TextButton(onClick=it){Text("＋ 子任务")}}
            onToggle?.let{TextButton(onClick=it){Text(if(collapsed)"展开"else"折叠")}}
            onMove?.let{move->
                val siblings=all.filter{it.parentId==task.parentId};val at=siblings.indexOfFirst{it.id==task.id}
                TextButton(enabled=at>0,onClick={move(-1)}){Text("↑ 上移")}
                TextButton(enabled=at<siblings.lastIndex,onClick={move(1)}){Text("↓ 下移")}
            }
        }
    }
}

@Composable private fun TaskEditor(initial:WorkTask,all:List<WorkTask>,onClose:()->Unit,onApply:(WorkTask)->Unit) {
    var task by rememberSaveable(initial.id,stateSaver=WorkTaskSaver){mutableStateOf(initial)}
    var reminder by rememberSaveable(initial.id){mutableStateOf(initial.reminderDays.toString())}
    var error by remember{mutableStateOf("")};var discard by remember{mutableStateOf(false)}
    fun close(){if(task!=initial||reminder!=initial.reminderDays.toString())discard=true else onClose()}
    val group=all.any{it.parentId==initial.id}
    Dialog(onDismissRequest={close()}) {
        Surface(shape=RoundedCornerShape(14.dp),color=Color.White) {
            Column(Modifier.padding(18.dp).verticalScroll(rememberScrollState()).imePadding(),verticalArrangement=Arrangement.spacedBy(10.dp)) {
                Text(if(initial.parentId!=null)"编辑子任务"else"编辑任务",fontSize=20.sp,fontWeight=FontWeight.SemiBold)
                OutlinedTextField(task.title,{task=task.copy(title=it.take(200))},label={Text("任务名称")},modifier=Modifier.fillMaxWidth())
                if(!group)TaskChoice("状态",task.status,workStatuses){task=task.copy(status=it)}else Text("阶段状态由子任务汇总",fontSize=12.sp,color=Muted)
                TaskDate("计划开始日期",task.plannedStart){task=task.copy(plannedStart=it)}
                TaskDate("截止日期",task.dueDate){task=task.copy(dueDate=it)}
                TaskDate("跟进日期",task.followUpDate){task=task.copy(followUpDate=it)}
                OutlinedTextField(reminder,{reminder=it},label={Text("提前提醒天数（0—90）")},modifier=Modifier.fillMaxWidth())
                OutlinedTextField(task.waitingReason,{task=task.copy(waitingReason=it.take(750))},label={Text("等待原因")},modifier=Modifier.fillMaxWidth())
                OutlinedTextField(task.notes,{task=task.copy(notes=it.take(6000))},label={Text("进度与说明")},minLines=2,modifier=Modifier.fillMaxWidth())
                TaskChoice("前置任务",task.dependsOn?:"",linkedMapOf("" to "无前置任务").apply{all.filter{it.id!=task.id && it.parentId!=task.id}.forEach{put(it.id,it.title)}}){task=task.copy(dependsOn=it.ifBlank{null})}
                if(error.isNotEmpty())Text(error,color=Red,fontSize=12.sp)
                Row(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.End) {
                    TextButton(onClick={close()}){Text("取消")}
                    Button(onClick={try{onApply(task.copy(reminderDays=reminder.toIntOrNull()?:error("提醒天数应为0至90天")))}catch(e:Exception){error=e.message?:"请检查填写内容"}}){Text("应用到项目")}
                }
                Text("应用后还需保存项目。取消任务可在状态中选择，保留过程记录。",fontSize=11.sp,color=Muted)
            }
        }
        BackHandler{close()}
    }
    if(discard)AlertDialog(onDismissRequest={discard=false},title={Text("放弃任务修改？")},confirmButton={TextButton(onClick=onClose){Text("放弃修改")}},dismissButton={TextButton(onClick={discard=false}){Text("继续编辑")}})
}
@Composable private fun TaskDate(label:String,value:String?,onChange:(String?)->Unit) {
    val context=LocalContext.current
    Row(verticalAlignment=Alignment.CenterVertically) {
        OutlinedTextField(value?:"",{onChange(it.ifBlank{null})},label={Text(label)},singleLine=true,modifier=Modifier.weight(1f))
        TextButton(onClick={val d=runCatching{LocalDate.parse(value)}.getOrDefault(LocalDate.now());DatePickerDialog(context,{_,y,m,day->onChange(LocalDate.of(y,m+1,day).toString())},d.year,d.monthValue-1,d.dayOfMonth).show()}){Text("日历")}
    }
}
@Composable private fun TaskChoice(label:String,value:String,choices:Map<String,String>,onChange:(String)->Unit) {
    var open by remember{mutableStateOf(false)}
    Box {
        OutlinedButton(onClick={open=true},modifier=Modifier.fillMaxWidth()){Text("$label：${choices[value]?:value}")}
        DropdownMenu(expanded=open,onDismissRequest={open=false}){choices.forEach{(k,v)->DropdownMenuItem(text={Text(v)},onClick={open=false;onChange(k)})}}
    }
}
