package dev.littleloop.chat

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp

@Composable
internal fun ColumnScope.ChannelDirectory(state: ChatState, subscribe: (ChatChannel) -> Unit, open: (String) -> Unit) {
    var query by rememberSaveable { mutableStateOf("") }
    OutlinedTextField(query, { query = it }, Modifier.fillMaxWidth().padding(12.dp), singleLine = true,
        label = { Text("搜索频道") })
    if (!state.connected) Text("正在重新连接", Modifier.padding(horizontal = 12.dp))
    state.error?.takeUnless { it == "Offline" }?.let { Text(it, Modifier.padding(12.dp), color = MaterialTheme.colorScheme.error) }
    if (!state.channelsLoaded) {
        if (state.connected) LinearProgressIndicator(Modifier.fillMaxWidth())
        return
    }
    val channels = state.channels.filter { it.title.contains(query, ignoreCase = true) }
    if (channels.isEmpty()) Text("没有匹配的频道", Modifier.padding(12.dp))
    val unread = remember(state.rooms) { state.rooms.associate { it.id to it.unread } }
    LazyColumn(Modifier.weight(1f)) {
        items(channels, key = ChatChannel::id) { channel ->
            Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 6.dp),
                verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(channel.title, maxLines = 2, overflow = TextOverflow.Ellipsis)
                    val status = when {
                        channel.owner -> "发布者"
                        channel.subscribed -> "已订阅"
                        else -> "未订阅"
                    }
                    val count = unread[channel.id] ?: 0
                    Text(if (count == 0L) status else "$status · $count 条未读",
                        style = MaterialTheme.typography.labelMedium)
                }
                if (channel.subscribed) TextButton(onClick = { open(channel.id) }) { Text("打开频道") }
                if (!channel.owner) TextButton(onClick = { subscribe(channel) }, enabled = !state.busy && state.connected) {
                    Text(if (channel.subscribed) "取消订阅" else "订阅频道")
                }
            }
            HorizontalDivider()
        }
    }
}

@Composable
internal fun CreateChatSpace(topic: Boolean, state: ChatState, dismiss: () -> Unit, create: (String) -> Unit) {
    var title by rememberSaveable(topic) { mutableStateOf("") }
    AlertDialog(onDismissRequest = { if (!state.busy) dismiss() },
        title = { Text(if (topic) "新建话题" else "新建频道") },
        text = {
            Column {
                OutlinedTextField(title, { if (it.length <= 100) title = it }, Modifier.fillMaxWidth(),
                    label = { Text(if (topic) "话题名称" else "频道名称") },
                    singleLine = true, enabled = !state.busy)
                state.error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
            }
        },
        confirmButton = {
            TextButton(onClick = { create(title) }, enabled = state.connected && !state.busy && title.isNotBlank()) {
                Text(if (topic) "创建话题" else "创建频道")
            }
        },
        dismissButton = { TextButton(onClick = dismiss, enabled = !state.busy) { Text("取消") } })
}

internal fun roomTypeLabel(type: String) = when (type) {
    "direct" -> "私聊"
    "group" -> "群聊"
    "topic" -> "群话题"
    "channel" -> "频道"
    "saved" -> "个人收藏"
    else -> "历史"
}
