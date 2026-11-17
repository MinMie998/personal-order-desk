package com.personalorderdesk.nativepreview

import com.personalorderdesk.nativepreview.sync.*
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.util.Base64
import java.io.File
import java.util.UUID

class RemotePublishTest {
    private class Wire:GitHubWire {
        val writes=mutableListOf<Triple<String,String,JSONObject>>()
        var currentHead="a".repeat(40);var rejectReference=false
        override fun oauth(path:String,body:JSONObject):JSONObject=error("OAuth not expected")
        override fun api(path:String,token:String):JSONObject=if(path.contains("/git/ref/"))JSONObject().put("object",JSONObject().put("sha",currentHead))else JSONObject().put("private",true).put("full_name","synthetic/book")
        override fun write(path:String,method:String,body:JSONObject,token:String):JSONObject {
            assertTrue(AndroidGitHubWire.validWritePath(path,method));writes+=Triple(path,method,JSONObject(body.toString()))
            return when {
                path.endsWith("/git/blobs")->JSONObject().put("sha","b".repeat(40))
                path.endsWith("/git/trees")->JSONObject().put("sha","c".repeat(40))
                path.endsWith("/git/commits")->JSONObject().put("sha","d".repeat(40))
                else->{check(!body.getBoolean("force"));if(rejectReference)error("remote changed");currentHead=body.getString("sha");JSONObject().put("object",JSONObject().put("sha",currentHead))}
            }
        }
    }
    private fun remote(wire:Wire):GitHubSnapshotRemote {
        val vault=object:AccountVault{override fun read()=JSONObject().put("accessToken","synthetic-access").put("refreshToken","synthetic-refresh").put("expiresAt",Long.MAX_VALUE).put("refreshExpiresAt",Long.MAX_VALUE).toString();override fun write(value:String){};override fun clear(){}}
        return GitHubSnapshotRemote(GitHubAccount(vault,wire))
    }
    private fun base()=RemoteBook("a".repeat(40),syntheticPhoneBook(),JSONObject(),"e".repeat(40),"f".repeat(40))
    @Test fun uploadIsEncryptedBacksUpOldBlobAndNeverForcesReference() {
        val wire=Wire();val book=syntheticPhoneBook();val recovery=syntheticRecovery()
        assertEquals("d".repeat(40),remote(wire).publish(recovery,base(),book))
        assertEquals(4,wire.writes.size)
        val envelope=JSONObject(Base64.getDecoder().decode(wire.writes[0].third.getString("content")).toString(Charsets.UTF_8))
        assertFalse(envelope.toString().contains("合成历史项目"));assertEquals(PhoneSnapshotCodec.digest(book),PhoneSnapshotCodec.digest(recovery.decrypt(envelope)))
        val entries=wire.writes[1].third.getJSONArray("tree")
        assertTrue(entries.getJSONObject(1).getString("path").contains("/backups/"));assertEquals("f".repeat(40),entries.getJSONObject(1).getString("sha"))
        assertEquals("a".repeat(40),wire.writes[2].third.getJSONArray("parents").getString(0))
        assertFalse(wire.writes.last().third.getBoolean("force"))
        val uploaded=recovery.decrypt(envelope).getJSONObject("orders")
        assertTrue(uploaded.getJSONObject(uploaded.keys().next()).has("revision"))
    }
    @Test fun changedHeadPreventsWritesAndRefFailureNeverRetriesWithForce() {
        val wire=Wire();wire.currentHead="0".repeat(40)
        assertThrows(IllegalStateException::class.java){remote(wire).publish(syntheticRecovery(),base(),syntheticPhoneBook())};assertTrue(wire.writes.isEmpty())
        wire.currentHead="a".repeat(40);wire.rejectReference=true
        assertThrows(IllegalStateException::class.java){remote(wire).publish(syntheticRecovery(),base(),syntheticPhoneBook())};assertEquals(4,wire.writes.size)
        assertFalse(wire.writes.last().third.getBoolean("force"))
        for(method in listOf("DELETE","PUT","GET"))assertFalse(AndroidGitHubWire.validWritePath("/repos/synthetic/book/git/refs/heads/desk-sync-v1",method))
        assertFalse(AndroidGitHubWire.validWritePath("/repos/synthetic/book/git/refs/heads/main","PATCH"))
    }
    @Test fun financialVoidConflictRequiresWholeRecordAndKeepsChosenVoid() {
        val base=syntheticPhoneBook();val local=JSONObject(base.toString());val remote=JSONObject(base.toString())
        val id=base.getJSONObject("money").keys().asSequence().first{base.getJSONObject("money").getJSONObject(it).optString("kind")=="receipt"&&base.getJSONObject("money").getJSONObject(it).isNull("voidedAt")}
        local.getJSONObject("money").getJSONObject(id).put("voidedAt","2026-11-17T12:00:00+08:00").put("voidReason","合成作废")
        remote.getJSONObject("money").getJSONObject(id).put("note","电脑改备注")
        val first=preparePhoneMerge(base.toString(),local,remote)
        val c=first.getJSONArray("conflicts");assertEquals(1,c.length())
        assertTrue(describeConflictValue(c.getJSONObject(0).get("local"),"record").contains("已作废"))
        val selected=preparePhoneMerge(base.toString(),local,remote,JSONObject().put(c.getJSONObject(0).getString("id"),"local"))
        assertEquals(0,selected.getJSONArray("conflicts").length());assertEquals("合成作废",selected.getJSONObject("snapshot").getJSONObject("money").getJSONObject(id).getString("voidReason"))
    }
    @Test fun journalPersistsUncertainAndConfirmedPhasesWithoutSecrets() {
        val folder=File(File(checkNotNull(System.getProperty("contracts.root"))).parentFile,"../tmp/sync-journal-tests/${UUID.randomUUID()}").canonicalFile
        val journal=SyncJournal(folder);val id=journal.begin("merge","synthetic/book","workspace","digest","head","target","backup")
        journal.phase(id,"publishing");assertEquals("publishing",SyncJournal(folder).list("workspace").getJSONObject(0).getString("phase"))
        journal.phase(id,"confirmed","published-head");assertEquals("confirmed",journal.list("workspace").getJSONObject(0).getString("phase"))
        assertFalse(folder.listFiles()!!.single().readText().contains("key"))
    }
}
