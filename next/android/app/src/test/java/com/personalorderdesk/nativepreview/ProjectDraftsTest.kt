package com.personalorderdesk.nativepreview

import java.io.File
import java.util.UUID
import org.junit.Assert.*
import org.junit.Test
import org.json.JSONObject

class ProjectDraftsTest {
    private fun folder():File=File(File(checkNotNull(System.getProperty("contracts.root"))).parentFile,"../tmp/android-draft-tests/${UUID.randomUUID()}").canonicalFile.apply{check(mkdirs())}
    private fun draft(workspace:String="workspace-a"):ProjectDraft {
        val base=WorkOrder(number="SYNTHETIC",title="合成项目",revision=4)
        return ProjectDraft(workspace,base,base.copy(title="尚未保存",tasks=taskTemplate("physical"),dueDate="尚未填完"),"12.","tasks")
    }
    @Test fun incompleteInputsAndTaskIdsSurviveDiskRoundTrip() {
        val store=ProjectDraftStore(folder());val value=draft();store.write(value)
        val restored=store.read(value.workspaceId,value.base.id)!!
        assertEquals(value,restored);assertEquals("12.",restored.quote)
        assertEquals(4,restored.base.revision);assertEquals(value.order.tasks.map{it.id},restored.order.tasks.map{it.id})
    }
    @Test fun workspacesAreIsolatedAndUnknownVersionsRemainUntouched() {
        val directory=folder();val store=ProjectDraftStore(directory);val value=draft();store.write(value)
        assertNull(store.read("workspace-b",value.base.id));assertTrue(store.list("workspace-b").isEmpty())
        val file=directory.listFiles()!!.single();val text=JSONObject(file.readText()).put("version",99).toString();file.writeText(text)
        assertThrows(IllegalArgumentException::class.java){store.read(value.workspaceId,value.base.id)}
        assertEquals(text,file.readText())
    }
    @Test fun pendingAutosaveCannotRecreateFinishedDraft() {
        val store=ProjectDraftStore(folder());val value=draft();val session=ProjectDraftSession(store,value.workspaceId,value.base.id)
        session.write(value);session.finish();session.write(value.copy(quote="999"))
        assertNull(store.read(value.workspaceId,value.base.id))
    }
    @Test fun wrongIdentityIsRejectedRatherThanResumed() {
        val directory=folder();val store=ProjectDraftStore(directory);val value=draft();store.write(value)
        val file=directory.listFiles()!!.single();file.writeText(JSONObject(file.readText()).put("workspaceId","other").toString())
        assertThrows(IllegalArgumentException::class.java){store.read(value.workspaceId,value.base.id)}
    }
    @Test fun backupReaderAcceptsSmallInputAndStopsOversizedStream() {
        assertEquals("合成备份",readBoundedBackup("合成备份".byteInputStream()))
        val input=object:java.io.InputStream(){var remaining=50L*1024*1024+1
            override fun read():Int=if(remaining-- > 0)32 else -1
            override fun read(buffer:ByteArray,offset:Int,length:Int):Int {
                if(remaining<=0)return -1
                val n=minOf(length.toLong(),remaining).toInt();buffer.fill(32,offset,offset+n);remaining-=n;return n
            }
        }
        assertThrows(IllegalArgumentException::class.java){readBoundedBackup(input)}
    }
}
