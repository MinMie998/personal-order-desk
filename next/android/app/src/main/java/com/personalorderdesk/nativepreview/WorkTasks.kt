package com.personalorderdesk.nativepreview

import androidx.room.TypeConverter
import org.json.JSONArray
import org.json.JSONObject
import java.time.LocalDate
import java.time.OffsetDateTime
import java.util.UUID

data class WorkTask(
    val id: String = UUID.randomUUID().toString(), val parentId: String? = null,
    val title: String = "", val status: String = "todo", val plannedStart: String? = null,
    val dueDate: String? = null, val followUpDate: String? = null, val reminderDays: Int = 1,
    val notes: String = "", val waitingReason: String = "", val dependsOn: String? = null,
    val createdAt: String = OffsetDateTime.now().toString(), val startedAt: String? = null,
    val completedAt: String? = null,
)

class TaskConverters {
    @TypeConverter fun encode(tasks: List<WorkTask>): String = JSONArray().apply {
        tasks.forEach { t -> put(JSONObject().apply {
            put("id",t.id);put("title",t.title);put("status",t.status);put("reminderDays",t.reminderDays)
            put("notes",t.notes);put("waitingReason",t.waitingReason);put("createdAt",t.createdAt)
            mapOf("parentId" to t.parentId,"plannedStart" to t.plannedStart,"dueDate" to t.dueDate,
                "followUpDate" to t.followUpDate,"dependsOn" to t.dependsOn,"startedAt" to t.startedAt,
                "completedAt" to t.completedAt).forEach { (k,v) -> put(k,v ?: JSONObject.NULL) }
        }) }
    }.toString()
    @TypeConverter fun decode(text: String): List<WorkTask> {
        val a = JSONArray(text)
        require(a.length() <= 500) { "单笔订单最多500个任务" }
        return (0 until a.length()).map { i -> val o=a.getJSONObject(i)
            val supported=setOf("id","parentId","title","status","plannedStart","dueDate","followUpDate","reminderDays","notes","waitingReason","dependsOn","createdAt","startedAt","completedAt")
            require(o.keys().asSequence().all{it in supported}) { "任务含不支持的新字段，请升级客户端" }
            fun optional(k:String)=if(o.isNull(k))null else o.getString(k)
            WorkTask(o.getString("id"),optional("parentId"),o.getString("title"),o.getString("status"),
                optional("plannedStart"),optional("dueDate"),optional("followUpDate"),o.getInt("reminderDays"),
                o.getString("notes"),o.getString("waitingReason"),optional("dependsOn"),o.getString("createdAt"),
                optional("startedAt"),optional("completedAt"))
        }
    }
}

fun normalizeTasks(tasks: List<WorkTask>, now: String = OffsetDateTime.now().toString()): List<WorkTask> {
    require(tasks.size <= 500) { "单笔订单最多500个任务" }
    val index=tasks.associateBy { it.id }
    require(index.size==tasks.size) { "任务标识重复" }
    val checked=tasks.map { task ->
        UUID.fromString(task.id)
        require(task.title.isNotBlank() && task.title.toByteArray().size<=600) { "请填写任务名称（最多600字节）" }
        require(task.status in workStatuses && task.reminderDays in 0..90) { "任务状态或提醒天数无效" }
        require(task.notes.toByteArray().size<=24000 && task.waitingReason.toByteArray().size<=3000) { "任务说明过长" }
        listOfNotNull(task.plannedStart,task.dueDate,task.followUpDate).forEach { LocalDate.parse(it) }
        listOfNotNull(task.createdAt,task.startedAt,task.completedAt).forEach { OffsetDateTime.parse(it) }
        require(task.plannedStart==null || task.dueDate==null || task.plannedStart<=task.dueDate) { "截止日期不能早于计划开始日期" }
        task.parentId?.let { p -> require(p!=task.id && index[p]!=null && index[p]?.parentId==null) { "任务只支持两级，子任务必须关联顶层任务" } }
        var cursor=task.dependsOn;val seen=mutableSetOf(task.id)
        while(cursor!=null) { require(index.containsKey(cursor) && seen.add(cursor)) { "前置任务不存在或形成循环" };cursor=index[cursor]?.dependsOn }
        task.copy(title=task.title.trim(),startedAt=if(task.status in listOf("doing","waiting","done"))task.startedAt?:now else task.startedAt,
            completedAt=if(task.status=="done")task.completedAt?:now else null)
    }
    return checked.map { parent ->
        val children=checked.filter { it.parentId==parent.id }
        if(children.isEmpty())parent else {
            val active=children.filter { it.status!="cancelled" }
            val status=when { active.isEmpty()->"cancelled";active.all{it.status=="done"}->"done";active.all{it.status=="waiting"}->"waiting";active.any{it.status!="todo"}->"doing";else->"todo" }
            parent.copy(status=status,startedAt=children.mapNotNull{it.startedAt}.minByOrNull{OffsetDateTime.parse(it).toInstant()},completedAt=if(status=="done")active.mapNotNull{it.completedAt}.maxByOrNull{OffsetDateTime.parse(it).toInstant()}else null)
        }
    }
}
fun leafTasks(tasks:List<WorkTask>)=tasks.filter { t -> tasks.none { it.parentId==t.id } && t.status!="cancelled" }
fun moveTaskWithinSiblings(tasks:List<WorkTask>,id:String,delta:Int):List<WorkTask> {
    val index=tasks.indexOfFirst{it.id==id};require(index>=0){"任务不存在"}
    val siblings=tasks.indices.filter{tasks[it].parentId==tasks[index].parentId}
    val target=siblings.indexOf(index)+delta
    if(target !in siblings.indices)return tasks
    val result=tasks.toMutableList();val other=siblings[target];val temp=result[index];result[index]=result[other];result[other]=temp
    return result
}
fun taskDueLabel(t:WorkTask,today:LocalDate=LocalDate.now()):String {
    if(t.status in listOf("done","cancelled"))return workStatuses.getValue(t.status)
    val due=t.dueDate?.let(LocalDate::parse)
    return when { due!=null && due<today->"已逾期";due==today->"今天截止";
        t.status=="waiting" && t.followUpDate?.let{LocalDate.parse(it)<=today}==true->"需要跟进";
        due!=null && due<=today.plusDays(t.reminderDays.toLong())->"即将到期";else->due?.toString()?:"未设截止日期" }
}
fun taskTemplate(kind:String):List<WorkTask> {
    val groups=when(kind) {
        "physical"->listOf("设计与备料" to listOf("确认需求与接口","准备元件与制作方案"),"制作与调试" to listOf("焊接与装配","上电与功能调试"),"演示与交付" to listOf("录制演示并整理资料","打包交付并登记快递"))
        "schematic"->listOf("设计" to listOf("确认需求与接口","绘制原理图与PCB"),"检查与交付" to listOf("检查设计规则","整理工程并交付"))
        else->listOf("设计与实现" to listOf("确认需求","搭建仿真与编写程序"),"检查与交付" to listOf("运行与检查结果","整理说明并交付"))
    }
    return groups.flatMap { (title,children) -> val parent=WorkTask(title=title);listOf(parent)+children.map{WorkTask(title=it,parentId=parent.id)} }
}
