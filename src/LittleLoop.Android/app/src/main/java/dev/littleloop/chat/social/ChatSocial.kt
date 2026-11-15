package dev.littleloop.chat

import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.runtime.Composable

@Composable
internal fun ColumnScope.FriendsPanel(chat: ChatRepository, state: ChatState, openChat: () -> Unit) {
    val controller = rememberFriendsController(chat, state, openChat)
    FriendsContent(state.friends, controller.username, controller.enabled, state.error,
        controller::username, controller::request, controller::respond, controller::open)
}

@Composable
internal fun ColumnScope.GroupMembersPanel(chat: ChatRepository, state: ChatState, room: Room) {
    val controller = rememberGroupMembersController(chat, state, room)
    GroupMembersContent(controller.ui.username, controller.members, controller.enabled, state.error,
        controller.permissions, controller::operations, controller::username, controller::invite, controller::select)
    GroupMemberDialogs(controller.ui, controller.pending, controller.permissions, controller.enabled,
        controller::dismiss, controller::confirm, controller::review, controller::respondJoin)
}
