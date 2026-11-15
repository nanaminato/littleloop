package dev.littleloop.chat

import android.content.Context
import android.net.Uri
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.listSaver
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.platform.LocalContext
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

internal data class ChannelDiscussionSummary(val post: Long, val count: Long, val unread: Long)

internal sealed interface ConversationAction {
    data class Reply(val sequence: Long) : ConversationAction
    data class Menu(val sequence: Long) : ConversationAction
    data object DismissMenu : ConversationAction
    data class Preview(val sequence: Long) : ConversationAction
    data class Details(val sequence: Long) : ConversationAction
    data class Comments(val sequence: Long) : ConversationAction
    data class Operate(val sequence: Long, val operation: MessageOperation) : ConversationAction
}

/** Only the same fields that previously used rememberSaveable survive recreation. */
@Stable
internal class ConversationUiState {
    var draft by mutableStateOf("")
    var replyTo by mutableStateOf<Long?>(null)
    var commentPost by mutableStateOf<Long?>(null)
    var preview by mutableStateOf<Long?>(null)
    var asking by mutableStateOf(false)
    var scheduleFrom by mutableStateOf<Long?>(null)
    var botDetails by mutableStateOf<Long?>(null)
    var file by mutableStateOf<SelectedAttachment?>(null)
    var menu by mutableStateOf<Long?>(null)
    var channelSettings by mutableStateOf(false)
    var messageOperation by mutableStateOf<Pair<Long, MessageOperation>?>(null)
    var operationResult by mutableStateOf<String?>(null)

    companion object {
        val Saver = listSaver<ConversationUiState, Any>(
            save = { listOf(it.draft, it.replyTo ?: -1L, it.commentPost ?: -1L, it.preview ?: -1L,
                it.asking, it.scheduleFrom ?: -1L, it.botDetails ?: -1L) },
            restore = { values -> ConversationUiState().apply {
                draft = values[0] as String
                replyTo = (values[1] as Long).takeIf { it >= 0 }
                commentPost = (values[2] as Long).takeIf { it >= 0 }
                preview = (values[3] as Long).takeIf { it >= 0 }
                asking = values[4] as Boolean
                scheduleFrom = (values[5] as Long).takeIf { it >= 0 }
                botDetails = (values[6] as Long).takeIf { it >= 0 }
            } },
        )
    }
}

@Stable
internal class ConversationController(
    private val chat: ChatRepository, val ui: ConversationUiState,
    private val room: State<Room>, private val state: State<ChatState>,
    private val scope: CoroutineScope, private val context: Context,
) {
    var robot by mutableStateOf<BotRoom?>(null)
        private set
    var botError by mutableStateOf<String?>(null)
        private set
    var fileError by mutableStateOf<String?>(null)
        private set
    var installing by mutableStateOf(false)
        private set
    var discussionSummaries by mutableStateOf<Map<Long, ChannelDiscussionSummary>>(emptyMap())
        private set

    suspend fun loadBot() {
        val result = botAttempt(context) { chat.bot.room(room.value.id) }
        if (result is BotResult.Success) robot = result.value
    }

    suspend fun observeDiscussions(posts: List<Long>) {
        while (posts.isNotEmpty()) {
            try { discussionSummaries = chat.discussionSummaries(room.value.id, posts) }
            catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { discussionSummaries = emptyMap() }
            delay(5000)
        }
    }

    fun selectAttachment(uri: Uri?) {
        if (uri == null) return
        scope.launch {
            try { ui.file = chat.selectedAttachment(uri); fileError = null }
            catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { fileError = context.getString(R.string.attach_picker_failed) }
        }
    }

    fun installBot() {
        if (installing) return
        installing = true
        scope.launch {
            try {
                when (val result = botAttempt(context) { chat.bot.install(room.value.id); chat.bot.room(room.value.id) }) {
                    is BotResult.Success -> { robot = result.value; botError = null }
                    is BotResult.Error -> botError = result.message
                }
            } finally { installing = false }
        }
    }

    fun send() {
        if (!room.value.canPost || state.value.busy) return
        val draft = ui.draft
        val reply = ui.replyTo
        val file = ui.file
        chat.send(room.value.id, draft, reply, file) {
            if (ui.draft == draft) ui.draft = ""
            if (ui.replyTo == reply) ui.replyTo = null
            if (ui.file == file) ui.file = null
        }
    }

    fun dispatch(action: ConversationAction) {
        when (action) {
            is ConversationAction.Reply -> ui.replyTo = action.sequence
            is ConversationAction.Menu -> { ui.menu = action.sequence; ui.operationResult = null }
            ConversationAction.DismissMenu -> ui.menu = null
            is ConversationAction.Preview -> ui.preview = action.sequence
            is ConversationAction.Details -> ui.botDetails = action.sequence
            is ConversationAction.Comments -> ui.commentPost = action.sequence
            is ConversationAction.Operate -> {
                ui.menu = null
                val message = state.value.messages.firstOrNull { it.sequence == action.sequence } ?: return
                if (!room.value.messagePermissions(message, state.value.username).allows(action.operation)) return
                if (action.operation == MessageOperation.Pin) chat.pinMessage(room.value, message.sequence)
                else ui.messageOperation = message.sequence to action.operation
            }
        }
    }
}

@Composable
internal fun rememberConversationController(chat: ChatRepository, state: ChatState, room: Room): ConversationController = key(room.id) {
    val ui = rememberSaveable(room.id, saver = ConversationUiState.Saver) { ConversationUiState() }
    val latestRoom = rememberUpdatedState(room)
    val latestState = rememberUpdatedState(state)
    val scope = rememberCoroutineScope()
    val context = LocalContext.current
    val controller = remember(chat, room.id, ui, context) { ConversationController(chat, ui, latestRoom, latestState, scope, context) }
    val posts = if (room.type == "channel") state.messages.filterNot(ChatMessage::deleted)
        .takeLast(100).map(ChatMessage::sequence) else emptyList()
    LaunchedEffect(controller, posts) { controller.observeDiscussions(posts) }
    LaunchedEffect(controller, state.connected) { if (state.connected) controller.loadBot() }
    LaunchedEffect(controller, state.openComments, room.type) {
        if (state.openComments != null && room.type == "channel") {
            ui.commentPost = state.openComments
            chat.commentLinkOpened()
        }
    }
    LaunchedEffect(controller, state.openWork, state.focus) {
        if (state.openWork != null && state.focus != null) {
            ui.botDetails = state.focus
            chat.workLinkOpened()
        }
    }
    controller
}
