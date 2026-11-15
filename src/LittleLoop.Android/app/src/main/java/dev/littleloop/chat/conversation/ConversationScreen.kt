package dev.littleloop.chat

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp

@Composable
internal fun ConversationScreen(chat: ChatRepository, state: ChatState, room: Room,
    onBack: () -> Unit, onTopics: () -> Unit, onMembers: () -> Unit, onSearch: () -> Unit) {
    val controller = rememberConversationController(chat, state, room)
    val ui = controller.ui
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument(), controller::selectAttachment)
    val bySequence = remember(state.messages) { state.messages.associateBy(ChatMessage::sequence) }
    Column(Modifier.fillMaxSize()) {
        ConversationHeader(room, state.connected,
            state.rooms.filter { it.type == "topic" && it.parentId == room.id }.sumOf(Room::unread),
            controller.robot, controller.installing, onBack, onSearch,
            onMute = { chat.roomPreference(room) }, onChannelSettings = { ui.channelSettings = true },
            onTopics = onTopics, onMembers = onMembers, onAsk = { ui.asking = true }, onInstall = controller::installBot)
        if (state.focus != null) {
            Surface(color = MaterialTheme.colorScheme.primaryContainer, tonalElevation = 0.dp) {
                Row(Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text(stringResource(R.string.conv_focused), Modifier.weight(1f), style = MaterialTheme.typography.bodySmall)
                    TextButton(onClick = { chat.select(room.id) }) { Text(stringResource(R.string.conv_latest)) }
                }
            }
        }

        room.pinnedMessage?.let { sequence ->
            Surface(color = MaterialTheme.colorScheme.secondaryContainer, tonalElevation = 0.dp) {
                TextButton(onClick = { chat.openPinned(room) }, modifier = Modifier.fillMaxWidth()) {
                    Text(stringResource(R.string.conv_pinned, sequence.toInt()))
                }
            }
        }

        if (!state.connected) {
            Text(
                stringResource(R.string.conv_offline),
                Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 6.dp),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        state.error?.takeUnless { it == "Offline" }?.let {
            Text(it, Modifier.padding(horizontal = 14.dp, vertical = 4.dp), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
        }
        controller.fileError?.let { Text(it, Modifier.padding(horizontal = 14.dp, vertical = 4.dp), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
        controller.botError?.let { Text(it, Modifier.padding(horizontal = 14.dp, vertical = 4.dp), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
        ui.operationResult?.let { Text(it, Modifier.padding(horizontal = 14.dp, vertical = 4.dp), style = MaterialTheme.typography.bodySmall) }

        ConversationTimeline(state, room, ui.menu, controller.discussionSummaries, Modifier.weight(1f),
            controller::dispatch, chat::older, chat::retry, chat::discard)
        ConversationComposer(room, state.busy, ui.draft, ui.replyTo,
            bySequence[ui.replyTo]?.text?.ifBlank { null }, ui.file,
            onDraft = { ui.draft = it }, onClearReply = { ui.replyTo = null },
            onRemoveAttachment = { ui.file = null }, onPickAttachment = { picker.launch(arrayOf("*/*")) },
            onSend = controller::send, onChannelSettings = { ui.channelSettings = true })
    }
    ConversationDialogs(chat, state, room, ui, controller.robot)
}
