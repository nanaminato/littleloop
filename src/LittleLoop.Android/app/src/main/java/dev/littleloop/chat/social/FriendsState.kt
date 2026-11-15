package dev.littleloop.chat

import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable

data class ChatFriend(val id: String, val username: String, val displayName: String, val state: String)

@Stable
internal class FriendsController(
    private val chat: ChatRepository, private val state: State<ChatState>,
    private val draft: MutableState<String>, private val openChat: State<() -> Unit>,
) {
    val username get() = draft.value
    val enabled get() = state.value.connected && !state.value.busy
    fun username(value: String) { if (value.length <= 32) draft.value = value }
    fun request() {
        if (!enabled || username.isBlank()) return
        val submitted = username
        chat.requestFriend(submitted) { if (draft.value == submitted) draft.value = "" }
    }
    fun respond(id: String, accepted: Boolean) {
        val friend = state.value.friends.firstOrNull { it.id == id } ?: return
        if (enabled && friend.permissions().respond) chat.respondFriend(friend, accepted)
    }
    fun open(id: String) {
        val friend = state.value.friends.firstOrNull { it.id == id } ?: return
        if (enabled && friend.permissions().openChat) chat.create("direct", "", friend.username, openChat.value)
    }
}

@Composable
internal fun rememberFriendsController(chat: ChatRepository, state: ChatState, openChat: () -> Unit): FriendsController = key(state.username) {
    val draft = rememberSaveable { mutableStateOf("") }
    val currentState = rememberUpdatedState(state)
    val onOpen = rememberUpdatedState(openChat)
    remember(chat) { FriendsController(chat, currentState, draft, onOpen) }
}
