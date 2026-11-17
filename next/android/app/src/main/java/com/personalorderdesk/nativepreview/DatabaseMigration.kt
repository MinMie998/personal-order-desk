package com.personalorderdesk.nativepreview

import android.content.Context
import android.database.Cursor
import androidx.room.migration.Migration
import androidx.sqlite.db.SupportSQLiteDatabase
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.time.OffsetDateTime
import java.util.UUID

/** Called inside Room's upgrade transaction; backup failure aborts all schema changes. */
fun migrationV1ToV2(context:Context)=object:Migration(1,2) {
    override fun migrate(db:SupportSQLiteDatabase) {
        val workspace=context.getSharedPreferences("workbench-next",Context.MODE_PRIVATE).getString("workspaceId",null)?:UUID.randomUUID().toString()
        UUID.fromString(workspace)
        val backup=JSONObject().put("format","personal-order-desk-android-backup").put("version",1)
            .put("workspaceId",workspace).put("createdAt",OffsetDateTime.now().toString())
        for(table in listOf("orders","money","history")) {
            val rows=JSONArray()
            db.query("SELECT * FROM `$table`").use { c -> while(c.moveToNext()) {
                val row=JSONObject()
                for(i in 0 until c.columnCount) {
                    val name=c.getColumnName(i)
                    val value=when { c.isNull(i)->JSONObject.NULL;name in listOf("quoteCents","amountCents") || table=="history"&&name=="id"->c.getString(i)
                        c.getType(i)==Cursor.FIELD_TYPE_INTEGER->c.getLong(i);else->c.getString(i) }
                    row.put(name,value)
                };rows.put(row)
            } };backup.put(table,rows)
        }
        // Uses the existing import format: protection backup can be selected in the UI.
        readPhoneArchive(backup.toString())
        val directory=File(context.filesDir,"backups");check(directory.isDirectory||directory.mkdirs()) { "升级前无法创建保护备份目录" }
        val file=File(directory,"升级前-v1-${UUID.randomUUID()}.json")
        val bytes=backup.toString().toByteArray(Charsets.UTF_8)
        require(bytes.size<=50*1024*1024) { "账本超过手机备份大小限制，升级已停止，原数据保留" }
        FileOutputStream(file).use { it.write(bytes);it.fd.sync() }
        check(file.isFile && file.length()>0) { "升级前保护备份失败" }
        db.execSQL("ALTER TABLE orders ADD COLUMN tasks TEXT NOT NULL DEFAULT '[]'")
        db.execSQL("ALTER TABLE orders ADD COLUMN historicalDates INTEGER NOT NULL DEFAULT 0")
        // Old unknown dates stay null when the user next edits the project.
        db.execSQL("UPDATE orders SET historicalDates=1 WHERE dueDate IS NULL OR (status='done' AND completedDate IS NULL)")
        db.execSQL("ALTER TABLE history ADD COLUMN stableId TEXT NOT NULL DEFAULT ''")
        db.query("SELECT id FROM history").use { c -> while(c.moveToNext()) {
            val id=c.getLong(0);val stable=UUID.nameUUIDFromBytes("$workspace:history:$id".toByteArray(Charsets.UTF_8)).toString()
            db.execSQL("UPDATE history SET stableId=? WHERE id=?",arrayOf<Any>(stable,id))
        } }
        db.execSQL("CREATE UNIQUE INDEX index_history_stableId ON history(stableId)")
        db.execSQL("CREATE TABLE IF NOT EXISTS workspace_meta (id INTEGER NOT NULL,workspaceId TEXT NOT NULL,generation TEXT NOT NULL,schemaVersion INTEGER NOT NULL,syncProtocolVersion INTEGER NOT NULL,PRIMARY KEY(id))")
        db.execSQL("INSERT INTO workspace_meta VALUES(1,?,'legacy',2,2)",arrayOf(workspace))
    }
}

fun migrationV2ToV3(context:Context)=object:Migration(2,3) {
    override fun migrate(db:SupportSQLiteDatabase) {
        val meta=db.query("SELECT workspaceId,generation FROM workspace_meta WHERE id=1").use{c->check(c.moveToFirst()){ "工作台信息缺失，已停止升级" };c.getString(0) to c.getString(1)}
        val backup=JSONObject().put("format","personal-order-desk-android-backup").put("version",2).put("workspaceId",meta.first).put("generation",meta.second).put("createdAt",OffsetDateTime.now().toString())
        for(table in listOf("orders","money","history")) {
            val rows=JSONArray()
            db.query("SELECT * FROM `$table`").use{c->while(c.moveToNext()){
                val row=JSONObject()
                for(i in 0 until c.columnCount){val name=c.getColumnName(i)
                    val value=when{c.isNull(i)->JSONObject.NULL;name=="tasks"->JSONArray(c.getString(i));name=="historicalDates"->c.getInt(i)!=0
                        name in listOf("quoteCents","amountCents")||table=="history"&&name=="id"->c.getString(i)
                        c.getType(i)==Cursor.FIELD_TYPE_INTEGER->c.getLong(i);else->c.getString(i)}
                    row.put(name,value)
                };rows.put(row)
            }};backup.put(table,rows)
        }
        readPhoneArchive(backup.toString())
        val directory=File(context.filesDir,"backups");check(directory.isDirectory||directory.mkdirs()){"升级前无法保存保护备份"}
        val file=File(directory,"升级前-v2-${UUID.randomUUID()}.json");val bytes=backup.toString().toByteArray(Charsets.UTF_8)
        require(bytes.size<=50*1024*1024){"备份过大，升级已停止"}
        FileOutputStream(file).use{it.write(bytes);it.fd.sync()}
        db.execSQL("CREATE TABLE IF NOT EXISTS sync_state (id INTEGER NOT NULL,workspaceId TEXT NOT NULL,repository TEXT NOT NULL,sealedRecovery TEXT NOT NULL,baseline TEXT,confirmedHead TEXT,confirmedAt TEXT,preferences TEXT NOT NULL,PRIMARY KEY(id))")
        db.execSQL("UPDATE workspace_meta SET schemaVersion=3 WHERE id=1")
    }
}
