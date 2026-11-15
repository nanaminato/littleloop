package dev.littleloop.chat

import android.content.ContentValues
import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

data class PendingMessage(
    val id: String, val room: String, val text: String, val replyTo: Long?, val failure: Int? = null,
    val fileName: String? = null,
)

internal class ChatLocalStore(context: Context) : SQLiteOpenHelper(context, "chat-local.db", null, 2) {
    override fun onConfigure(db: SQLiteDatabase) = db.setForeignKeyConstraintsEnabled(true)

    override fun onCreate(db: SQLiteDatabase) {
        db.execSQL("""
            CREATE TABLE outbox (
                position INTEGER PRIMARY KEY, session TEXT NOT NULL, id TEXT NOT NULL UNIQUE,
                room TEXT NOT NULL, body TEXT NOT NULL, reply_to INTEGER, failure INTEGER
            )
        """.trimIndent())
        db.execSQL("CREATE INDEX outbox_session ON outbox(session, position)")
        db.execSQL("""
            CREATE TABLE snapshots (
                session TEXT NOT NULL, name TEXT NOT NULL, payload TEXT NOT NULL,
                PRIMARY KEY(session, name)
            )
        """.trimIndent())
        onUpgrade(db, 1, 2)
    }

    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) {
        check(oldVersion == 1 && newVersion == 2)
        db.execSQL("ALTER TABLE outbox ADD COLUMN file_name TEXT")
        db.execSQL("""
            CREATE TABLE outbox_file (
                id TEXT NOT NULL REFERENCES outbox(id) ON DELETE CASCADE,
                part INTEGER NOT NULL, bytes BLOB NOT NULL, PRIMARY KEY(id, part)
            )
        """.trimIndent())
    }

    suspend fun enqueue(session: String, message: PendingMessage, bytes: ByteArray?) = withContext(Dispatchers.IO) {
        val db = writableDatabase
        db.beginTransaction()
        try {
            db.insertOrThrow("outbox", null, ContentValues().apply {
                put("session", session)
                put("id", message.id)
                put("room", message.room)
                put("body", message.text)
                put("reply_to", message.replyTo)
                put("file_name", message.fileName)
            })
            if (bytes != null) {
                for (offset in bytes.indices step FILE_CHUNK_SIZE) {
                    db.insertOrThrow("outbox_file", null, ContentValues().apply {
                        put("id", message.id)
                        put("part", offset)
                        put("bytes", bytes.copyOfRange(offset, minOf(offset + FILE_CHUNK_SIZE, bytes.size)))
                    })
                }
            }
            db.setTransactionSuccessful()
        } finally {
            db.endTransaction()
        }
    }

    suspend fun attachment(session: String, id: String): ByteArray = withContext(Dispatchers.IO) {
        val output = java.io.ByteArrayOutputStream()
        readableDatabase.rawQuery("""
            SELECT f.bytes FROM outbox_file f JOIN outbox o ON o.id=f.id
            WHERE o.session=? AND o.id=? ORDER BY f.part
        """.trimIndent(), arrayOf(session, id)).use { rows ->
            while (rows.moveToNext()) output.write(rows.getBlob(0))
        }
        output.toByteArray()
    }

    suspend fun entries(session: String): List<PendingMessage> = withContext(Dispatchers.IO) {
        readableDatabase.rawQuery(
            "SELECT id, room, body, reply_to, failure, file_name FROM outbox WHERE session = ? ORDER BY position", arrayOf(session),
        ).use { rows ->
            buildList {
                while (rows.moveToNext()) add(PendingMessage(rows.getString(0), rows.getString(1), rows.getString(2),
                    if (rows.isNull(3)) null else rows.getLong(3), if (rows.isNull(4)) null else rows.getInt(4), rows.getString(5)))
            }
        }
    }

    suspend fun remove(session: String, id: String) = withContext(Dispatchers.IO) {
        writableDatabase.delete("outbox", "session = ? AND id = ?", arrayOf(session, id))
    }

    suspend fun clear(session: String) = withContext(Dispatchers.IO) {
        val db = writableDatabase
        db.beginTransaction()
        try {
            db.delete("outbox", "session = ?", arrayOf(session))
            db.delete("snapshots", "session = ?", arrayOf(session))
            db.setTransactionSuccessful()
        } finally {
            db.endTransaction()
        }
    }

    suspend fun failure(session: String, id: String, status: Int?) = withContext(Dispatchers.IO) {
        writableDatabase.update("outbox", ContentValues().apply { put("failure", status) },
            "session = ? AND id = ?", arrayOf(session, id))
    }

    suspend fun snapshot(session: String, name: String): String? = withContext(Dispatchers.IO) {
        readableDatabase.rawQuery("SELECT payload FROM snapshots WHERE session = ? AND name = ?",
            arrayOf(session, name)).use { row -> if (row.moveToFirst()) row.getString(0) else null }
    }

    suspend fun histories(session: String, room: String?): Map<String, String> = withContext(Dispatchers.IO) {
        readableDatabase.rawQuery(
            if (room == null) "SELECT name,payload FROM snapshots WHERE session=? AND name<>'workspace'"
            else "SELECT name,payload FROM snapshots WHERE session=? AND name=?",
            if (room == null) arrayOf(session) else arrayOf(session, room),
        ).use { rows ->
            buildMap { while (rows.moveToNext()) put(rows.getString(0), rows.getString(1)) }
        }
    }

    suspend fun saveSnapshot(session: String, name: String, payload: String) = withContext(Dispatchers.IO) {
        val db = writableDatabase
        db.execSQL("""
            INSERT OR REPLACE INTO snapshots(session,name,payload)
            SELECT ?,?,? WHERE NOT EXISTS (
                SELECT 1 FROM snapshots WHERE session=? AND name=? AND payload=?
            )
        """.trimIndent(), arrayOf(session, name, payload, session, name, payload))
    }
}

private const val FILE_CHUNK_SIZE = 256 * 1024
