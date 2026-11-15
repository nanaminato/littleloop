package dev.littleloop.chat

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject

data class BotCitation(val path: String, val start: Int, val end: Int, val hash: String, val text: String) {
    fun json() = json("path" to path, "start" to start, "end" to end, "sha256" to hash, "text" to text)
}

data class BotCard(val kind: String, val taskId: String?, val source: BotCitation?) {
    fun json() = json("kind" to kind, "taskId" to taskId, "source" to source?.json())
    companion object {
        fun decode(card: JSONObject) = BotCard(card.getString("kind"), card.stringOrNull("taskId"),
            card.optJSONObject("source")?.let {
                BotCitation(it.getString("path"), it.getInt("start"), it.getInt("end"), it.getString("sha256"), it.getString("text"))
            })
    }
}

internal data class BotRoom(val canOperate: Boolean, val installed: Boolean, val providers: List<String>)
internal data class BotApproval(val nonce: String, val method: String, val parameters: String)
internal data class BotReceipt(val id: String, val exitCode: Int, val stdout: String, val stderr: String)
internal data class BotWork(val status: String, val error: String?, val output: String?, val provider: String,
    val approval: BotApproval?, val patches: Map<String, String>, val receipts: List<BotReceipt>) {
    val terminal get() = status !in setOf("queued", "running")
}

internal class BotChat(private val api: ChatApi) {
    suspend fun room(room: String): BotRoom {
        val role = JSONObject(api.request("/bot/capabilities"))
        if (!role.optBoolean("enabled") || !role.optBoolean("canOperate")) return BotRoom(false, false, emptyList())
        val item = JSONObject(api.request("/bot/rooms/$room"))
        val providers = item.optJSONArray("providers")
        return BotRoom(true, item.optBoolean("installed"), providers?.strings().orEmpty())
    }

    suspend fun install(room: String) {
        api.request("/bot/rooms/$room/install", json("enabled" to true))
    }

    suspend fun sources(room: String) = JSONArray(api.request("/bot/rooms/$room/sources")).strings()

    suspend fun ask(room: String, id: String, text: String, path: String, start: Int, end: Int, provider: String) {
        api.request("/bot/rooms/$room/ask", json("clientId" to id, "text" to text, "path" to path,
            "start" to start, "end" to end, "provider" to provider))
    }

    suspend fun schedule(room: String, id: String, sequence: Long, goal: String, provider: String) {
        api.request("/bot/rooms/$room/schedule", json("clientId" to id, "sequence" to sequence,
            "goal" to goal, "provider" to provider))
    }

    suspend fun work(id: String): BotWork {
        val raw = api.request("/bot/work/$id")
        return withContext(Dispatchers.Default) {
            val item = JSONObject(raw)
            val execution = item.optJSONObject("execution")
            val approval = execution?.optJSONObject("approval")?.takeIf {
                item.optBoolean("canApprove") && item.optString("status") == "running"
            }?.let {
                BotApproval(it.getString("nonce"), it.optString("method"),
                    it.optJSONObject("parameters")?.toString(2) ?: it.optString("parameters"))
            }
            val patches = execution?.optJSONObject("changes")
            BotWork(item.getString("status"), item.stringOrNull("error"), item.stringOrNull("output"),
                item.optString("provider"), approval,
                patches?.keys()?.asSequence()?.associateWith { patches.getString(it) }.orEmpty(),
                execution?.optJSONArray("receipts")?.items {
                    BotReceipt(it.getString("id"), it.getInt("exitCode"), it.optString("standardOutput"),
                        it.optString("standardError"))
                }.orEmpty())
        }
    }

    suspend fun decide(id: String, approval: BotApproval, value: String) {
        api.request("/bot/work/$id/decision", json("nonce" to approval.nonce, "value" to value))
    }
}

internal fun JSONObject.stringOrNull(name: String) = if (isNull(name)) null else optString(name)

private fun JSONArray.strings() = (0 until length()).map(::getString)
