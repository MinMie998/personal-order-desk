package com.personalorderdesk.nativepreview
import org.junit.Test
import org.junit.Assert.*
import org.json.JSONObject
import java.io.File
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec
class CryptoTest{
 @Test fun sharedCiphertextAndAuthentication(){
 val v=JSONObject(File(System.getProperty("contracts.root"),"aes-gcm-vector.json").readText());fun bytes(k:String)=v.getString(k).chunked(2).map{it.toInt(16).toByte()}.toByteArray()
 val key=SecretKeySpec(bytes("key_hex"),"AES");val nonce=GCMParameterSpec(128,bytes("nonce_hex"));val aad=bytes("aad_hex");val plaintext=bytes("plaintext_hex");val expected=bytes("ciphertext_and_tag_hex")
 fun cipher(mode:Int,context:ByteArray)=Cipher.getInstance("AES/GCM/NoPadding").apply{init(mode,key,nonce);updateAAD(context)}
 assertArrayEquals(expected,cipher(Cipher.ENCRYPT_MODE,aad).doFinal(plaintext));assertArrayEquals(plaintext,cipher(Cipher.DECRYPT_MODE,aad).doFinal(expected))
 val bad=expected.clone();bad[0]=(bad[0].toInt() xor 1).toByte();assertThrows(javax.crypto.AEADBadTagException::class.java){cipher(Cipher.DECRYPT_MODE,aad).doFinal(bad)}
 assertThrows(javax.crypto.AEADBadTagException::class.java){cipher(Cipher.DECRYPT_MODE,"wrong-book".toByteArray()).doFinal(expected)}
 }
}
