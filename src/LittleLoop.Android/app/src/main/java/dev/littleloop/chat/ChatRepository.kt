package dev.littleloop.chat

import android.app.Application
import android.content.Context
import java.util.UUID
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import org.json.JSONArray
import org.json.JSONObject
import org.json.JSONTokener

class ChatApplication : Application() {
    lateinit var chat: ChatRepository
        private set
    override fun onCreate() { super.onCreate(); chat = ChatRepository(this) }
}

data class ChatState(
    val username: String = "", val rooms: List<Room> = emptyList(), val selected: String? = null,
    val messages: List<ChatMessage> = emptyList(), val settings: JSONObject = JSONObject(),
    val resuming: Boolean = false, val busy: Boolean = false, val connected: Boolean = false, val error: String? = null,
)

class ChatRepository(context: Context) {
    private val api = ChatApi(SessionStorage(context))
    private val local = context.getSharedPreferences("chat_preferences", Context.MODE_PRIVATE)
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val serial = Mutex()
    private var socket: WebSocket? = null
    private var handshake = false
    var foreground = false
        private set
    val backgroundEnabled = MutableStateFlow(false)
    private val listening get() = backgroundEnabled.value
    var origin = local.getString("origin", "") ?: ""
        private set
    val theme = MutableStateFlow(local.getString("theme", "system") ?: "system")
    val state = MutableStateFlow(ChatState(resuming = api.session != null))
    private val pending = mutableMapOf<String, Pair<String, String>>()

    init {
        scope.launch {
            while (isActive) {
                if (api.session != null && (foreground || listening)) {
                    attach()
                    if (handshake) socket?.send("{\"type\":6}\u001e")
                    refresh()
                }
                delay(if (foreground) 2500 else 4500)
            }
        }
    }

    fun foreground(value: Boolean) {
        foreground = value
        if (value) { attach(); scope.launch { refresh() } }
        else if (!listening) disconnect()
    }

    fun listening(value: Boolean) {
        backgroundEnabled.value = value
        if (value) { attach(); scope.launch { refresh() } }
        else if (!foreground) disconnect()
    }

    fun theme(value: String) { theme.value = value; local.edit().putString("theme", value).apply() }

    fun login(base: String, name: String, password: String) = action {
        api.login(base, name.trim(), password)
        origin = ChatApi.validateUrl(base).toString()
        local.edit().putString("origin", origin).apply()
        attach()
        load(force = true)
    }

    fun logout() = action {
        api.logout()
        pending.clear()
        disconnect()
        state.value = ChatState()
    }

    fun select(room: String?) {
        if (state.value.selected == room) return
        state.update { it.copy(selected = room, messages = emptyList(), error = null) }
        scope.launch { refresh(force = true) }
    }

    fun create(type: String, title: String, usernames: String, accepted: () -> Unit) = action {
        val members = usernames.split(',', '，', ' ').map(String::trim).filter(String::isNotEmpty)
        val result = JSONObject(api.request("/rooms", json("type" to type, "title" to title.trim(), "members" to JSONArray(members))))
        state.update { it.copy(selected = result.getString("id"), messages = emptyList()) }
        accepted()
        load(force = true)
    }

    fun send(text: String, accepted: () -> Unit) = action {
        val room = state.value.selected ?: return@action
        if (text.isBlank()) return@action
        val former = pending[room]
        val id = if (former?.first == text) former.second else UUID.randomUUID().toString()
        pending[room] = text to id
        api.request("/rooms/$room/messages", json("clientId" to id, "text" to text))
        pending.remove(room)
        accepted()
        load()
    }

    fun older() = action {
        val view = state.value
        val room = view.selected ?: return@action
        val first = view.messages.firstOrNull()?.sequence ?: return@action
        if (first <= 1) return@action
        val earlier = messages("/rooms/$room/messages?before=$first")
        if (state.value.selected == room)
            state.update { it.copy(messages = (earlier + it.messages).distinctBy(ChatMessage::sequence)) }
    }

    fun save(section: String, key: String, value: Any) = action {
        val current = JSONObject(api.request("/settings")).optJSONObject(section) ?: JSONObject()
        current.put(key, value)
        api.request("/settings/$section", current)
        load(force = true)
    }

    fun profile(name: String, bio: String) = action {
        api.request("/settings/profile", json("displayName" to name, "bio" to bio))
        load(force = true)
    }

    fun roomPreference(room: Room) = action {
        api.request("/rooms/${room.id}/preference", json("key" to "muted", "value" to !room.muted))
        load()
    }

