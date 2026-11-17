package com.personalorderdesk.nativepreview

import com.personalorderdesk.nativepreview.sync.*
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class GitHubAccountTest {
    private class Vault:AccountVault {var value:String?=null;override fun read()=value;override fun write(value:String){this.value=value};override fun clear(){value=null}}
    private class Wire:GitHubWire {
        val calls=mutableListOf<String>();var next=JSONObject().put("error","authorization_pending");var privateRepo=true
        override fun oauth(path:String,body:JSONObject):JSONObject {
            calls+=path;assertFalse(body.has("client_secret"))
            return if(path.endsWith("/code"))JSONObject().put("device_code","synthetic-device").put("user_code","TEST-CODE").put("expires_in",900).put("interval",5)else next
        }
        override fun api(path:String,token:String):JSONObject {calls+=path;return if(path=="/user")JSONObject().put("login","synthetic-user")else JSONObject().put("private",privateRepo).put("full_name","synthetic/book")}
    }
    private fun granted()=JSONObject().put("access_token","synthetic-access").put("refresh_token","synthetic-refresh").put("expires_in",28800).put("refresh_token_expires_in",15897600)
    @Test fun pendingAndSlowdownRespectServerIntervals() {
        val wire=Wire();var time=1000L;val account=GitHubAccount(Vault(),wire){time}
        assertNotNull(account.begin().optJSONObject("pending"));account.poll();assertEquals(1,wire.calls.size)
        time+=5;wire.next=JSONObject().put("error","slow_down");account.poll();assertEquals(2,wire.calls.size)
        time+=5;account.poll();assertEquals(2,wire.calls.size)
        time+=5;account.poll();assertEquals(3,wire.calls.size)
    }
    @Test fun loginPersistsOnlyAfterGrantAndPublicStatusNeverContainsTokens() {
        val vault=Vault();val wire=Wire();var time=1000L;val account=GitHubAccount(vault,wire){time}
        account.begin();assertNull(vault.value);time+=5;wire.next=granted()
        val status=account.poll();assertTrue(status.getBoolean("saved"));assertNull(status.optJSONObject("pending"))
        assertFalse(status.toString().contains("synthetic-access"));assertFalse(status.toString().contains("synthetic-refresh"))
        assertEquals("synthetic-user",account.checkAccount().getString("login"))
    }
    @Test fun rejectedReauthorizationKeepsPreviousLoginAndRemoveOnlyClearsVault() {
        val vault=Vault();val wire=Wire();var time=1000L;val account=GitHubAccount(vault,wire){time}
        account.begin();time+=5;wire.next=granted();account.poll();val original=vault.value
        account.begin();time+=5;wire.next=JSONObject().put("error","access_denied")
        assertThrows(IllegalStateException::class.java){account.poll()};assertEquals(original,vault.value)
        account.remove();assertNull(vault.value)
    }
    @Test fun expiryRefreshesBeforeReadAndPublicRepositoryIsRefused() {
        val vault=Vault();val wire=Wire();var time=1000L;val account=GitHubAccount(vault,wire){time}
        account.begin();time+=5;wire.next=granted();account.poll();time+=28800
        val result=account.checkRepository("synthetic/book");assertTrue(result.getBoolean("private"))
        assertEquals(3,wire.calls.count{it=="/login/oauth/access_token" || it.startsWith("/repos/")})
        wire.privateRepo=false;assertThrows(IllegalStateException::class.java){account.checkRepository("synthetic/public")}
        val calls=wire.calls.size
        for(bad in listOf("https://github.com/user/book","user/../book","user/..","user/book?x=1"))assertThrows(IllegalArgumentException::class.java){account.checkRepository(bad)}
        assertEquals(calls,wire.calls.size)
    }
}
