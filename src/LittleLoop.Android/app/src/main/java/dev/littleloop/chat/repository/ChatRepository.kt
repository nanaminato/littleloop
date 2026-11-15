package dev.littleloop.chat

import android.app.Application
import android.content.Context
import android.net.Uri
import android.provider.OpenableColumns
import java.io.IOException
import java.util.UUID
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
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
    val outbox: List<PendingMessage> = emptyList(),
    val upload: UploadProgress? = null,
    val focus: Long? = null,
    val openComments: Long? = null,
    val notificationTarget: String? = null,
    val openWork: String? = null,
    val timelineRevision: Long? = null,
    val channels: List<ChatChannel> = emptyList(),
    val channelsLoaded: Boolean = false,
    val friends: List<ChatFriend> = emptyList(),
    val members: List<ChatMember> = emptyList(),
    val membersRoom: String? = null,
)

data class UploadProgress(val id: String, val percent: Int)
data class SelectedAttachment(val uri: Uri, val name: String)
data class SearchHit(val room: String, val message: ChatMessage)
data class SearchPage(val hits: List<SearchHit>, val offline: Boolean, val hasMore: Boolean)

class ChatRepository(context: Context) {
    private val api = ChatApi(SessionStorage(context))
    internal val push = NativePush(context, api)
    internal val bot = BotChat(api)
    private val outbox = ChatLocalStore(context)
    private val files = context.contentResolver
    private val appContext = context.applicationContext
    private val strings get() = Localization.localized(appContext)
    private val local = context.getSharedPreferences(Localization.PREFERENCES, Context.MODE_PRIVATE)
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val serial = Mutex()
    private val invalidations = Channel<Unit>(Channel.CONFLATED)
    private val realtime = ChatRealtime(api, scope) { invalidations.trySend(Unit) }
    var foreground = false
        private set
    val backgroundEnabled = MutableStateFlow(false)
    private val listening get() = backgroundEnabled.value
    var origin = local.getString("origin", "") ?: ""
        private set
    val theme = MutableStateFlow(local.getString("theme", "system") ?: "system")
    val language = MutableStateFlow(Localization.saved(context))
    val state = MutableStateFlow(ChatState(resuming = api.session != null))
    private var directoryOpen = false
    private var friendsOpen = false
    private var memberRoom: String? = null
    private var timelineVisible = true

    init {
        scope.launch {
            for (ignored in invalidations) {
                if (foreground || listening) refresh(force = true)
            }
        }
        scope.launch {
            serial.withLock {
                api.session?.outboxKey?.let { restore(it) }
            }
            while (isActive) {
                if (api.session != null && (foreground || listening)) {
                    attach()
                    realtime.heartbeat()
                    refresh()
                }
                delay(if (foreground) 2500 else 4500)
            }
        }
    }

    fun foreground(value: Boolean) {
        foreground = value
        if (value) { attach(); push.refreshRegistration(); scope.launch { refresh() } }
        else if (!listening) realtime.disconnect()
    }

    fun listening(value: Boolean) {
        backgroundEnabled.value = value
        if (value) { attach(); scope.launch { refresh() } }
        else if (!foreground) realtime.disconnect()
    }

    fun theme(value: String) { theme.value = value; local.edit().putString("theme", value).apply() }

    fun language(value: String) {
        language.value = value
        local.edit().putString(Localization.KEY, value).apply()
    }

    fun login(base: String, name: String, password: String) = action {
        api.login(base, name.trim(), password)
        push.sessionChanged()
        origin = ChatApi.validateUrl(base).toString()
        local.edit().putString("origin", origin).apply()
        attach()
        load(force = true)
    }

    fun logout() = action {
        val key = api.session?.outboxKey
        try {
            api.logout()
        } catch (_: IOException) {
            api.clear()
        } finally {
            realtime.disconnect()
            push.sessionChanged()
            state.value = ChatState()
            if (key != null) outbox.clear(key)
        }
    }

    fun select(room: String?) {
        if (state.value.selected == room && state.value.focus == null) return
        state.update { it.copy(selected = room, messages = emptyList(), error = null, focus = null, openWork = null, timelineRevision = null) }
        scope.launch {
            serial.withLock {
                val key = api.session?.outboxKey
                if (key != null && room != null && state.value.selected == room && state.value.messages.isEmpty()) {
                    val cached = runCatching { outbox.snapshot(key, room)?.let(::decodeMessages) }.getOrNull()
                    if (cached != null && state.value.selected == room && state.value.messages.isEmpty())
                        state.update { it.copy(messages = cached) }
                }
            }
            refresh(force = true)
        }
    }

