package dev.littleloop.chat

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

internal data class ChannelAuditEntry(val id: Long, val actor: String, val action: String,
    val detail: String, val createdAt: String)
internal data class ChannelGrant(val name: String, val rights: Int)
internal data class ChannelSettings(val owner: Boolean, val group: String?, val grants: List<ChannelGrant>)

@Composable
internal fun ChannelAdministration(chat: ChatRepository, state: ChatState, room: Room, dismiss: () -> Unit) {
    val scope = rememberCoroutineScope()
    var settings by remember { mutableStateOf<ChannelSettings?>(null) }
    var error by remember { mutableStateOf(false) }
    var busy by remember { mutableStateOf(false) }
    var title by remember(room.id, room.title) { mutableStateOf(room.title) }
    var signPosts by remember(room.id, room.signPosts) { mutableStateOf(room.signPosts) }
    var newOwner by remember { mutableStateOf("") }
    var transferTarget by remember { mutableStateOf<String?>(null) }
    var confirmingDeletion by remember { mutableStateOf(false) }
    var subscriber by remember { mutableStateOf("") }
    var name by remember { mutableStateOf("") }
    var editing by remember { mutableStateOf<String?>(null) }
    var rights by remember { mutableIntStateOf(0) }
    var requestedGroup by remember { mutableStateOf<String?>(null) }
    var auditing by remember { mutableStateOf(false) }
    var audit by remember { mutableStateOf<List<ChannelAuditEntry>?>(null) }
    var auditMore by remember { mutableStateOf(false) }

    fun loadAudit(before: Long? = null) {
        if (auditing) return
        auditing = true
        error = false
        scope.launch {
            try {
                val page = chat.channelAudit(room.id, before)
                audit = if (before == null) page else audit.orEmpty() + page
                auditMore = page.size == 50
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                error = true
            } finally {
                auditing = false
            }
        }
    }

    var picker by remember { mutableStateOf(false) }
    val labels = listOf(R.string.channel_publish, R.string.channel_edit, R.string.channel_delete,
        R.string.action_pin, R.string.channel_profile, R.string.channel_subscribers, R.string.channel_discussion)

    fun update(refresh: Boolean = true, change: suspend () -> Unit = {}) {
        if (busy) return
        busy = true
        error = false
        scope.launch {
            try {
                change()
                if (refresh) settings = chat.channelSettings(room.id, state.username.orEmpty())
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                error = true
            } finally {
                busy = false
            }
        }
    }
    LaunchedEffect(room.id) { update() }

    AlertDialog(onDismissRequest = dismiss, title = { Text(stringResource(R.string.channel_settings)) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                val current = settings
                if (error) Text(stringResource(R.string.channel_settings_failed), color = MaterialTheme.colorScheme.error)
                if (current != null) {
                    if (room.channelRights and 16 != 0) {
                        OutlinedTextField(title, { title = it.take(100) }, singleLine = true,
                            label = { Text(stringResource(R.string.channel_title)) })
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Checkbox(checked = signPosts, onCheckedChange = { signPosts = it })
                            Text(stringResource(R.string.channel_sign_posts))
                        }
                        TextButton(enabled = !busy && title.isNotBlank(), onClick = {
                            val newTitle = title.trim()
                            val signed = signPosts
                            update { chat.updateChannelProfile(room.id, newTitle, signed) }
                        }) { Text(stringResource(R.string.action_save)) }
                    }
                    for (grant in current.grants) {
                        val granted = labels.mapIndexedNotNull { index, label ->
                            if (grant.rights and (1 shl index) != 0) stringResource(label) else null
                        }.joinToString(" · ")
                        Text(grant.name, style = MaterialTheme.typography.titleSmall)
                        Text(granted, style = MaterialTheme.typography.bodySmall)
                        if (current.owner) TextButton(onClick = {
                            name = grant.name
                            editing = grant.name
                            rights = grant.rights
                        }) { Text(stringResource(R.string.action_edit)) }
                    }
                    if (current.owner) {
                        OutlinedTextField(name, { name = it.take(32) }, singleLine = true,
                            label = { Text(stringResource(R.string.channel_admin_name)) })
                        labels.forEachIndexed { index, label ->
                            val bit = 1 shl index
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Checkbox(checked = rights and bit != 0, onCheckedChange = { rights = rights xor bit })
                                Text(stringResource(label))
                            }
                        }
                        TextButton(enabled = !busy && name.isNotBlank() && rights != 0, onClick = {
                            val target = name.trim()
                            val mask = rights
                            update { chat.setChannelGrant(room.id, target, mask) }
                        }) { Text(stringResource(R.string.action_save)) }
                        editing?.let { target ->
                            TextButton(enabled = !busy, onClick = {
                                update { chat.setChannelGrant(room.id, target, 0); editing = null; name = ""; rights = 0 }
                            }) { Text(stringResource(R.string.channel_revoke)) }
                        }
                    }
                    if (current.owner) {
                        OutlinedTextField(newOwner, { newOwner = it.take(32) }, singleLine = true,
                            label = { Text(stringResource(R.string.channel_new_owner)) })
                        TextButton(enabled = !busy && newOwner.isNotBlank(), onClick = { transferTarget = newOwner.trim() }) {
                            Text(stringResource(R.string.channel_transfer))
                        }
                    }
                    if (room.channelRights and 32 != 0) {
                        OutlinedTextField(subscriber, { subscriber = it.take(32) }, singleLine = true,
                            label = { Text(stringResource(R.string.channel_admin_name)) })
                        TextButton(enabled = !busy && subscriber.isNotBlank(), onClick = {
                            val target = subscriber.trim()
                            update { chat.removeChannelSubscriber(room.id, target); subscriber = "" }
                        }) { Text(stringResource(R.string.channel_remove_subscriber)) }
                    }
                    if (current.owner) {
                        TextButton(enabled = !auditing, onClick = { loadAudit() }) {
                            Text(stringResource(R.string.channel_audit))
                        }
                        audit?.let { entries ->
                            if (entries.isEmpty()) Text(stringResource(R.string.channel_audit_empty))
                            for (entry in entries) {
                                Text(entry.actor + " · " + channelAuditLabel(entry.action), style = MaterialTheme.typography.labelLarge)
                                Text(entry.detail + " · " + messageDay(entry.createdAt) + " " + messageTime(entry.createdAt),
                                    style = MaterialTheme.typography.bodySmall)
                            }
                            if (auditMore) TextButton(enabled = !auditing, onClick = { loadAudit(entries.lastOrNull()?.id) }) {
                                Text(stringResource(R.string.channel_comments_older))
                            }
                        }
                    }
                    val linked = state.rooms.firstOrNull { it.id == current.group }
                    Text(linked?.title ?: stringResource(
                        if (current.group == null) R.string.channel_no_discussion else R.string.channel_linked))
                    if (linked != null) TextButton(onClick = { dismiss(); chat.select(linked.id) }) {
                        Text(stringResource(R.string.channel_open_group))
                    }
                    if (current.group != null && linked == null) {
                        val pending = requestedGroup == current.group
                        TextButton(enabled = !busy && !pending, onClick = {
                            update {
                                chat.requestDiscussionMembership(room.id)
                                requestedGroup = current.group
                            }
                        }) { Text(stringResource(if (pending) R.string.channel_join_requested else R.string.channel_request_join)) }
                    }
                    val canLink = current.owner || current.grants.any { it.name == state.username && it.rights and 64 != 0 }
                    if (canLink) {
                        Text(stringResource(R.string.channel_link_notice))
                        TextButton(enabled = !busy, onClick = { picker = !picker }) {
                            Text(stringResource(R.string.channel_link))
                        }
                        if (picker) {
                            for (group in state.rooms.filter { it.type == "group" && it.canManage }) {
                                TextButton(enabled = !busy, onClick = {
                                    update { chat.linkDiscussion(room.id, group.id); picker = false }
                                }) { Text(group.title) }
                            }
                            if (current.group != null) TextButton(enabled = !busy, onClick = {
                                update { chat.linkDiscussion(room.id, null); picker = false }
                            }) { Text(stringResource(R.string.channel_unlink)) }
                        }
                    }
                    if (current.owner) TextButton(enabled = !busy, onClick = { confirmingDeletion = true }) {
                        Text(stringResource(R.string.channel_delete_room), color = MaterialTheme.colorScheme.error)
                    }
                }
            }
        },
        confirmButton = { TextButton(onClick = dismiss) { Text(stringResource(R.string.action_cancel)) } })
    transferTarget?.let { target ->
        AlertDialog(onDismissRequest = { transferTarget = null },
            title = { Text(stringResource(R.string.channel_transfer)) },
            text = { Text(stringResource(R.string.channel_transfer_notice, target)) },
            confirmButton = {
                TextButton(enabled = !busy, onClick = {
                    update {
                        chat.transferChannelOwnership(room.id, target)
                        newOwner = ""
                        transferTarget = null
                    }
                }) { Text(stringResource(R.string.social_confirm)) }
            },
            dismissButton = { TextButton(onClick = { transferTarget = null }) { Text(stringResource(R.string.action_cancel)) } })
    }
    if (confirmingDeletion) {
        AlertDialog(onDismissRequest = { if (!busy) confirmingDeletion = false },
            title = { Text(stringResource(R.string.channel_delete_room)) },
            text = { Text(stringResource(if (error) R.string.channel_delete_failed else R.string.channel_delete_notice)) },
            confirmButton = {
                TextButton(enabled = !busy, onClick = {
                    update(refresh = false) {
                        chat.deleteChannel(room.id)
                        confirmingDeletion = false
                        dismiss()
                    }
                }) { Text(stringResource(R.string.channel_delete_confirm), color = MaterialTheme.colorScheme.error) }
            },
            dismissButton = {
                TextButton(enabled = !busy, onClick = { confirmingDeletion = false }) {
                    Text(stringResource(R.string.action_cancel))
                }
            })
    }

}


@Composable
private fun channelAuditLabel(action: String): String {
    val label = when (action) {
        "ownership_transferred" -> R.string.channel_transfer
        "profile_changed" -> R.string.channel_profile
        "subscriber_removed" -> R.string.channel_remove_subscriber
        "administrator_granted" -> R.string.channel_admin_granted
        "administrator_revoked" -> R.string.channel_revoke
        "post_published" -> R.string.channel_publish
        "post_edited" -> R.string.channel_edit
        "post_deleted" -> R.string.channel_delete
        "post_pinned" -> R.string.action_pin
        "post_unpinned" -> R.string.action_unpin
        "comments_opened" -> R.string.channel_comments_open
        "comments_closed" -> R.string.channel_comments_close
        "discussion_linked" -> R.string.channel_link
        "discussion_unlinked" -> R.string.channel_unlink
        else -> return action
    }
    return stringResource(label)
}