    private fun action(work: suspend () -> Unit) {
        if (state.value.busy) return
        state.update { it.copy(busy = true, error = null) }
        scope.launch {
            try { serial.withLock { work() } }
            catch (e: Exception) { failed(e) }
            finally { state.update { it.copy(busy = false) } }
        }
    }

    private suspend fun refresh(force: Boolean = false) {
        if (api.session == null) return
        try { serial.withLock { load(force) } }
        catch (e: Exception) { failed(e) }
    }

    private suspend fun load(force: Boolean = false) {
        val settings = JSONObject(api.request("/settings"))
        val rooms = JSONArray(api.request("/rooms")).items {
            Room(it.getString("id"), it.getString("title"), it.getString("type"), it.getLong("latest"),
                it.getLong("unread"), it.getLong("read"), it.getBoolean("muted"), it.getBoolean("archived"),
                it.getBoolean("canPost"), it.optString("preview"))
        }
        val view = state.value
        val room = rooms.firstOrNull { it.id == view.selected }
        var history = if (room == null) emptyList() else view.messages
        if (room != null && foreground) {
            if (history.isEmpty()) {
                val recent = messages("/rooms/${room.id}/messages")
                // Refresh edits and tombstones without dropping pages already visible.
                history = (history + recent).associateBy(ChatMessage::sequence).values.sortedBy(ChatMessage::sequence)
            } else {
                var cursor = history.last().sequence
                while (cursor < room.latest) {
                    val next = messages("/rooms/${room.id}/messages?after=$cursor")
                    if (next.isEmpty()) break
                    history = history + next
                    cursor = history.last().sequence
                }
                if (force) {
                    val recent = messages("/rooms/${room.id}/messages")
                    history = (history + recent).associateBy(ChatMessage::sequence).values.sortedBy(ChatMessage::sequence)
                }
            }
            val seen = history.lastOrNull()?.sequence ?: 0
            if (seen > room.read && state.value.selected == room.id && foreground)
                api.request("/rooms/${room.id}/read", json("sequence" to seen))
        }
        val selected = state.value.selected
        state.update { it.copy(username = settings.optString("username"), settings = settings, rooms = rooms,
            selected = selected.takeIf { id -> rooms.any { room -> room.id == id } },
            messages = if (selected == view.selected) history else it.messages, resuming = false,
            connected = true, error = if (it.error == "Offline") null else it.error) }
    }

    private suspend fun messages(path: String) = JSONArray(api.request(path)).items {
        ChatMessage(it.getLong("sequence"), it.getString("sender"), it.optString("text"), it.getBoolean("deleted"),
            it.optString("sentAt"))
    }

    private fun attach() {
        if (socket != null || (!foreground && !listening)) return
        socket = api.connect(object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                webSocket.send("{\"protocol\":\"json\",\"version\":1}\u001e")
            }
            override fun onMessage(webSocket: WebSocket, text: String) {
                scope.launch {
                    if (socket != webSocket) return@launch
                    var changed = false
                    for (frame in text.split('\u001e').filter(String::isNotBlank)) {
                        val item = runCatching { JSONObject(frame) }.getOrNull() ?: continue
                        if (!handshake && !item.has("error")) handshake = true
                        if (item.optInt("type") == 1) changed = true
                        if (item.optInt("type") == 7) disconnect()
                    }
                    if (changed) refresh(force = foreground)
                }
            }
            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) = ended(webSocket)
            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) = ended(webSocket)
        })
    }

    private fun ended(closed: WebSocket) {
        scope.launch { if (socket == closed) { socket = null; handshake = false } }
    }

    private fun disconnect() {
        socket?.cancel()
        socket = null
        handshake = false
    }

    private fun failed(error: Exception) {
        if (error is ChatRequestException && error.status == 401) {
            api.clear()
            disconnect()
            pending.clear()
            state.value = ChatState(error = "Session expired")
        } else {
            val message = when (error) {
                is ChatRequestException -> when (error.status) {
                    400 -> (runCatching { JSONTokener(error.body).nextValue() }.getOrNull() as? String)
                        ?.take(240) ?: "请检查输入内容"
                    403 -> "没有执行此操作的权限"
                    else -> "请求失败（${error.status}）"
                }
                is IllegalArgumentException -> error.message
                else -> "Offline"
            }
            state.update { it.copy(resuming = false, connected = error is ChatRequestException, error = message) }
        }
    }
}
