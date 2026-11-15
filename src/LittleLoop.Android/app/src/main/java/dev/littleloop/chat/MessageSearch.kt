package dev.littleloop.chat

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
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
            error = "搜索暂不可用，请重试"
        } finally {
            loading = false
        }
    }

    val roomTitles = remember(rooms) { rooms.associate { it.id to it.title } }
    val visibleHits = hits.filter { it.room in roomTitles }

    OutlinedTextField(query, { if (it.length <= 100) query = it },
        Modifier.fillMaxWidth().padding(12.dp), singleLine = true, enabled = !jumping,
        label = { Text(if (room == null) "搜索全部消息" else "搜索本会话") })
    if (offline) Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp)) {
        Text("离线 · 仅搜索本机缓存", Modifier.weight(1f))
        TextButton(onClick = { offset = 0; refresh++ }, enabled = !loading && !jumping) { Text("重新搜索") }
    }
    if (loading || jumping) LinearProgressIndicator(Modifier.fillMaxWidth())
    error?.let { Text(it, Modifier.padding(12.dp), color = MaterialTheme.colorScheme.error) }
    if (term.isNotEmpty() && !loading && error == null && visibleHits.isEmpty())
        Text("未找到消息", Modifier.padding(12.dp))
    LazyColumn(Modifier.weight(1f)) {
        items(visibleHits, key = { "${it.room}:${it.message.sequence}" }) { hit ->
            val message = hit.message
            val title = roomTitles[hit.room]
            Column(Modifier.fillMaxWidth().clickable(enabled = !jumping) {
                keyboard?.hide()
                jumping = true
                error = null
                scope.launch {
                    try {
                        if (chat.jump(hit)) opened()
                        else error = "原消息已删除或当前不可访问"
                    } catch (cancelled: CancellationException) {
                        throw cancelled
                    } catch (_: Exception) {
                        error = "无法打开原消息，请重试"
                    } finally {
                        jumping = false
                    }
                }
            }.padding(horizontal = 16.dp, vertical = 10.dp)) {
                val subtitle = listOfNotNull(title, message.sender).joinToString(" · ")
                Text(subtitle, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.primary)
                if (message.text.isNotBlank()) Text(message.text, maxLines = 3, overflow = TextOverflow.Ellipsis)
                message.attachment?.let { Text(it.name, maxLines = 1, overflow = TextOverflow.Ellipsis) }
            }
            HorizontalDivider()
        }
        if (more) item {
            TextButton(onClick = { offset = nextOffset; refresh++ }, enabled = !loading && !jumping) { Text("更多结果") }
        }
    }
}
