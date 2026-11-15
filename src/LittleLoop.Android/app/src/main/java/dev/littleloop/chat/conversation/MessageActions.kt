package dev.littleloop.chat

import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import java.util.UUID

internal enum class MessageOperation { Edit, Delete, Forward, Pin, Save }

@Composable
internal fun MessageMenu(canEdit: Boolean, canDelete: Boolean, canPin: Boolean, pinned: Boolean, enabled: Boolean, dismiss: () -> Unit, reply: (() -> Unit)? = null,
                         select: (MessageOperation) -> Unit) {
    DropdownMenu(
        expanded = true,
        onDismissRequest = dismiss,
        containerColor = MaterialTheme.colorScheme.surface,
    ) {
        if (reply != null) DropdownMenuItem(text = { Text(stringResource(R.string.action_reply)) }, onClick = reply)
        if (canEdit) ActionItem(stringResource(R.string.action_edit), MessageOperation.Edit, enabled, select)
        ActionItem(stringResource(R.string.action_forward), MessageOperation.Forward, enabled, select)
        if (canPin) {
            ActionItem(
                stringResource(if (pinned) R.string.action_unpin else R.string.action_pin),
                MessageOperation.Pin,
                enabled,
                select,
            )
        }
        ActionItem(stringResource(R.string.message_save), MessageOperation.Save, enabled, select)
        if (canDelete) ActionItem(stringResource(R.string.action_delete), MessageOperation.Delete, enabled, select)
        if (!enabled) DropdownMenuItem(text = { Text(stringResource(R.string.message_offline_actions)) }, onClick = {}, enabled = false)
    }
}

@Composable
private fun ActionItem(label: String, operation: MessageOperation, enabled: Boolean, select: (MessageOperation) -> Unit) {
    DropdownMenuItem(text = { Text(label) }, onClick = { select(operation) }, enabled = enabled)
}

@Composable
internal fun MessageOperationDialog(chat: ChatRepository, state: ChatState, room: Room, message: ChatMessage,
                                    operation: MessageOperation, dismiss: () -> Unit, done: (String) -> Unit) {
    var draft by rememberSaveable(message.sequence) { mutableStateOf(message.text) }
    var target by rememberSaveable { mutableStateOf<String?>(null) }
    val id = rememberSaveable(room.id, message.sequence, operation.name, target) { UUID.randomUUID().toString() }
    val own = message.sender == state.username
    val allowed = when (operation) {
        MessageOperation.Edit -> if (room.type == "channel") room.channelRights and 2 != 0 else own
        MessageOperation.Delete -> if (room.type == "channel") room.channelRights and 4 != 0 else own || room.canModerate
        MessageOperation.Pin -> if (room.type == "channel") room.channelRights and 8 != 0 else room.canPost
        else -> true
    }
    val online = state.connected && !state.busy && !message.deleted && allowed
    val targets = state.rooms.filter { it.canPost && it.type !in setOf("saved", "legacy") && !it.archived }
    val label = when (operation) {
        MessageOperation.Edit -> stringResource(R.string.message_edit)
        MessageOperation.Delete -> stringResource(R.string.message_delete)
        MessageOperation.Forward -> stringResource(R.string.message_forward)
        MessageOperation.Save -> stringResource(R.string.message_save)
        MessageOperation.Pin -> return
    }
    val saved = stringResource(R.string.message_saved)
    val deleted = stringResource(R.string.message_deleted)
    val forwarded = stringResource(R.string.message_forwarded)
    val added = stringResource(R.string.message_added_saved)
    AlertDialog(
        onDismissRequest = { if (!state.busy) dismiss() },
        title = { Text(label) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                when (operation) {
                    MessageOperation.Edit -> OutlinedTextField(
                        draft, { if (it.length <= 4000) draft = it },
                        modifier = Modifier.fillMaxWidth(), minLines = 3, maxLines = 8,
                        label = { Text(stringResource(R.string.action_edit)) }, enabled = !state.busy,
                        shape = MaterialTheme.shapes.medium,
                    )
                    MessageOperation.Delete -> Text(stringResource(R.string.message_delete_note))
                    MessageOperation.Save -> Text(stringResource(R.string.message_save_note))
                    MessageOperation.Forward -> LazyColumn(Modifier.heightIn(max = 320.dp)) {
                        if (targets.isEmpty()) item { Text(stringResource(R.string.message_no_targets)) }
                        items(targets, key = Room::id) { candidate ->
                            TextButton(
                                onClick = { target = candidate.id }, enabled = !state.busy,
                                modifier = Modifier.fillMaxWidth(), shape = MaterialTheme.shapes.small,
                            ) { Text(if (target == candidate.id) "✓ ${candidate.title}" else candidate.title) }
                        }
                    }
                    MessageOperation.Pin -> Unit
                }
                if (message.deleted) Text(stringResource(R.string.conv_deleted), color = MaterialTheme.colorScheme.error)
                else state.error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
            }
        },
        confirmButton = {
            when (operation) {
                MessageOperation.Edit -> TextButton(onClick = {
                    chat.editMessage(room.id, message.sequence, draft) { done(saved) }
                }, enabled = online && draft != message.text && (draft.isNotBlank() || message.attachment != null)) {
                    Text(stringResource(R.string.action_save))
                }
                MessageOperation.Delete -> TextButton(onClick = {
                    chat.deleteMessage(room.id, message.sequence) { done(deleted) }
                }, enabled = online) { Text(stringResource(R.string.action_delete)) }
                MessageOperation.Forward -> TextButton(onClick = {
                    target?.let { to -> chat.forwardMessage(room.id, message.sequence, to, id) { done(forwarded) } }
                }, enabled = online && targets.any { it.id == target }) { Text(stringResource(R.string.action_forward)) }
                MessageOperation.Save -> TextButton(onClick = {
                    chat.forwardMessage(room.id, message.sequence, null, id) { done(added) }
                }, enabled = online) { Text(stringResource(R.string.action_save_to_saved)) }
                MessageOperation.Pin -> Unit
            }
        },
        dismissButton = { TextButton(onClick = dismiss, enabled = !state.busy) { Text(stringResource(R.string.action_cancel)) } },
    )
}
