package com.personalorderdesk.nativepreview

import org.junit.Assert.*
import org.junit.Test
import java.time.LocalDate
import java.util.UUID
import org.json.JSONArray
import org.json.JSONObject

class WorkTasksTest {
    private val now="2026-11-17T12:00:00+08:00"
    @Test fun siblingMovementRetainsIdsAndParentSummariesUseActualInstants() {
        val p=WorkTask(title="阶段");val a=WorkTask(title="A",parentId=p.id,status="done",startedAt="2026-11-17T08:00:00+08:00",completedAt="2026-11-17T09:00:00+08:00")
        val b=a.copy(id=UUID.randomUUID().toString(),title="B",startedAt="2026-11-17T01:00:00+00:00",completedAt="2026-11-17T02:00:00+00:00")
        val tasks=normalizeTasks(moveTaskWithinSiblings(listOf(p,a,b),b.id,-1),now)
        assertEquals(listOf(p.id,b.id,a.id),tasks.map{it.id});assertEquals(a.startedAt,tasks[0].startedAt);assertEquals(b.completedAt,tasks[0].completedAt)
    }
    @Test fun physicalTemplateRollsUpChildrenWithoutCompletingOrder() {
        val tasks=taskTemplate("physical");val parent=tasks.first()
        val a=normalizeTasks(tasks.map{if(it.parentId==parent.id)it.copy(status="done")else it},now)
        assertEquals("done",a.first().status);assertEquals(now,a.first().completedAt)
        assertEquals(6,leafTasks(a).size);assertEquals(2,leafTasks(a).count{it.status=="done"})
        assertEquals("todo",WorkOrder(tasks=a).status)
    }
    @Test fun taskRoundTripPreservesStableIdsDatesAndUnknownFieldsAreNotInvented() {
        val task=WorkTask(title="调试",dueDate="2026-11-18",notes="串口已通",status="waiting",followUpDate="2026-11-17")
        val encoded=TaskConverters().encode(listOf(task))
        assertEquals(listOf(task),TaskConverters().decode(encoded))
        assertNull(TaskConverters().decode(encoded).single().completedAt)
    }
    @Test fun rejectsCyclesDeepNestingDuplicateIdsAndReversedDates() {
        val a=WorkTask(title="A");val b=WorkTask(title="B",parentId=a.id)
        for(bad in listOf(listOf(a,a),listOf(a,b,WorkTask(title="C",parentId=b.id)),listOf(a.copy(dependsOn=b.id),b.copy(dependsOn=a.id)),listOf(a.copy(plannedStart="2026-11-18",dueDate="2026-11-17")))) {
            assertThrows(IllegalArgumentException::class.java){normalizeTasks(bad,now)}
        }
    }
    @Test fun completionTimeIsStableAndReopenClearsCompletion() {
        val task=WorkTask(title="调试",status="done")
        val first=normalizeTasks(listOf(task),now).single()
        assertEquals(now,first.startedAt);assertEquals(now,first.completedAt)
        assertEquals(first,normalizeTasks(listOf(first),"2026-11-18T12:00:00+08:00").single())
        val reopened=normalizeTasks(listOf(first.copy(status="doing")),now).single()
        assertNull(reopened.completedAt);assertEquals(now,reopened.startedAt)
    }
    @Test fun cancelledChildrenDoNotInflateProgressAndFinishedItemsDoNotWarn() {
        val tasks=listOf(WorkTask(title="A",status="cancelled"),WorkTask(title="B",status="done",dueDate="2026-11-01"))
        assertEquals(1,leafTasks(tasks).size)
        assertEquals("已完成",taskDueLabel(tasks[1],LocalDate.parse("2026-11-17")))
        assertEquals("需要跟进",taskDueLabel(WorkTask(title="等待",status="waiting",followUpDate="2026-11-16"),LocalDate.parse("2026-11-17")))
    }
    @Test fun legacyArchivePreservesUnknownDatesAndDerivesRepeatableHistoryIds() {
        val workspace=UUID.randomUUID().toString();val order=WorkOrder(number="SYNTHETIC",title="合成项目",status="done",dueDate=null,completedDate=null)
        val root=JSONObject().put("format","personal-order-desk-android-backup").put("version",1).put("workspaceId",workspace)
            .put("orders",JSONArray().put(JSONObject(order.wire()).apply{remove("tasks");remove("historicalDates")}))
            .put("money",JSONArray()).put("history",JSONArray().put(JSONObject().put("id","1").put("entityId",order.id).put("action","save-order").put("at",now).put("payload","{}")))
        val a=readPhoneArchive(root.toString());val b=readPhoneArchive(root.toString())
        assertTrue(a.orders.single().historicalDates);assertNull(a.orders.single().dueDate);assertNull(a.orders.single().completedDate)
        assertEquals(a.history.single().stableId,b.history.single().stableId)
        root.put("version",2).put("orders",JSONArray().put(JSONObject(order.copy(tasks=taskTemplate("physical")).wire())))
        assertEquals(9,readPhoneArchive(root.toString()).orders.single().tasks.size)
    }
}
