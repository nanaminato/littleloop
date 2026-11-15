package dev.littleloop.chat

import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp

@Composable
internal fun ColumnScope.ChannelDirectory(state: ChatState, subscribe: (ChatChannel) -> Unit, open: (String) -> Unit) {
    var query by rememberSaveable { mutableStateOf("") }
    OutlinedTextField(
        query, { query = it }, Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
        singleLine = true,
        label = { Text(stringResource(R.string.channels_search)) },
        leadingIcon = { Text("⌕") },
        shape = MaterialTheme.shapes.extraLarge,
    )
    if (!state.connected) {
        Text(
            stringResource(R.string.status_reconnecting),
            Modifier.padding(horizontal = 16.dp, vertical = 2.dp),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
    state.error?.takeUnless { it == "Offline" }?.let {
        Text(it, Modifier.padding(horizontal = 16.dp, vertical = 4.dp), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
    }
    if (!state.channelsLoaded) {
        if (state.connected) LinearProgressIndicator(Modifier.fillMaxWidth())
        return
    }
    val channels = state.channels.filter { it.title.contains(query, ignoreCase = true) }
    if (channels.isEmpty()) {
        EmptyState("◎", stringResource(R.string.channels_empty))
    }
    val unread = state.rooms.associate { it.id to it.unread }
    LazyColumn(Modifier.weight(1f), contentPadding = androidx.compose.foundation.layout.PaddingValues(bottom = 16.dp)) {
        items(channels, key = ChatChannel::id) { channel ->
            Row(
                Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                RoomAvatar(channel.title, "channel", 46.dp)
                Column(Modifier.weight(1f)) {
                    Text(channel.title, maxLines = 2, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.titleMedium)
                    val status = when {
                        channel.owner -> stringResource(R.string.channels_publisher)
                        channel.subscribed -> stringResource(R.string.channels_subscribed)
                        else -> stringResource(R.string.channels_unsubscribed)
                    }
                    val count = unread[channel.id] ?: 0
                    Text(
                        if (count == 0L) status else "$status · " + stringResource(R.string.status_unread, count.toInt()),
                        style = MaterialTheme.typography.labelMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    if (channel.subscribed) {
                        TextButton(onClick = { open(channel.id) }) { Text(stringResource(R.string.channels_open)) }
                    }
                    if (!channel.owner) {
                        TextButton(onClick = { subscribe(channel) }, enabled = !state.busy && state.connected) {
                            Text(stringResource(if (channel.subscribed) R.string.channels_unsubscribe else R.string.channels_subscribe))
                        }
                    }
                }
            }
            HorizontalDivider(Modifier.padding(start = 74.dp))
        }
    }
}

@Composable
internal fun CreateChatSpace(topic: Boolean, state: ChatState, dismiss: () -> Unit, create: (String) -> Unit) {
    var title by rememberSaveable(topic) { mutableStateOf("") }
    AlertDialog(
        onDismissRequest = { if (!state.busy) dismiss() },
        title = { Text(stringResource(if (topic) R.string.topics_new else R.string.channels_new)) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedTextField(
                    title, { if (it.length <= 100) title = it }, Modifier.fillMaxWidth(),
                    label = { Text(stringResource(if (topic) R.string.topics_title else R.string.channels_name)) },
                    singleLine = true, enabled = !state.busy, shape = MaterialTheme.shapes.medium,
                )
                state.error?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
            }
        },
        confirmButton = {
            TextButton(onClick = { create(title) }, enabled = state.connected && !state.busy && title.isNotBlank()) {
                Text(stringResource(if (topic) R.string.topics_create else R.string.channels_create))
            }
        },
        dismissButton = { TextButton(onClick = dismiss, enabled = !state.busy) { Text(stringResource(R.string.action_cancel)) } },
    )
}
