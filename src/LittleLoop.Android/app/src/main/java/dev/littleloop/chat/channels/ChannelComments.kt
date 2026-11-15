package dev.littleloop.chat

import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

internal data class DiscussionThread(
    val group: String, val root: Long, val closed: Boolean, val count: Long, val messages: List<ChatMessage>,
    val readSequence: Long, val unread: Long, val canComment: Boolean, val following: Boolean,
)

@Composable
internal fun ChannelComments(chat: ChatRepository, state: ChatState, channel: Room, post: ChatMessage, dismiss: () -> Unit) {
    val fromGroup = channel.type == "group"
    var pageLimit by rememberSaveable(channel.id, post.sequence) { mutableIntStateOf(50) }
    var hasOlder by remember { mutableStateOf(false) }
    var thread by remember { mutableStateOf<DiscussionThread?>(null) }
    val commentList = rememberLazyListState()
    var positioned by rememberSaveable(channel.id, post.sequence) { mutableStateOf(false) }
    var followingChange by remember { mutableStateOf(false) }
    var followFailed by remember { mutableStateOf(false) }
    var changing by remember { mutableStateOf(false) }
    var changeFailed by remember { mutableStateOf(false) }
    var offline by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf(false) }
    var draft by rememberSaveable { mutableStateOf("") }
    var reply by remember { mutableStateOf<ChatMessage?>(null) }
    var attachment by remember { mutableStateOf<SelectedAttachment?>(null) }
    var editing by remember { mutableStateOf<ChatMessage?>(null) }
    var deleting by remember { mutableStateOf<ChatMessage?>(null) }
    var preview by remember { mutableStateOf<ChatMessage?>(null) }
    val scope = rememberCoroutineScope()
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) scope.launch {
            try { attachment = chat.selectedAttachment(uri) }
            catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { error = true }
        }
    }

    LaunchedEffect(channel.id, post.sequence, pageLimit) {
        while (true) {
            try {
                val latest = chat.channelComments(channel.id, post.sequence, fromGroup = fromGroup)
                val messages = latest.messages.toMutableList()
                var pageSize = messages.size
                while (pageSize == 50 && messages.size < pageLimit) {
                    val older = chat.channelComments(channel.id, post.sequence, messages.first().sequence, fromGroup).messages
                    pageSize = older.size
                    messages.addAll(0, older)
                }
                hasOlder = pageSize == 50
                thread = latest.copy(messages = messages)
                error = false
                offline = false
                val sequence = latest.messages.lastOrNull()?.sequence
                if (sequence != null && latest.unread > 0 && sequence > latest.readSequence) {
                    try { chat.markChannelCommentsRead(channel.id, post.sequence, sequence, fromGroup) }
                    catch (cancelled: CancellationException) { throw cancelled }
                    catch (_: Exception) { /* The next refresh retries an unacknowledged read. */ }
                }
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                offline = failure is java.io.IOException && failure !is ChatRequestException
                if (failure is ChatRequestException && failure.status in setOf(401, 403, 404)) thread = null
                error = !offline || thread == null
            }
            delay(3000)
        }
    }

    AlertDialog(
        onDismissRequest = dismiss,
        title = { Text(stringResource(R.string.channel_comments)) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                Text(if (post.deleted) stringResource(R.string.conv_deleted) else post.text, maxLines = 3)
                if (error) Text(stringResource(R.string.channel_comments_unavailable))
                else if (offline) Text(stringResource(R.string.conv_offline))
                if (!post.deleted && channel.hasRight(ChannelRight.Discussion)) {
                    val open = thread?.closed != false
                    TextButton(enabled = !changing, onClick = {
                        changing = true
                        changeFailed = false
                        scope.launch {
                            try {
                                chat.setPostDiscussion(channel.id, post.sequence, open)
                                thread = chat.channelComments(channel.id, post.sequence, fromGroup = fromGroup)
                                error = false
                            } catch (cancelled: CancellationException) {
                                throw cancelled
                            } catch (_: Exception) {
                                changeFailed = true
                            } finally {
                                changing = false
                            }
                        }
                    }) { Text(stringResource(if (open) R.string.channel_comments_open else R.string.channel_comments_close)) }
                    if (changeFailed) Text(stringResource(R.string.channel_settings_failed))
                }
                thread?.let { discussion ->
                    Text(stringResource(R.string.channel_comment_count, discussion.count))
                    if (!fromGroup) TextButton(enabled = !followingChange, onClick = {
                        followingChange = true
                        followFailed = false
                        scope.launch {
                            try {
                                val following = !discussion.following
                                chat.followChannelDiscussion(channel.id, post.sequence, following)
                                thread = thread?.copy(following = following)
                            } catch (cancelled: CancellationException) {
                                throw cancelled
                            } catch (_: Exception) {
                                followFailed = true
                            } finally {
                                followingChange = false
                            }
                        }
                    }) {
                        Text(stringResource(if (discussion.following) R.string.channel_unfollow else R.string.channel_follow))
                    }
                    if (followFailed) Text(stringResource(R.string.channel_follow_failed))
                    val commentsBySequence = remember(discussion.messages) {
                        discussion.messages.associateBy(ChatMessage::sequence)
                    }
                    LaunchedEffect(discussion.messages.isNotEmpty(), hasOlder) {
                        if (!positioned && discussion.messages.isNotEmpty()) {
                            commentList.scrollToItem(discussion.messages.lastIndex + if (hasOlder) 1 else 0)
                            positioned = true
                        }
                    }
                    LazyColumn(Modifier.heightIn(max = 260.dp), state = commentList) {
                        if (hasOlder) item {
                            TextButton(onClick = { pageLimit += 50 }) { Text(stringResource(R.string.channel_comments_older)) }
                        }
                        items(discussion.messages, key = ChatMessage::sequence) { comment ->
                            Text(comment.sender, style = MaterialTheme.typography.labelLarge)
                            comment.replyTo?.takeIf { !comment.deleted && it != discussion.root }?.let { sequence ->
                                val quoted = commentsBySequence[sequence]
                                val snippet = when {
                                    quoted == null -> "#$sequence"
                                    quoted.deleted -> quoted.sender + " · " + stringResource(R.string.conv_deleted)
                                    else -> quoted.sender + " · " + quoted.text.ifBlank { quoted.attachment?.name.orEmpty() }
                                }
                                Text(stringResource(R.string.action_reply) + " · " + snippet,
                                    maxLines = 1, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                    style = MaterialTheme.typography.bodySmall)
                            }
                            Text(if (comment.deleted) stringResource(R.string.conv_deleted) else comment.text)
                            val permissions = state.rooms.firstOrNull { it.id == discussion.group }
                                ?.messagePermissions(comment, state.username)
                            if (!comment.deleted) {
                                comment.attachment?.let { file ->
                                    TextButton(onClick = { preview = comment }) { Text(file.name) }
                                }
                                if (discussion.canComment && permissions?.reply == true) TextButton(onClick = { reply = comment }) {
                                    Text(stringResource(R.string.action_reply))
                                }
                                if (permissions?.edit == true) {
                                    TextButton(onClick = { editing = comment }) { Text(stringResource(R.string.action_edit)) }
                                }
                                if (permissions?.delete == true) {
                                    TextButton(onClick = { deleting = comment }) { Text(stringResource(R.string.action_delete)) }
                                }
                            }
                        }
                    }
                    val commentSequences = discussion.messages.mapTo(mutableSetOf()) { it.sequence }
                    commentSequences.add(discussion.root)
                    val received = discussion.messages.mapTo(mutableSetOf()) { it.clientId }
                    val pending = state.outbox.filter {
                        it.room == discussion.group && it.replyTo in commentSequences && it.id !in received
                    }
                    for (message in pending) {
                        Text(message.text.ifBlank { message.fileName.orEmpty() },
                            maxLines = 2, style = MaterialTheme.typography.bodySmall)
                        if (message.failure != null) {
                            Text(stringResource(R.string.conv_failed, message.failure.toString()),
                                color = MaterialTheme.colorScheme.error)
                            Row {
                                TextButton(onClick = { chat.retry(message) }, enabled = discussion.canComment && !state.busy) {
                                    Text(stringResource(R.string.action_retry))
                                }
                                TextButton(onClick = { chat.discard(message) }, enabled = !state.busy) {
                                    Text(stringResource(R.string.action_remove))
                                }
                            }
                        } else Text(stringResource(R.string.conv_pending))
                    }
                    if (discussion.closed) {
                        Text(stringResource(R.string.channel_comments_closed))
                    } else if (!discussion.canComment) {
                        Text(stringResource(R.string.channel_comments_silenced))
                    } else {
                        reply?.let { target ->
                            TextButton(onClick = { reply = null }) {
                                Text(stringResource(R.string.conv_cancel_reply) + " · " + target.sender)
                            }
                        }
                        OutlinedTextField(draft, { if (it.length <= 4000) draft = it },
                            label = { Text(stringResource(R.string.conv_message_hint)) }, maxLines = 3)
                        TextButton(onClick = {
                            if (attachment == null) picker.launch(arrayOf("*/*")) else attachment = null
                        }) { Text(attachment?.name ?: stringResource(R.string.conv_attachment)) }
                    }
                }
            }
        },
        confirmButton = {
            thread?.takeUnless { !it.canComment || error }?.let { discussion ->
                TextButton(enabled = draft.isNotBlank() || attachment != null, onClick = {
                    chat.send(discussion.group, draft, reply?.sequence ?: discussion.root, attachment) {
                        draft = ""; reply = null; attachment = null
                    }
                }) { Text(stringResource(R.string.action_send)) }
            }
        },
        dismissButton = { TextButton(onClick = dismiss) { Text(stringResource(R.string.action_cancel)) } },
    )
    val group = thread?.group
    val discussionRoom = state.rooms.firstOrNull { it.id == group }
    if (discussionRoom != null) editing?.let { comment ->
        val latest = thread?.messages?.firstOrNull { it.sequence == comment.sequence } ?: comment
        MessageOperationDialog(chat, state, discussionRoom, latest, MessageOperation.Edit,
            dismiss = { editing = null }, done = { editing = null })
    }
    if (discussionRoom != null) deleting?.let { comment ->
        val latest = thread?.messages?.firstOrNull { it.sequence == comment.sequence } ?: comment
        MessageOperationDialog(chat, state, discussionRoom, latest, MessageOperation.Delete,
            dismiss = { deleting = null }, done = { deleting = null })
    }
    if (group != null) preview?.let { AttachmentDialog(chat, group, it) { preview = null } }
}
