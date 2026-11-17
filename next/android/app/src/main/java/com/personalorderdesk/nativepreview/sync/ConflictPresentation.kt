package com.personalorderdesk.nativepreview.sync

import com.personalorderdesk.nativepreview.moneyKinds
import com.personalorderdesk.nativepreview.workStatuses
import com.personalorderdesk.nativepreview.yuan
import org.json.JSONArray
import org.json.JSONObject

internal fun conflictTitle(conflict:JSONObject,plan:PhoneSyncPlan):String {
    val record=conflict.getString("recordId");val collection=conflict.getString("collection")
    val field=conflict.getString("field")
    val orderId=if(collection=="money"){
        plan.local.optJSONObject("money")?.optJSONObject(record)?.optString("orderId")?:plan.remote.snapshot.optJSONObject("money")?.optJSONObject(record)?.optString("orderId")?:""
    }else record
    val order=plan.local.optJSONObject("orders")?.optJSONObject(orderId)?:plan.remote.snapshot.optJSONObject("orders")?.optJSONObject(orderId)
    val label=when{
        field.startsWith("task:")->"任务："+fieldLabel(field.substringAfterLast(':'))
        collection=="money"->"收支流水（整笔选择）"
        collection=="preferences"->"偏好设置："+fieldLabel(field)
        field=="record"->"项目删除或完整记录"
        else->fieldLabel(field)
    }
    return (order?.optString("number")?.takeIf{it.isNotBlank()}?.let{"$it · "}?:"")+label
}
internal fun fieldLabel(field:String):String=mapOf(
    "title" to "名称","number" to "编号","notes" to "备注","progress" to "当前进度","nextStep" to "下一步",
    "completion" to "状态与完成时间","deadline" to "类型与截止日期","status" to "状态","dueDate" to "截止日期",
    "plannedStart" to "计划开始","followUpDate" to "跟进日期","reminderDays" to "提前提醒天数","waitingReason" to "等待原因",
    "quoteCents" to "订单金额","trackingNumber" to "快递单号","carrier" to "快递公司","shippedDate" to "寄出日期","deliveredDate" to "收货日期",
    "projectPath" to "工程路径","parentId" to "原单关联","dependsOn" to "前置任务","taskOrder" to "任务顺序",
    "physicalReminderDays" to "实物提醒天数","digitalReminderDays" to "其他提醒天数","receiptMethod" to "收款方式","costMethod" to "支出方式","moneyAnimation" to "收款动效"
)[field]?:"相关记录"

internal fun describeConflictValue(value:Any?,field:String,taskNames:Map<String,String> = emptyMap()):String {
    if(value==null||value==JSONObject.NULL)return "无记录 / 未填写"
    if(value is Boolean)return if(value)"开启"else"关闭"
    if(value is JSONArray)return if(field=="taskOrder")(0 until value.length()).joinToString(" → "){taskNames[value.optString(it)]?:value.optString(it).take(8)}else "${value.length()} 项记录"
    if(value !is JSONObject)return if(field=="quoteCents")runCatching{yuan(value.toString().toBigInteger())}.getOrDefault("金额无效")else value.toString().take(800)
    val v=value
    if(v.has("amountCents"))return buildString {
        append(moneyKinds[v.optString("kind")]?:"收支");append(" ");append(runCatching{yuan(v.getString("amountCents").toBigInteger())}.getOrDefault("金额无效"))
        append(if(v.isNull("voidedAt"))" · 有效"else" · 已作废")
        append("\n${if(v.isNull("date"))"日期未知"else v.optString("date")} · ${v.optString("method")}")
        if(v.optString("note").isNotBlank())append("\n备注：${v.optString("note")}")
        if(!v.isNull("voidReason"))append("\n作废原因：${v.optString("voidReason")}")
    }.take(1000)
    return buildList {
        if(v.has("title"))add(v.optString("title"))
        if(v.has("status"))add("状态：${workStatuses[v.optString("status")]?:v.optString("status")}")
        if(v.has("deletedAt"))add(if(v.isNull("deletedAt"))"保留项目"else"移入回收站")
        if(v.has("quoteCents"))add("报价："+runCatching{yuan(v.getString("quoteCents").toBigInteger())}.getOrDefault("金额无效"))
        for(k in listOf("kind","acceptedDate","dueDate","completedDate","startedAt","completedAt","notes","progress","nextStep"))if(v.has(k)&&!v.isNull(k)){
            val name=mapOf("kind" to "类型","acceptedDate" to "接单日期","completedDate" to "完成日期","startedAt" to "开始时间","completedAt" to "完成时间")[k]?:fieldLabel(k)
            add("$name：${v.optString(k)}")
        }
        v.optJSONArray("tasks")?.let{add("包含 ${it.length()} 项任务")}
    }.joinToString("\n").ifBlank{"完整记录，选择一端保留"}.take(1200)
}
internal fun duplicateMoneyWarnings(plan:PhoneSyncPlan):List<String> {
    if(plan.mode!="merge"||plan.state.baseline==null)return emptyList()
    val base=JSONObject(plan.state.baseline).getJSONObject("money");val local=plan.local.getJSONObject("money");val remote=plan.remote.snapshot.getJSONObject("money")
    val fields=listOf("orderId","kind","amountCents","date")
    fun signature(row:JSONObject)=fields.map{if(row.isNull(it))null else row.getString(it)}
    val incoming=remote.keys().asSequence().filter{!base.has(it)&&remote.getJSONObject(it).isNull("voidedAt")}.groupBy{signature(remote.getJSONObject(it))}
    val result=linkedSetOf<String>()
    for(lid in local.keys())if(!base.has(lid)){
        val a=local.getJSONObject(lid)
        if(a.isNull("voidedAt")&&incoming[signature(a)]?.any{it!=lid}==true){
            val number=plan.local.getJSONObject("orders").optJSONObject(a.optString("orderId"))?.optString("number")?:"项目"
            result.add("$number：发现两笔金额/日期相同但 ID 不同的流水，将同时保留，请核对是否重复记录。")
            if(result.size>=20)return result.toList()
        }
    };return result.toList()
}
