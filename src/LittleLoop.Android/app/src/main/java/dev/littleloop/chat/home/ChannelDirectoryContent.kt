package dev.littleloop.chat


import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp

@Composable
internal fun ColumnScope.ChannelDirectoryContent(
    state: ChatState, query: String, channels: List<ChatChannel>, onQuery: (String) -> Unit,
    subscribe: (ChatChannel) -> Unit, open: (String) -> Unit,
) {
    OutlinedTextField(
        query, onQuery, Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
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
                    if (channel.directoryPermissions().open) {
                        TextButton(onClick = { open(channel.id) }) { Text(stringResource(R.string.channels_open)) }
                    }
                    if (channel.directoryPermissions().subscribe) {
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
