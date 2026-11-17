package com.personalorderdesk.nativepreview.sync

import com.personalorderdesk.nativepreview.*
import org.json.JSONObject
import org.json.JSONArray
import java.math.BigInteger

internal object PhoneSnapshotCodec {
    private val orderFields=setOf("id","number","title","kind","status","acceptedDate","dueDate","completedDate","quoteCents","progress","nextStep","projectPath","notes","trackingNumber","carrier","shippedDate","deliveredDate","parentId","revision","deletedAt","tasks","historicalDates")
    private val moneyFields=setOf("id","orderId","kind","amountCents","date","method","note","voidedAt","voidReason")
    private val preferenceFields=setOf("physicalReminderDays","digitalReminderDays","receiptMethod","costMethod","moneyAnimation","entryDateToday","defaultKind")
    fun digest(snapshot:JSONObject):String=NativeSync.execute(JSONObject().put("operation","digest").put("snapshot",snapshot)).getString("digest")
    fun normalized(snapshot:JSONObject):JSONObject=NativeSync.execute(JSONObject().put("operation","normalize").put("snapshot",snapshot)).getJSONObject("snapshot")
    fun decode(snapshot:JSONObject):PhoneArchive {
        require(snapshot.getInt("format") in listOf(1,2)){"账本格式不兼容，请升级客户端"}
        require(snapshot.keys().asSequence().all{it in setOf("format","workspaceId","generation","orders","money","history","preferences")}){"账本包含不支持的新字段，请升级客户端"}
        val orders=snapshot.getJSONObject("orders");val money=snapshot.getJSONObject("money");val history=snapshot.getJSONObject("history")
        val orderArray=JSONArray();val moneyArray=JSONArray();val historyArray=JSONArray()
        orders.keys().asSequence().sorted().forEach{id->val row=JSONObject(orders.getJSONObject(id).toString())
            require(row.keys().asSequence().all{it in orderFields}&&row.getString("id")==id&&row.get("quoteCents") is String){"订单字段或身份不兼容"}
            require(listOf("id","number","title","kind","status","acceptedDate","quoteCents","progress","nextStep","projectPath","notes","trackingNumber","carrier").all{row.get(it) is String}){"订单字段类型不兼容"}
            if(row.has("historicalDates"))require(row.get("historicalDates") is Boolean){"历史日期标记类型无效"}
            if(!row.has("revision"))row.put("revision",0)
            orderArray.put(row)
        }
        money.keys().asSequence().sorted().forEach{id->val row=money.getJSONObject(id)
            require(row.keys().asSequence().all{it in moneyFields}&&row.getString("id")==id&&row.get("amountCents") is String){"流水字段或身份不兼容"}
            require(listOf("id","orderId","kind","amountCents","method","note").all{row.get(it) is String}){"流水字段类型不兼容"};moneyArray.put(row)
        }
        history.keys().asSequence().sorted().forEachIndexed{i,id->val event=history.getJSONObject(id)
            require(Regex("[A-Za-z0-9._:-]{1,100}").matches(id)&&event.keys().asSequence().all{it in setOf("entityId","action","at","payload")}){"历史事件格式不兼容"}
            listOf("entityId","action","at","payload").forEach{require(event.get(it) is String){"历史事件字段格式无效"}}
            java.time.OffsetDateTime.parse(event.getString("at"))
            historyArray.put(JSONObject(event.toString()).put("id",(i+1).toString()).put("stableId",id))
        }
        val preferences=snapshot.getJSONObject("preferences")
        require(preferences.keys().asSequence().all{it in preferenceFields}){"偏好设置包含不支持的新字段"}
        for(k in listOf("physicalReminderDays","digitalReminderDays"))if(preferences.has(k))require((preferences.get(k) is Int||preferences.get(k) is Long)&&preferences.getLong(k) in 0..90){"提醒天数格式或范围无效"}
        for(k in listOf("moneyAnimation","entryDateToday"))if(preferences.has(k))require(preferences.get(k) is Boolean){"偏好设置类型不兼容"}
        for(k in listOf("receiptMethod","costMethod","defaultKind"))if(preferences.has(k))require(preferences.get(k) is String){"偏好设置类型不兼容"}
        val archive=readPhoneArchive(JSONObject().put("format","personal-order-desk-android-backup").put("version",2).put("workspaceId",snapshot.getString("workspaceId"))
            .put("generation",snapshot.optString("generation","legacy")).put("orders",orderArray).put("money",moneyArray).put("history",historyArray).toString())
        val index=archive.orders.associateBy{it.id}
        archive.orders.forEach{order->
            val seen=mutableSetOf(order.id);var parent=order.parentId
            while(parent!=null){require(seen.add(parent)&&index.containsKey(parent)){"项目追加关联无效或形成循环"};parent=index[parent]?.parentId}
            require(order.deliveredDate==null || order.shippedDate!=null&&order.deliveredDate>=order.shippedDate){"物流日期无效"}
            val b=orderBalance(order,archive.money);require(b.received>=BigInteger.ZERO&&b.cost>=BigInteger.ZERO){"合并后退款或退回超过原金额，请核对流水"}
        }
        return archive
    }
    fun snapshot(db:WorkbenchDatabase):JSONObject {
        var result=JSONObject()
        db.runInTransaction{
            val meta=checkNotNull(db.dao().workspace()){ "账本信息缺失" };val state=db.dao().syncState()
            result=encode(PhoneArchive(meta.workspaceId,db.dao().allOrders(),db.dao().allMoney(),db.dao().allHistory(),meta.generation),JSONObject(state?.preferences?:"{}"))
        };return result
    }
    fun encode(archive:PhoneArchive,preferences:JSONObject):JSONObject {
        val orders=JSONObject();archive.orders.forEach{orders.put(it.id,JSONObject(it.wire()))}
        val money=JSONObject();archive.money.forEach{money.put(it.id,JSONObject(it.wire()))}
        val history=JSONObject();archive.history.forEach{history.put(it.stableId,JSONObject().put("entityId",it.entityId).put("action",it.action).put("at",it.at).put("payload",it.payload))}
        return JSONObject().put("format",2).put("workspaceId",archive.workspaceId).put("generation",archive.generation).put("orders",orders).put("money",money).put("history",history).put("preferences",preferences)
    }
    fun summary(snapshot:JSONObject):JSONObject {
        val a=decode(snapshot);val active=a.orders.filter{it.deletedAt==null};var received=BigInteger.ZERO;var cost=BigInteger.ZERO;var remaining=BigInteger.ZERO
        active.forEach{o->val b=orderBalance(o,a.money);received+=b.received;cost+=b.cost;if(o.status!="cancelled")remaining+=b.remaining}
        return JSONObject().put("orders",active.size).put("money",a.money.size).put("received",yuan(received)).put("cost",yuan(cost)).put("profit",yuan(received-cost)).put("remaining",yuan(remaining))
    }
}