    suspend fun search(term: String, room: String?, offset: Int): SearchPage {
        val session = api.session ?: throw ChatRequestException(401, "")
        val hits = try {
            val path = "/search?q=${Uri.encode(term)}&offset=$offset" + (room?.let { "&room=${Uri.encode(it)}" } ?: "")
            JSONArray(api.request(path)).items { SearchHit(it.getString("roomId"), decodeMessage(it)) }
        } catch (failure: IOException) {
            if (failure is ChatRequestException) throw failure
            val allowed = state.value.rooms.map { it.id }.toSet()
            val snapshots = outbox.histories(session.outboxKey, room)
            val cached = withContext(Dispatchers.Default) {
                snapshots.asSequence().filter { it.key in allowed }.flatMap { (roomId, snapshot) ->
                    decodeMessages(snapshot).asSequence().filter {
                        !it.deleted && (it.text.contains(term, ignoreCase = true) ||
                            it.attachment?.name?.contains(term, ignoreCase = true) == true)
                    }.map { SearchHit(roomId, it) }
                }.sortedWith(compareByDescending<SearchHit> { it.message.sentAt }.thenBy { it.room }
                    .thenByDescending { it.message.sequence })
                    .drop(offset).take(51).toList()
            }
            if (api.session !== session) throw ChatRequestException(401, "")
            return SearchPage(cached.take(50), offline = true, hasMore = cached.size > 50)
        }
        if (api.session !== session) throw ChatRequestException(401, "")
        return SearchPage(hits, offline = false, hasMore = hits.size == 50)
    }

    suspend fun jump(hit: SearchHit): Boolean = jump(hit.room, hit.message.sequence)

    fun openNotification(room: String, sequence: Long, work: String?, owner: String, comments: Boolean = false) {
        scope.launch {
            if (!push.acceptsLink(owner)) return@launch
            val opened = runCatching { jump(room, sequence, work, owner, comments) }.getOrDefault(false)
            if (!opened && push.acceptsLink(owner)) state.update { it.copy(error = strings.getString(R.string.error_message_unavailable)) }
        }
    }

    fun openDiscussionLink(room: String, sequence: Long) {
        scope.launch {
            try {
                val session = api.session ?: return@launch
                val visible = state.value.rooms.firstOrNull { it.id == room }
                    ?: decodeRooms(JSONArray(api.request("/rooms", expected = session))).firstOrNull { it.id == room }
                if (api.session !== session || visible?.type != "channel") return@launch
                if (!jump(room, sequence, comments = true))
                    state.update { it.copy(error = strings.getString(R.string.error_message_unavailable)) }
            } catch (failure: IOException) { failed(failure) }
        }
    }

    fun commentLinkOpened() { state.update { it.copy(openComments = null) } }

    fun workLinkOpened() { state.update { it.copy(openWork = null) } }

    fun openPinned(room: Room) {
        val sequence = room.pinnedMessage ?: return
        scope.launch {
            try {
                if (!jump(room.id, sequence)) state.update { it.copy(error = strings.getString(R.string.error_pinned_unavailable)) }
            } catch (e: IOException) { failed(e) }
        }
    }

    fun notificationLinkOpened(id: String) {
        state.update { if (it.notificationTarget == id) it.copy(notificationTarget = null) else it }
    }

    private suspend fun jump(roomId: String, target: Long, work: String? = null, owner: String? = null, comments: Boolean = false): Boolean = serial.withLock {
        val session = api.session ?: return@withLock false
        if (owner != null && !push.acceptsLink(owner)) return@withLock false
        if (target <= 0 || target == Long.MAX_VALUE) return@withLock false
        val history = try {
            val through = messages("/rooms/$roomId/messages?before=${target + 1}")
            if (through.none { it.sequence == target && !it.deleted }) return@withLock false
            through.takeLast(25) + messages("/rooms/$roomId/messages?after=$target").take(25)
        } catch (failure: IOException) {
            if (failure is ChatRequestException) {
                if (failure.status in setOf(403, 404)) return@withLock false
                throw failure
            }
            if (state.value.rooms.none { it.id == roomId }) return@withLock false
            outbox.snapshot(session.outboxKey, roomId)?.let(::decodeMessages).orEmpty()
        }
        val original = history.firstOrNull { it.sequence == target && !it.deleted }
        if (api.session !== session || original == null || (work != null && original.card?.taskId != work)) return@withLock false
        state.update { it.copy(selected = roomId, messages = history, focus = target, error = null, openWork = work, openComments = target.takeIf { comments }, timelineRevision = null,
            notificationTarget = if (owner == null) it.notificationTarget else UUID.randomUUID().toString()) }
        true
    }

