package dev.littleloop.chat

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import java.util.UUID

internal enum class MessageOperation { Edit, Delete, Forward, Pin, Save }

@Composable
internal fun MessageMenu(own: Boolean, canPin: Boolean, pinned: Boolean, enabled: Boolean, dismiss: () -> Unit,
                         select: (MessageOperation) -> Unit) {
    DropdownMenu(expanded = true, onDismissRequest = dismiss) {
        if (own) ActionItem("编辑", MessageOperation.Edit, enabled, select)
        ActionItem("转发", MessageOperation.Forward, enabled, select)
        if (canPin) ActionItem(if (pinned) "取消置顶消息" else "置顶消息", MessageOperation.Pin, enabled, select)
        ActionItem("添加到个人收藏", MessageOperation.Save, enabled, select)
        if (own) ActionItem("删除", MessageOperation.Delete, enabled, select)
        if (!enabled) DropdownMenuItem(text = { Text("联网后可操作") }, onClick = {}, enabled = false)
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
    val online = state.connected && !state.busy && !message.deleted
    val targets = state.rooms.filter { it.canPost && it.type !in setOf("saved", "legacy") && !it.archived }
    val label = when (operation) {
        MessageOperation.Edit -> "编辑消息"
        MessageOperation.Delete -> "删除消息"
        MessageOperation.Forward -> "转发消息"
        MessageOperation.Save -> "添加到个人收藏"
        MessageOperation.Pin -> return
    }
    AlertDialog(onDismissRequest = { if (!state.busy) dismiss() }, title = { Text(label) }, text = {
        Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            when (operation) {
                MessageOperation.Edit -> OutlinedTextField(draft, { if (it.length <= 4000) draft = it },
                    modifier = Modifier.fillMaxWidth(), minLines = 3, maxLines = 8,
                    label = { Text("编辑内容") }, enabled = !state.busy)
                MessageOperation.Delete -> Text("删除后，此消息将在会话中显示为已删除。")
                MessageOperation.Save -> Text("将这条消息保存到只有你能访问的会话。")
                MessageOperation.Forward -> LazyColumn(Modifier.heightIn(max = 320.dp)) {
                    if (targets.isEmpty()) item { Text("没有可转发的会话") }
                    items(targets, key = Room::id) { candidate ->
                        TextButton(
                            onClick = { target = candidate.id }, enabled = !state.busy,
                            modifier = Modifier.fillMaxWidth(),
                        ) { Text(if (target == candidate.id) "✓ ${candidate.title}" else candidate.title) }
                    }
                }
                MessageOperation.Pin -> Unit
            }
            if (message.deleted) Text("消息已删除", color = MaterialTheme.colorScheme.error)
            else state.error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
        }
    }, confirmButton = {
        when (operation) {
            MessageOperation.Edit -> TextButton(onClick = {
                chat.editMessage(room.id, message.sequence, draft) { done("已保存修改") }
            }, enabled = online && draft != message.text && (draft.isNotBlank() || message.attachment != null)) { Text("保存修改") }
            MessageOperation.Delete -> TextButton(onClick = {
                chat.deleteMessage(room.id, message.sequence) { done("已删除消息") }
            }, enabled = online) { Text("确认删除") }
            MessageOperation.Forward -> TextButton(onClick = {
                target?.let { to -> chat.forwardMessage(room.id, message.sequence, to, id) { done("已转发") } }
            }, enabled = online && targets.any { it.id == target }) { Text("确认转发") }
            MessageOperation.Save -> TextButton(onClick = {
                chat.forwardMessage(room.id, message.sequence, null, id) { done("已添加到个人收藏") }
            }, enabled = online) { Text("保存到收藏") }
            MessageOperation.Pin -> Unit
        }
    }, dismissButton = { TextButton(onClick = dismiss, enabled = !state.busy) { Text("取消") } })
}
