package dev.littleloop.chat

import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AssistChip
import androidx.compose.material3.ExtendedFloatingActionButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
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
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp

@Composable
internal fun ChatHomeScreen(
    chat: ChatRepository,
    state: ChatState,
    onSearch: () -> Unit,
    onFriends: () -> Unit,
    onChannels: () -> Unit,
    onSettings: () -> Unit,
    onNewChat: () -> Unit,
) {
    var query by rememberSaveable { mutableStateOf("") }
    val rooms = state.rooms.filter {
        !it.archived && (it.type != "topic" || query.isNotBlank()) && it.title.contains(query, ignoreCase = true)
    }
    val newChat = stringResource(R.string.home_new_chat)
    Box(Modifier.fillMaxSize()) {
        Column(Modifier.fillMaxSize()) {
            Row(
                Modifier.fillMaxWidth().padding(start = 16.dp, end = 6.dp, top = 10.dp, bottom = 4.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                BrandMark(38.dp)
                Spacer(Modifier.width(10.dp))
                Column(Modifier.weight(1f)) {
                    Text(stringResource(R.string.app_name), maxLines = 1, overflow = TextOverflow.Ellipsis,
                        style = MaterialTheme.typography.titleLarge)
                    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                        Text(stringResource(R.string.app_chat_subtitle), style = MaterialTheme.typography.labelMedium,
                            color = MaterialTheme.colorScheme.onSurfaceVariant)
                        ConnectionDot(state.connected)
                        Text(stringResource(if (state.connected) R.string.status_connected else R.string.status_reconnecting),
                            maxLines = 1, overflow = TextOverflow.Ellipsis,
                            style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
                TextButton(onClick = onSettings) {
                    Text("⚙", style = MaterialTheme.typography.titleLarge)
                }
            }

            OutlinedTextField(
                query, { if (it.length <= 100) query = it },
                Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 6.dp),
                singleLine = true,
                placeholder = { Text(stringResource(R.string.home_search_hint)) },
                leadingIcon = { Text("⌕", color = MaterialTheme.colorScheme.onSurfaceVariant) },
                trailingIcon = {
                    if (query.isNotEmpty()) {
                        TextButton(onClick = { query = "" }) { Text("×") }
                    }
                },
                shape = MaterialTheme.shapes.extraLarge,
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
                keyboardActions = KeyboardActions(onSearch = { onSearch() }),
            )

            Row(
                Modifier.horizontalScroll(rememberScrollState()).padding(horizontal = 12.dp, vertical = 4.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                AssistChip(onClick = onSearch, label = { Text(stringResource(R.string.action_search)) }, leadingIcon = { Text("⌕") })
                AssistChip(onClick = { chat.openSaved() }, label = { Text(stringResource(R.string.home_saved)) }, leadingIcon = { Text("★") })
                AssistChip(onClick = onFriends, label = { Text(stringResource(R.string.home_friends)) }, leadingIcon = { Text("♧") })
                AssistChip(onClick = onChannels, label = { Text(stringResource(R.string.home_channels)) }, leadingIcon = { Text("◎") })
            }

            if (state.busy) LinearProgressIndicator(Modifier.fillMaxWidth())
            state.error?.takeUnless { it == "Offline" }?.let {
                Surface(
                    color = MaterialTheme.colorScheme.errorContainer,
                    shape = MaterialTheme.shapes.medium,
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 6.dp),
                ) {
                    Text(
                        it,
                        color = MaterialTheme.colorScheme.onErrorContainer,
                        style = MaterialTheme.typography.bodySmall,
                        modifier = Modifier.padding(horizontal = 12.dp, vertical = 8.dp),
                    )
                }
            }

            LazyColumn(
                Modifier.weight(1f).fillMaxWidth(),
                contentPadding = PaddingValues(top = 4.dp, bottom = 96.dp),
            ) {
                items(rooms, key = Room::id) { room ->
                    RoomRow(room, selected = room.id == state.selected) { chat.select(room.id) }
                }
                if (rooms.isEmpty() && query.isBlank()) item {
                    EmptyState("✦", stringResource(R.string.home_no_chats), stringResource(R.string.home_no_chats_hint))
                }
                if (rooms.isEmpty() && query.isNotBlank()) item {
                    EmptyState("⌕", stringResource(R.string.search_empty))
                }
            }
        }

        ExtendedFloatingActionButton(
            onClick = onNewChat,
            modifier = Modifier.align(Alignment.BottomEnd).padding(20.dp).semantics { contentDescription = newChat },
            icon = { Text("＋", style = MaterialTheme.typography.titleLarge) },
            text = { Text(newChat) },
            containerColor = MaterialTheme.colorScheme.primary,
            contentColor = MaterialTheme.colorScheme.onPrimary,
        )
    }
}