    fun create(type: String, title: String, usernames: String, accepted: () -> Unit) = action {
        val members = usernames.split(',', '，', ' ').map(String::trim).filter(String::isNotEmpty)
        val result = JSONObject(api.request("/rooms", json("type" to type, "title" to title.trim(), "members" to JSONArray(members))))
        state.update { it.copy(selected = result.getString("id"), messages = emptyList(), focus = null, timelineRevision = null) }
        accepted()
        load(force = true)
    }

    fun browseSocial(friends: Boolean, group: String?) {
        friendsOpen = friends
        memberRoom = group
        if (friends || group != null) scope.launch { refresh() }
    }

    fun requestFriend(username: String, accepted: () -> Unit) = action {
        api.request("/friends", json("username" to username.trim()))
        accepted()
        load()
    }

    fun respondFriend(friend: ChatFriend, accepted: Boolean) = action {
        api.request("/friends/${friend.id}/respond", json("accepted" to accepted))
        load()
    }

    fun changeMember(room: String, username: String, operation: String, accepted: () -> Unit = {}) = action {
        api.request("/rooms/$room/members", json("username" to username.trim(), "operation" to operation))
        accepted()
        load()
    }

    fun browseChannels(open: Boolean) {
        directoryOpen = open
        if (open) scope.launch { refresh() }
    }

    fun showingTimeline(visible: Boolean) {
        if (timelineVisible == visible) return
        timelineVisible = visible
        if (visible && foreground) scope.launch { refresh() }
    }

    fun createSpace(parent: String?, title: String, accepted: () -> Unit) = action {
        val path = if (parent == null) "/channels" else "/rooms/$parent/topics"
        val id = JSONObject(api.request(path, json("title" to title.trim()))).getString("id")
        state.update { it.copy(selected = id, messages = emptyList(), focus = null, timelineRevision = null, openWork = null) }
        accepted()
        load()
    }

    fun subscribe(channel: ChatChannel) = action {
        api.request("/channels/${channel.id}/subscription", json("subscribed" to !channel.subscribed))
        load()
    }

    internal suspend fun requestDiscussionMembership(channel: String) {
        api.request("/channels/$channel/discussion/join", JSONObject())
    }

    internal suspend fun discussionJoinRequests(group: String): List<String> {
        val requests = JSONArray(api.request("/rooms/$group/discussion-requests"))
        return List(requests.length()) { requests.getString(it) }
    }

    internal suspend fun channelAudit(room: String, before: Long?): List<ChannelAuditEntry> {
        val cursor = before?.let { "?before=$it" }.orEmpty()
        return JSONArray(api.request("/channels/$room/audit$cursor")).items {
            ChannelAuditEntry(it.getLong("id"), it.getString("actor"), it.getString("action"),
                it.getString("detail"), it.optString("createdAt"))
        }
    }

    internal suspend fun channelSettings(room: String, username: String): ChannelSettings {
        val grants = JSONArray(api.request("/channels/$room/admins")).items {
            ChannelGrant(it.getString("name"), it.getInt("rights"))
        }
        val group = JSONObject(api.request("/channels/$room/discussion")).stringOrNull("groupId")
        val owner = JSONArray(api.request("/rooms/$room/members")).items {
            it.optBoolean("owner") && it.optString("name") == username
        }.any { it }
        return ChannelSettings(owner, group, grants)
    }

    internal suspend fun removeChannelSubscriber(room: String, name: String) {
        api.request("/channels/$room/subscribers/${Uri.encode(name)}", method = "DELETE")
    }

    internal suspend fun transferChannelOwnership(room: String, username: String) {
        api.request("/channels/$room/ownership", json("username" to username), method = "PUT")
    }

    internal suspend fun deleteChannel(room: String) {
        api.request("/channels/$room", method = "DELETE")
    }

    internal suspend fun updateChannelProfile(room: String, title: String, signPosts: Boolean) {
        api.request("/channels/$room/profile", json("title" to title, "signPosts" to signPosts), method = "PUT")
    }

    internal suspend fun setChannelGrant(room: String, name: String, rights: Int) {
        api.request("/channels/$room/admins/${Uri.encode(name)}", json("rights" to rights), method = "PUT")
    }

