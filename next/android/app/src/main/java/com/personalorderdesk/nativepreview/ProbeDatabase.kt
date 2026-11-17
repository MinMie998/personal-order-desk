package com.personalorderdesk.nativepreview
import android.content.Context
import androidx.room.*
@Entity(tableName="probe_notes")
data class ProbeNote(@PrimaryKey val id:Int=1,val title:String,val amountCents:Long)
@Dao interface ProbeDao {
 @Query("SELECT * FROM probe_notes WHERE id=1") suspend fun read():ProbeNote?
 @Upsert suspend fun save(note:ProbeNote)
}
@Database(entities=[ProbeNote::class],version=1,exportSchema=false)
abstract class ProbeDatabase:RoomDatabase(){
 abstract fun notes():ProbeDao
 companion object{
  @Volatile private var instance:ProbeDatabase?=null
  fun open(context:Context):ProbeDatabase=instance?:synchronized(this){instance?:Room.databaseBuilder(context.applicationContext,ProbeDatabase::class.java,"r0-local-probe.db").build().also{instance=it}}
 }
}
