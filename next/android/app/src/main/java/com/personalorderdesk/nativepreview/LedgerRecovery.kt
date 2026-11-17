package com.personalorderdesk.nativepreview

import android.content.Context
import java.io.File
import java.io.FileOutputStream
import java.nio.file.Files
import java.util.UUID
import java.io.InputStream

internal fun readBoundedBackup(input:InputStream):String=input.use { stream ->
    val output=java.io.ByteArrayOutputStream();val buffer=ByteArray(8192)
    while(true){val n=stream.read(buffer);if(n<0)break;if(n==0)continue
        require(output.size().toLong()+n<=50L*1024*1024){"备份超过50MiB，未修改本机数据"};output.write(buffer,0,n)
    };output.toString("UTF-8")
}

/** Only for a missing database. Existing/corrupt database replacement is deliberately refused. */
internal object LedgerRecovery {
    @Synchronized fun restoreMissing(context:Context,content:String) {
        val archive=readPhoneArchive(content)
        val target=context.getDatabasePath("workbench-next.db")
        require(!target.exists()){ "已有账本文件，请打开后使用备份恢复；不会覆盖现有文件" }
        require(!File(target.path+"-wal").exists()&&!File(target.path+"-shm").exists()){ "发现旧数据库日志，已停止恢复，请保留现有文件后排查" }
        WorkbenchDatabase.closeCached()
        val directory=File(context.filesDir,"backups");check(directory.isDirectory||directory.mkdirs()){"无法创建恢复保护目录"}
        val source=File(directory,"恢复来源-${UUID.randomUUID()}.json")
        val bytes=content.toByteArray(Charsets.UTF_8)
        require(bytes.size<=50*1024*1024){"备份超过50MiB"}
        FileOutputStream(source).use{it.write(bytes);it.fd.sync()}
        val stageName="recovery-${UUID.randomUUID()}.db"
        val stage=context.getDatabasePath(stageName)
        val db=WorkbenchDatabase.build(context,stageName)
        try {
            db.openHelper.writableDatabase
            db.restoreArchive(archive,source)
            check(db.dao().allOrders().map{it.id}.toSet()==archive.orders.map{it.id}.toSet()){"恢复项目校验失败"}
            check(db.dao().allMoney().toSet()==archive.money.toSet()){"恢复流水校验失败"}
            check(db.dao().workspace()?.workspaceId==archive.workspaceId){"恢复账本身份校验失败"}
            db.openHelper.writableDatabase.query("PRAGMA wal_checkpoint(TRUNCATE)").use{check(it.moveToFirst()&&it.getInt(0)==0){"恢复数据尚未完成写入"}}
        } finally { db.close() }
        check(!target.exists()){ "恢复期间已有账本出现，已保留临时恢复库，未替换" }
        // Same private directory, no replace option. Completed populated DB becomes the entry.
        Files.move(stage.toPath(),target.toPath())
        // If the process exits here, next launch still recognizes the populated existing DB.
        check(context.getSharedPreferences("workbench-next",Context.MODE_PRIVATE).edit().putBoolean("initialized",true).commit()){"数据已恢复，登记未完成，请重新打开"}
    }
}
