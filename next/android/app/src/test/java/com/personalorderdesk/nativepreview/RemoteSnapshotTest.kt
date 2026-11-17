package com.personalorderdesk.nativepreview

import com.personalorderdesk.nativepreview.sync.*
import org.json.JSONObject
import org.json.JSONArray
import org.junit.Assert.*
import org.junit.Test
import java.util.Base64
import java.security.MessageDigest

class RemoteSnapshotTest {
    private class Wire:GitHubWire {
        val book=syntheticPhoneBook();val recovery=syntheticRecovery();val head="a".repeat(40);val tree="b".repeat(40)
        val key=JSONObject(recovery.encode()).getString("key")
        val envelope=NativeSync.execute(JSONObject().put("operation","encrypt").put("snapshot",book).put("key",key))
        val bytes=envelope.toString().toByteArray(Charsets.UTF_8)
        val sha=MessageDigest.getInstance("SHA-1").apply{update("blob ${bytes.size}\u0000".toByteArray());update(bytes)}.digest().joinToString(""){"%02x".format(it)}
        var truncated=false;var tampered=false;val paths=mutableListOf<String>()
        override fun oauth(path:String,body:JSONObject):JSONObject=error("No OAuth call expected")
        override fun api(path:String,token:String):JSONObject {
            assertTrue(AndroidGitHubWire.validReadPath(path));paths+=path
            return when {
                path.endsWith("/git/ref/heads/desk-sync-v1")->JSONObject().put("object",JSONObject().put("sha",head))
                path.endsWith("/git/commits/$head")->JSONObject().put("tree",JSONObject().put("sha",tree))
                path.contains("/git/trees/")->JSONObject().put("truncated",truncated).put("tree",JSONArray().put(JSONObject().put("path","workspaces/${recovery.workspaceId}/snapshot.enc.json").put("type","blob").put("size",bytes.size).put("sha",sha)))
                path.endsWith("/git/blobs/$sha")->JSONObject().put("encoding","base64").put("content",Base64.getEncoder().encodeToString(if(tampered)"tampered".toByteArray()else bytes))
                else->JSONObject().put("private",true).put("full_name","synthetic/book")
            }
        }
    }
    private fun remote(wire:Wire):GitHubSnapshotRemote {
        val vault=object:AccountVault{override fun read()=JSONObject().put("accessToken","synthetic-access").put("refreshToken","synthetic-refresh").put("expiresAt",Long.MAX_VALUE).put("refreshExpiresAt",Long.MAX_VALUE).toString();override fun write(value:String){};override fun clear(){}}
        return GitHubSnapshotRemote(GitHubAccount(vault,wire))
    }
    @Test fun fixedGitReadChainVerifiesObjectAndDecryptsWithSharedCore() {
        val wire=Wire();val result=remote(wire).read(wire.recovery)
        assertEquals(wire.head,result.head);assertEquals(PhoneSnapshotCodec.digest(wire.book),PhoneSnapshotCodec.digest(result.snapshot))
        assertEquals(5,wire.paths.size)
    }
    @Test fun tamperingAndTruncatedListingStopBeforeApplyingAnything() {
        val wire=Wire();wire.tampered=true
        assertThrows(IllegalStateException::class.java){remote(wire).read(wire.recovery)}
        wire.tampered=false;wire.truncated=true
        assertThrows(IllegalStateException::class.java){remote(wire).read(wire.recovery)}
    }
    @Test fun arbitraryPathsAndWriteEndpointsAreNotAcceptedByTransport() {
        for(path in listOf("/repos/synthetic/book/git/refs/heads/desk-sync-v1","/repos/synthetic/book/git/trees/../evil","/repos/synthetic/book/contents/secret","https://example.com/user"))assertFalse(path,AndroidGitHubWire.validReadPath(path))
    }
}
