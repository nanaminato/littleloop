package dev.littleloop.chat

import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable

internal fun List<Room>.homeRooms(query: String) = filter {
    !it.archived && (it.type != "topic" || query.isNotBlank()) && it.title.contains(query, ignoreCase = true)
}

internal fun List<Room>.topics(parent: String) = filter {
    it.parentId == parent && it.type == "topic" && !it.archived
}

@Stable
internal class ChatHomeController(
    private val chat: ChatRepository, private val state: State<ChatState>, private val draft: MutableState<String>,
) {
    val query get() = draft.value
    val rooms get() = state.value.rooms.homeRooms(query)
    fun query(value: String) { if (value.length <= 100) draft.value = value }
    fun open(id: String) { if (rooms.any { it.id == id }) chat.select(id) }
}

@Composable
internal fun rememberChatHomeController(chat: ChatRepository, state: ChatState): ChatHomeController = key(state.username) {
    val query = rememberSaveable { mutableStateOf("") }
    val current = rememberUpdatedState(state)
    remember(chat) { ChatHomeController(chat, current, query) }
}
