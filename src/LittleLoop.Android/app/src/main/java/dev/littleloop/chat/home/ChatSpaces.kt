package dev.littleloop.chat

import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.runtime.Composable

@Composable
internal fun ColumnScope.ChannelDirectory(chat: ChatRepository, state: ChatState, open: (String) -> Unit) {
    val controller = rememberChannelDirectoryController(chat, state, open)
    ChannelDirectoryContent(state, controller.query, controller.channels, controller::query,
        { controller.subscribe(it.id) }, controller::open)
}

@Composable
internal fun CreateRoom(chat: ChatRepository, state: ChatState, dismiss: () -> Unit) {
    val controller = rememberCreationController(chat, state, CreationKind.Direct, null, dismiss)
    val draft = controller.draft
    CreateRoomDialog(draft.kind == CreationKind.Group, draft.title, draft.members, state.error, controller.canSubmit,
        controller::group, controller::title, controller::members, dismiss, controller::submit)
}

@Composable
internal fun CreateChatSpace(chat: ChatRepository, state: ChatState, parent: String?, dismiss: () -> Unit, created: () -> Unit) {
    val controller = rememberCreationController(chat, state,
        if (parent == null) CreationKind.Channel else CreationKind.Topic, parent, created)
    CreateSpaceDialog(parent != null, controller.draft.title, state.busy, state.error, controller.canSubmit,
        controller::title, dismiss, controller::submit)
}
