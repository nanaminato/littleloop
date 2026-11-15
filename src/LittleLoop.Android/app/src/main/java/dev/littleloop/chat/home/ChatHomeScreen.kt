package dev.littleloop.chat

import androidx.compose.runtime.Composable

@Composable
internal fun ChatHomeScreen(
    chat: ChatRepository, state: ChatState, onSearch: () -> Unit, onFriends: () -> Unit,
    onChannels: () -> Unit, onSettings: () -> Unit, onNewChat: () -> Unit,
) {
    val controller = rememberChatHomeController(chat, state)
    ChatHomeContent(state, controller.query, controller.rooms, controller::query, controller::open,
        { chat.openSaved() }, onSearch, onFriends, onChannels, onSettings, onNewChat)
}
