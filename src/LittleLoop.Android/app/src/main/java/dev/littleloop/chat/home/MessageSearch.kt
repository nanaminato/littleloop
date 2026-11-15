package dev.littleloop.chat

import androidx.compose.foundation.clickable
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
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

@Composable
internal fun ColumnScope.MessageSearch(chat: ChatRepository, rooms: List<Room>, room: String?, opened: () -> Unit) {
    var query by rememberSaveable(room) { mutableStateOf("") }
    val term = query.trim()
    var hits by remember(term) { mutableStateOf<List<SearchHit>>(emptyList()) }
    var offline by remember(term) { mutableStateOf(false) }
    var more by remember(term) { mutableStateOf(false) }
    var offset by remember(term) { mutableIntStateOf(0) }
    var nextOffset by remember(term) { mutableIntStateOf(0) }
    var refresh by remember(term) { mutableIntStateOf(0) }
    var loading by remember(term) { mutableStateOf(false) }
    var jumping by remember(term) { mutableStateOf(false) }
    var error by remember(term) { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()
    val keyboard = LocalSoftwareKeyboardController.current
    val unavailable = stringResource(R.string.search_unavailable)
    val gone = stringResource(R.string.search_gone)
    val openFailed = stringResource(R.string.search_open_failed)

    LaunchedEffect(chat, room, term, offset, refresh) {
        if (term.isEmpty()) return@LaunchedEffect
        loading = true
        error = null
        try {
            if (offset == 0) delay(250)
            var page = chat.search(term, room, offset)
            val reset = offset == 0 || page.offline != offline
            if (offset != 0 && reset) page = chat.search(term, room, 0)
            hits = ((if (reset) emptyList() else hits) + page.hits).distinctBy { it.room to it.message.sequence }
            nextOffset = (if (reset) 0 else offset) + page.hits.size
            offline = page.offline
            more = page.hasMore
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (_: Exception) {
            error = unavailable
        } finally {
            loading = false
        }
    }

    val roomTitles = remember(rooms) { rooms.associate { it.id to it.title } }
    val visibleHits = hits.filter { it.room in roomTitles }

    OutlinedTextField(
        query, { if (it.length <= 100) query = it },
        Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 10.dp),
        singleLine = true,
        enabled = !jumping,
        placeholder = { Text(stringResource(if (room == null) R.string.search_all else R.string.search_in)) },
        leadingIcon = { Text("⌕") },
        shape = MaterialTheme.shapes.extraLarge,
    )
    if (offline) {
        Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(stringResource(R.string.search_offline), Modifier.weight(1f), style = MaterialTheme.typography.bodySmall)
            TextButton(onClick = { offset = 0; refresh++ }, enabled = !loading && !jumping) { Text(stringResource(R.string.search_again)) }
        }
    }
    if (loading || jumping) LinearProgressIndicator(Modifier.fillMaxWidth())
    error?.let { Text(it, Modifier.padding(horizontal = 16.dp, vertical = 6.dp), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
    if (term.isNotEmpty() && !loading && error == null && visibleHits.isEmpty()) {
        EmptyState("⌕", stringResource(R.string.search_empty))
    }
    LazyColumn(Modifier.weight(1f), contentPadding = androidx.compose.foundation.layout.PaddingValues(bottom = 16.dp)) {
        items(visibleHits, key = { "${it.room}:${it.message.sequence}" }) { hit ->
            val message = hit.message
            val title = roomTitles[hit.room]
            Row(
                Modifier.fillMaxWidth().clickable(enabled = !jumping) {
                    keyboard?.hide()
                    jumping = true
                    error = null
                    scope.launch {
                        try {
                            if (chat.jump(hit)) opened()
                            else error = gone
                        } catch (cancelled: CancellationException) {
                            throw cancelled
                        } catch (_: Exception) {
                            error = openFailed
                        } finally {
                            jumping = false
                        }
                    }
                }.padding(horizontal = 16.dp, vertical = 10.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                RoomAvatar(title.orEmpty(), "direct", 40.dp)
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    val subtitle = listOfNotNull(title, message.sender).joinToString(" · ")
                    Text(subtitle, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.primary)
                    if (message.text.isNotBlank()) Text(message.text, maxLines = 3, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodyMedium)
                    message.attachment?.let { Text(it.name, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodySmall) }
                }
            }
            HorizontalDivider(Modifier.padding(start = 68.dp))
        }
        if (more) item {
            TextButton(
                onClick = { offset = nextOffset; refresh++ },
                enabled = !loading && !jumping,
                modifier = Modifier.fillMaxWidth(),
            ) { Text(stringResource(R.string.search_more)) }
        }
    }
}
