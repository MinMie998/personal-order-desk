package com.personalorderdesk.nativepreview
import org.junit.Test
import org.junit.Assert.*
import org.json.*
import java.io.File
class ContractsTest {
 @Test fun exactMoney(){assertEquals(9007199254740993L,parseCents("9007199254740993"));listOf("","01","1.0","+1","-0","9223372036854775808").forEach{bad->assertThrows(IllegalArgumentException::class.java){parseCents(bad)}}}
 @Test fun sharedMergeVectors(){val all=JSONArray(File(System.getProperty("contracts.root"),"field-merge-vectors.json").readText());fun map(o:JSONObject)=o.keys().asSequence().associateWith{o.getString(it)}
 for(i in 0 until all.length()){val v=all.getJSONObject(i);val result=mergeFields(map(v.getJSONObject("baseline")),map(v.getJSONObject("local")),map(v.getJSONObject("remote")));assertEquals(v.getString("name"),map(v.getJSONObject("expected_fields")),result.fields);val expected=v.getJSONArray("expected_conflicts");assertEquals((0 until expected.length()).map{expected.getString(it)},result.conflicts)}}
}