    internal suspend fun linkDiscussion(room: String, group: String?) {
        api.request("/channels/$room/discussion",
            JSONObject().put("groupId", group ?: JSONObject.NULL), method = "PUT")
    }

    internal suspend fun setPostDiscussion(channel: String, post: Long, open: Boolean) {
        api.request("/channels/$channel/posts/$post/discussion", json("open" to open), method = "PUT")
    }

    internal suspend fun followChannelDiscussion(channel: String, post: Long, following: Boolean) {
        api.request("/channels/$channel/posts/$post/comments/follow", json("following" to following), method = "PUT")
    }

    internal suspend fun markChannelCommentsRead(channel: String, post: Long, sequence: Long, fromGroup: Boolean) {
        val path = if (fromGroup) "/rooms/$channel/discussions/$post/comments" else "/channels/$channel/posts/$post/comments"
        api.request("$path/read", json("sequence" to sequence))
    }

    internal suspend fun discussionSummaries(channel: String, posts: List<Long>): Map<Long, ChannelDiscussionSummary> {
        if (posts.isEmpty()) return emptyMap()
        return JSONArray(api.request("/channels/$channel/comments?posts=${posts.joinToString(",")}")).items {
            ChannelDiscussionSummary(it.getLong("post"), it.getLong("count"), it.getLong("unread"))
        }.associateBy(ChannelDiscussionSummary::post)
    }

    internal suspend fun channelComments(channel: String, post: Long, before: Long? = null, fromGroup: Boolean = false): DiscussionThread {
        val cursor = before?.let { "?before=$it" }.orEmpty()
        val path = if (fromGroup) "/rooms/$channel/discussions/$post/comments" else "/channels/$channel/posts/$post/comments"
        val value = JSONObject(api.request(path + cursor))
        return DiscussionThread(value.getString("groupId"), value.getLong("rootSequence"), value.getBoolean("closed"),
            value.getLong("count"), decodeMessages(value.getJSONArray("messages").toString()),
            value.getLong("readSequence"), value.getLong("unread"), value.optBoolean("canComment", !value.getBoolean("closed")), value.optBoolean("following"))
    }

    fun send(room: String, text: String, replyTo: Long?, file: SelectedAttachment?, accepted: () -> Unit) = action {
        val key = api.session?.outboxKey ?: return@action
        if ((text.isBlank() && file == null) || text.length > 4000) return@action
        val bytes = file?.let {
            withContext(Dispatchers.IO) {
                files.openInputStream(it.uri)?.use { input -> input.readLimitedAttachment(strings.resources) }
                    ?: throw IllegalArgumentException(strings.getString(R.string.error_attachment_unreadable))
            }
        }
        outbox.enqueue(key, PendingMessage(UUID.randomUUID().toString(), room, text, replyTo, fileName = file?.name), bytes)
        showOutbox(key)
        accepted()
        scope.launch { refresh() }
    }

