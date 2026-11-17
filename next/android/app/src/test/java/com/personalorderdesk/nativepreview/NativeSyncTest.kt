package com.personalorderdesk.nativepreview

import com.personalorderdesk.nativepreview.sync.NativeSync
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.io.File

/** Executes the real JNI DLL on the host JVM, not a Kotlin copy of the algorithm. */
class NativeSyncTest {
    private fun tree(value: Any?): Any? = when(value) {
        null, JSONObject.NULL -> null
        is JSONObject -> value.keys().asSequence().associateWith { tree(value.get(it)) }
        is JSONArray -> (0 until value.length()).map { tree(value.get(it)) }
        else -> value
    }
    @Test fun sharedGoldenVectorsThroughJni() {
        val vectors = JSONArray(File(System.getProperty("contracts.root"), "sync-core-vectors.json").readText())
        for (i in 0 until vectors.length()) {
            val vector = vectors.getJSONObject(i)
            val actual = NativeSync.execute(vector.getJSONObject("request"))
            assertEquals(vector.getString("name"), tree(vector.getJSONObject("expected")), tree(actual))
        }
    }
    @Test fun unsupportedOperationReturnsControlledError() {
        assertThrows(IllegalStateException::class.java) { NativeSync.execute(JSONObject().put("operation", "unknown")) }
    }
}
