package dev.littleloop.chat

import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable

@Stable
internal class ChannelDirectoryController(
    private val chat: ChatRepository, private val state: State<ChatState>,
    private val draft: MutableState<String>, private val onOpen: State<(String) -> Unit>,
) {
    val query get() = draft.value
    val channels get() = state.value.channels.filter { it.title.contains(query, ignoreCase = true) }
    fun query(value: String) { draft.value = value }
    fun open(id: String) {
        val channel = state.value.channels.firstOrNull { it.id == id } ?: return
        if (channel.directoryPermissions().open) onOpen.value(id)
    }
    fun subscribe(id: String) {
        val channel = state.value.channels.firstOrNull { it.id == id } ?: return
        if (state.value.connected && !state.value.busy && channel.directoryPermissions().subscribe) chat.subscribe(channel)
    }
}

@Composable
internal fun rememberChannelDirectoryController(chat: ChatRepository, state: ChatState, open: (String) -> Unit): ChannelDirectoryController = key(state.username) {
    val query = rememberSaveable { mutableStateOf("") }
    val current = rememberUpdatedState(state)
    val onOpen = rememberUpdatedState(open)
    remember(chat) { ChannelDirectoryController(chat, current, query, onOpen) }
}
