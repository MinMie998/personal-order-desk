package com.personalorderdesk.nativepreview

import com.personalorderdesk.nativepreview.sync.*
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.util.Base64
import java.util.UUID

internal fun syntheticPhoneBook():JSONObject {
    val id="11111111-1111-4111-8111-111111111111"
    val order=WorkOrder(id="22222222-2222-4222-8222-222222222222",number="SYNTHETIC",title="合成历史项目",status="done",quoteCents=90000,dueDate=null,completedDate=null,historicalDates=true,tasks=taskTemplate("physical"))
    val entries=listOf(WorkMoney(orderId=order.id,kind="receipt",amountCents=70000),WorkMoney(orderId=order.id,kind="cost",amountCents=35000),WorkMoney(orderId=order.id,kind="receipt",amountCents=5000,voidedAt="2026-11-17T12:00:00+08:00",voidReason="合成作废"))
    val event=WorkHistory(id=1,entityId=order.id,action="save-order",at="2026-11-17T12:00:00+08:00",payload="{}",stableId="0123456789abcdef0123456789abcdef")
    return PhoneSnapshotCodec.encode(PhoneArchive(id,listOf(order),entries,listOf(event)),JSONObject())
}
internal fun syntheticRecovery():SyncRecovery {
    val key=Base64.getEncoder().encodeToString(ByteArray(32){it.toByte()})
    val keyId=NativeSync.execute(JSONObject().put("operation","keyInfo").put("key",key)).getString("keyId")
    return SyncRecovery.parse(JSONObject().put("format","personal-order-desk.sync-recovery").put("version",1).put("workspaceId","11111111-1111-4111-8111-111111111111").put("repository","synthetic/book").put("key",key).put("keyId",keyId).toString())
}
class PhoneSnapshotTest {
    @Test fun windowsHistoryTasksDatesAndMoneyRoundTripExactly() {
        val source=syntheticPhoneBook();val archive=PhoneSnapshotCodec.decode(source)
        assertEquals("0123456789abcdef0123456789abcdef",archive.history.single().stableId)
        assertNull(archive.orders.single().completedDate);assertNull(archive.orders.single().dueDate)
        assertEquals(9,archive.orders.single().tasks.size)
        assertEquals(PhoneSnapshotCodec.digest(source),PhoneSnapshotCodec.digest(PhoneSnapshotCodec.encode(archive,source.getJSONObject("preferences"))))
        val summary=PhoneSnapshotCodec.summary(source)
        assertEquals("¥700.00",summary.getString("received"));assertEquals("¥350.00",summary.getString("profit"));assertEquals("¥200.00",summary.getString("remaining"))
    }
    @Test fun unsupportedFieldsInvalidMoneyAndParentCyclesAreRejected() {
        val future=syntheticPhoneBook().put("format",99)
        assertThrows(IllegalArgumentException::class.java){PhoneSnapshotCodec.decode(future)}
        val unknown=syntheticPhoneBook();unknown.getJSONObject("orders").getJSONObject("22222222-2222-4222-8222-222222222222").put("futureField",true)
        assertThrows(IllegalArgumentException::class.java){PhoneSnapshotCodec.decode(unknown)}
        val cycle=syntheticPhoneBook();cycle.getJSONObject("orders").getJSONObject("22222222-2222-4222-8222-222222222222").put("parentId","22222222-2222-4222-8222-222222222222")
        assertThrows(IllegalArgumentException::class.java){PhoneSnapshotCodec.decode(cycle)}
        val negative=syntheticPhoneBook();val id=UUID.randomUUID().toString()
        negative.getJSONObject("money").put(id,JSONObject(WorkMoney(id=id,orderId="22222222-2222-4222-8222-222222222222",kind="refund",amountCents=80000).wire()))
        assertThrows(IllegalArgumentException::class.java){PhoneSnapshotCodec.decode(negative)}
    }
    @Test fun recoveryKeyChecksDigestAndNeverPrintsSecret() {
        val recovery=syntheticRecovery();assertEquals("SyncRecovery(redacted)",recovery.toString())
        val bad=JSONObject(recovery.encode()).put("keyId","0".repeat(64))
        assertThrows(IllegalArgumentException::class.java){SyncRecovery.parse(bad.toString())}
    }
    @Test fun mergeRequiresBaselineAndRejectsAnotherGeneration() {
        val base=syntheticPhoneBook()
        assertThrows(IllegalStateException::class.java){preparePhoneMerge(null,base,base)}
        val remote=JSONObject(base.toString()).put("generation",UUID.randomUUID().toString())
        assertThrows(IllegalStateException::class.java){preparePhoneMerge(base.toString(),base,remote)}
        val merged=preparePhoneMerge(base.toString(),base,base)
        assertEquals(0,merged.getJSONArray("conflicts").length())
        assertEquals(PhoneSnapshotCodec.digest(base),PhoneSnapshotCodec.digest(merged.getJSONObject("snapshot")))
    }
    @Test fun downloadPreviewCannotOverwriteLocalChangesOrUseChangedRemoteOrBinding() {
        val local=syntheticPhoneBook();val state=PhoneSyncState(workspaceId=local.getString("workspaceId"),repository="synthetic/book",sealedRecovery="synthetic-sealed")
        val head="a".repeat(40);val book=RemoteBook(head,local,JSONObject())
        val plan=PhoneSyncPlan("download",local,book,JSONObject(),state,PhoneSnapshotCodec.digest(local),"synthetic-backup")
        requireDownloadCurrent(plan,state,local,head)
        val changed=JSONObject(local.toString());changed.getJSONObject("orders").getJSONObject("22222222-2222-4222-8222-222222222222").put("title","预览后修改")
        assertThrows(IllegalStateException::class.java){requireDownloadCurrent(plan,state,changed,head)}
        assertThrows(IllegalStateException::class.java){requireDownloadCurrent(plan,state,local,"b".repeat(40))}
        assertThrows(IllegalStateException::class.java){requireDownloadCurrent(plan,state.copy(repository="other/book"),local,head)}
        plan.consumed=true;assertThrows(IllegalStateException::class.java){requireDownloadCurrent(plan,state,local,head)}
    }
}
