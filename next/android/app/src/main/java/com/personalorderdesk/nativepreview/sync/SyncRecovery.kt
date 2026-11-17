package com.personalorderdesk.nativepreview.sync

import org.json.JSONObject
import java.util.UUID
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import java.util.Base64

internal class SyncRecovery(val workspaceId:String,val repository:String,private val key:String,val keyId:String) {
    fun encode():String=JSONObject().put("format","personal-order-desk.sync-recovery").put("version",1)
        .put("workspaceId",workspaceId).put("repository",repository).put("key",key).put("keyId",keyId).toString()
    fun decrypt(envelope:JSONObject):JSONObject=NativeSync.execute(JSONObject().put("operation","decrypt").put("key",key).put("workspaceId",workspaceId).put("envelope",envelope)).getJSONObject("snapshot")
    fun encrypt(snapshot:JSONObject):JSONObject {
        require(snapshot.getString("workspaceId")==workspaceId){"上传工作台身份不匹配"}
        return NativeSync.execute(JSONObject().put("operation","encrypt").put("key",key).put("snapshot",snapshot))
    }
    override fun toString()="SyncRecovery(redacted)"
    companion object {
        fun parse(text:String):SyncRecovery {
            require(text.toByteArray().size<=16384){"恢复密钥文件过大"}
            val v=JSONObject(text)
            require(v.getString("format")=="personal-order-desk.sync-recovery"&&v.getInt("version")==1){"不是兼容的工作台恢复密钥文件"}
            val id=v.getString("workspaceId");UUID.fromString(id)
            val repo=v.getString("repository");require(GitHubAccount.validRepository(repo)){"恢复文件中的仓库名称无效"}
            val key=v.getString("key")
            val info=NativeSync.execute(JSONObject().put("operation","keyInfo").put("key",key))
            require(info.getString("keyId")==v.getString("keyId")){"恢复密钥校验失败"}
            return SyncRecovery(id,repo,key,info.getString("keyId"))
        }
    }
}

/** Recovery material is sealed in Room; Keystore key never leaves this device. */
internal object SyncRecoveryCipher {
    private const val ALIAS="PersonalOrderDesk.SyncRecovery.v1"
    private val aad="PersonalOrderDesk.SyncRecovery.v1".toByteArray(Charsets.UTF_8)
    private fun key(create:Boolean):SecretKey {
        val store=KeyStore.getInstance("AndroidKeyStore").apply{load(null)}
        val current=store.getKey(ALIAS,null) as? SecretKey
        if(current!=null)return current
        check(create){"手机恢复密钥无法解锁，请重新导入电脑导出的恢复密钥"}
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES,"AndroidKeyStore").apply{
            init(KeyGenParameterSpec.Builder(ALIAS,KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).setKeySize(256).build())
        }.generateKey()
    }
    fun seal(recovery:SyncRecovery):String {
        val c=Cipher.getInstance("AES/GCM/NoPadding");c.init(Cipher.ENCRYPT_MODE,key(true));c.updateAAD(aad)
        return JSONObject().put("version",1).put("iv",Base64.getEncoder().encodeToString(c.iv))
            .put("ciphertext",Base64.getEncoder().encodeToString(c.doFinal(recovery.encode().toByteArray(Charsets.UTF_8)))).toString()
    }
    fun open(text:String):SyncRecovery {
        try {
            val v=JSONObject(text);check(v.getInt("version")==1)
            val c=Cipher.getInstance("AES/GCM/NoPadding");c.init(Cipher.DECRYPT_MODE,key(false),GCMParameterSpec(128,Base64.getDecoder().decode(v.getString("iv"))));c.updateAAD(aad)
            return SyncRecovery.parse(c.doFinal(Base64.getDecoder().decode(v.getString("ciphertext"))).toString(Charsets.UTF_8))
        }catch(e:Exception){throw IllegalStateException("手机恢复密钥无法解锁，请重新导入；本地账本保留")}
    }
}