    suspend fun selectedAttachment(uri: Uri): SelectedAttachment = withContext(Dispatchers.IO) {
        val name = files.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)
            ?.use { if (it.moveToFirst()) it.getString(0) else null } ?: "attachment"
        val safe = name.substringAfterLast('/').substringAfterLast('\\').filterNot(Char::isISOControl).take(180)
        SelectedAttachment(uri, safe.ifBlank { "attachment" })
    }

    suspend fun readAttachment(room: String, sequence: Long) = api.download(room, sequence, strings.resources)

    suspend fun saveAttachment(room: String, sequence: Long, target: Uri) {
        val bytes = readAttachment(room, sequence)
        withContext(Dispatchers.IO) {
            files.openOutputStream(target, "wt")?.use { it.write(bytes) }
                ?: throw IOException(strings.getString(R.string.error_write_download))
        }
    }

    fun retry(message: PendingMessage) = action {
        val key = api.session?.outboxKey ?: return@action
        outbox.failure(key, message.id, null)
        showOutbox(key)
        scope.launch { refresh() }
    }

    fun discard(message: PendingMessage) = action {
        val key = api.session?.outboxKey ?: return@action
        outbox.remove(key, message.id)
        showOutbox(key)
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

    fun openSaved() = action {
        val id = JSONObject(api.request("/saved", JSONObject())).getString("id")
        state.update { it.copy(selected = id, messages = emptyList(), focus = null, timelineRevision = null, openWork = null) }
        load()
    }

    fun editMessage(room: String, sequence: Long, text: String, done: () -> Unit) = action {
        api.request("/rooms/$room/messages/$sequence/edit", json("text" to text))
        done()
        load()
    }

    fun deleteMessage(room: String, sequence: Long, done: () -> Unit) = action {
        api.request("/rooms/$room/messages/$sequence/delete", JSONObject())
        done()
        load()
    }

    fun pinMessage(room: Room, sequence: Long) = action {
        api.request("/rooms/${room.id}/messages/$sequence/pin", json("value" to (room.pinnedMessage != sequence)))
        load()
    }

    fun forwardMessage(room: String, sequence: Long, target: String?, request: String, done: () -> Unit) = action {
        val destination = target ?: JSONObject(api.request("/saved", JSONObject())).getString("id")
        api.request("/rooms/$destination/forward", json("roomId" to room, "sequence" to sequence, "clientId" to request))
        done()
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
        try {
            serial.withLock {
                val key = api.session?.outboxKey ?: return@withLock
                showOutbox(key)
                drainOutbox(key)
                load(force)
            }
        }
        catch (e: Exception) { failed(e) }
    }

    private suspend fun showOutbox(key: String) {
        val entries = outbox.entries(key)
        state.update { it.copy(outbox = entries) }
    }

    private suspend fun restore(key: String) {
        runCatching {
            val saved = outbox.snapshot(key, "workspace")?.let(::JSONObject) ?: return
            val settings = saved.getJSONObject("settings")
            val rooms = decodeRooms(saved.getJSONArray("rooms"))
            val queued = outbox.entries(key)
            state.update {
                it.copy(username = settings.optString("username"), rooms = rooms, settings = settings,
                    outbox = queued, resuming = false)
            }
        }
    }

    private suspend fun drainOutbox(key: String) {
        val queued = state.value.outbox.asSequence().filter { it.failure == null }.take(16)
        for (message in queued) {
            try {
                val attachmentId = message.fileName?.let { name ->
                    val bytes = outbox.attachment(key, message.id)
                    var lastPercent = -1
                    api.upload(message.id, name, bytes) { percent ->
                        if (lastPercent != percent) {
                            lastPercent = percent
                            state.update { it.copy(upload = UploadProgress(message.id, percent)) }
                        }
                    }
                }
                api.request("/rooms/${message.room}/messages",
                    json("clientId" to message.id, "text" to message.text, "replyTo" to message.replyTo,
                        "attachmentId" to attachmentId))
                outbox.remove(key, message.id)
            } catch (e: ChatRequestException) {
                if (e.status == 401 || e.status == 429 || e.status >= 500) throw e
                outbox.failure(key, message.id, e.status)
            } finally {
                if (message.fileName != null) state.update { it.copy(upload = null) }
            }
        }
    }

    private suspend fun load(force: Boolean = false) {
        val settings = JSONObject(api.request("/settings"))
        val rawRooms = JSONArray(api.request("/rooms"))
        val rooms = decodeRooms(rawRooms)
        val group = memberRoom?.takeIf { id -> rooms.any { it.id == id } }
        val members = group?.let { id ->
            try {
                JSONArray(api.request("/rooms/$id/members")).items {
                    ChatMember(it.getString("name"), it.getString("displayName"), it.getBoolean("robot"), it.optBoolean("owner"), it.optBoolean("administrator"), it.optBoolean("silenced"))
                }
            } catch (error: ChatRequestException) {
                if (error.status != 404) throw error
                emptyList()
            }
        }
        val friends = if (friendsOpen) JSONArray(api.request("/friends")).items {
            ChatFriend(it.getString("id"), it.getString("username"), it.getString("displayName"), it.getString("state"))
        } else null
        val directory = if (directoryOpen) JSONArray(api.request("/channels")).items {
            ChatChannel(it.getString("id"), it.getString("title"), it.getBoolean("subscribed"), it.getBoolean("owner"))
        } else null
        val key = api.session?.outboxKey ?: return
        val revoked = state.value.rooms.map(Room::id).toSet() - rooms.map(Room::id).toSet()
        if (revoked.isNotEmpty()) outbox.forgetRooms(key, revoked)
        outbox.saveSnapshot(key, "workspace", json("settings" to settings, "rooms" to rawRooms).toString())
        val view = state.value
        val room = rooms.firstOrNull { it.id == view.selected }
        var history = if (room == null) emptyList() else view.messages
        if (room != null && foreground && history.isNotEmpty() && view.timelineRevision != room.revision) {
            val first = history.first().sequence
            val last = history.last().sequence
            var cursor = first - 1
            val updated = mutableListOf<ChatMessage>()
            while (cursor < last) {
                val batch = messages("/rooms/${room.id}/messages?after=$cursor")
                if (batch.isEmpty()) break
                updated.addAll(batch.takeWhile { it.sequence <= last })
                cursor = batch.last().sequence
            }
            history = updated
        }
        if (room != null && foreground && view.focus == null) {
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
            val cached = JSONArray()
            for (message in history.takeLast(50)) cached.put(json(
                "sequence" to message.sequence, "clientId" to message.clientId, "sender" to message.sender,
                "text" to message.text, "deleted" to message.deleted, "sentAt" to message.sentAt, "replyTo" to message.replyTo,
                "attachment" to message.attachment?.json(),
                "card" to message.card?.json(),
                "edited" to message.edited, "forwardedFrom" to message.forwardedFrom, "discussionRoot" to message.discussionRoot,
            ))
            outbox.saveSnapshot(key, room.id, cached.toString())
            val seen = history.lastOrNull()?.sequence ?: 0
            if (seen > room.read && state.value.selected == room.id && foreground && timelineVisible)
                api.request("/rooms/${room.id}/read", json("sequence" to seen))
        }
        val queued = outbox.entries(key)
        state.update {
            val selected = it.selected.takeIf { id -> rooms.any { room -> room.id == id } }
            val timeline = when {
                selected == null -> emptyList()
                selected == view.selected && it.focus == view.focus -> history
                else -> it.messages
            }
            it.copy(username = settings.optString("username"), settings = settings, rooms = rooms,
                selected = selected, messages = timeline, focus = it.focus.takeIf { selected != null }, resuming = false,
                timelineRevision = if (selected == view.selected && it.focus == view.focus && foreground) room?.revision else it.timelineRevision,
                friends = friends ?: it.friends,
                members = members.orEmpty(), membersRoom = group,
                channels = directory ?: it.channels, channelsLoaded = directory != null || it.channelsLoaded,
                connected = true, outbox = queued, error = if (it.error == "Offline") null else it.error)
        }
    }

    private fun decodeRooms(rooms: JSONArray) = rooms.items {
        Room(it.getString("id"), it.getString("title"), it.getString("type"), it.getLong("latest"),
            it.getLong("unread"), it.getLong("read"), it.getBoolean("muted"), it.getBoolean("archived"),
            it.getBoolean("canPost"), it.optString("preview"), it.optLong("revision"),
            it.optLong("pinnedMessage").takeIf { sequence -> sequence > 0 }, it.stringOrNull("parentId"), it.optBoolean("canManage"),
            it.optBoolean("previewDeleted"), it.optInt("channelRights"), it.optBoolean("signPosts"), it.optBoolean("canModerate"))
    }

    private suspend fun messages(path: String) = decodeMessages(api.request(path))

    private fun decodeMessages(raw: String) = JSONArray(raw).items(::decodeMessage)

    private fun decodeMessage(it: JSONObject) =
        ChatMessage(it.getLong("sequence"), it.getString("clientId"), it.getString("sender"), it.optString("text"),
            it.getBoolean("deleted"), it.optString("sentAt"), it.optLong("replyTo").takeIf { sequence -> sequence > 0 },
            it.optJSONObject("attachment")?.attachment(), it.optJSONObject("card")?.let(BotCard::decode),
            it.optBoolean("edited"), it.stringOrNull("forwardedFrom"), it.optBoolean("discussionRoot"))

    private fun attach() {
        if (foreground || listening) realtime.connect()
    }

    private suspend fun failed(error: Exception) {
        if (error is ChatRequestException && error.status == 401) {
            val key = api.session?.outboxKey
            api.clear()
            realtime.disconnect()
            push.sessionChanged()
            state.value = ChatState(error = strings.getString(R.string.error_session_expired))
            if (key != null) outbox.clear(key)
        } else {
            val message = when (error) {
                is ChatRequestException -> when (error.status) {
                    400 -> (runCatching { JSONTokener(error.body).nextValue() }.getOrNull() as? String)
                        ?.take(240) ?: strings.getString(R.string.error_check_input)
                    403 -> strings.getString(R.string.error_forbidden)
                    else -> strings.getString(R.string.error_request_failed, error.status)
                }
                is IllegalArgumentException -> error.message
                else -> "Offline"
            }
            state.update { it.copy(resuming = false, connected = error is ChatRequestException, error = message) }
        }
    }
}
