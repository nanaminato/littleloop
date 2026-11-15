package dev.littleloop.chat

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp

data class ChatFriend(val id: String, val username: String, val displayName: String, val state: String)
data class ChatMember(val username: String, val displayName: String, val robot: Boolean, val owner: Boolean, val administrator: Boolean = false, val silenced: Boolean = false)

@Composable
internal fun FriendsPanel(chat: ChatRepository, state: ChatState, openChat: () -> Unit) {
    var username by rememberSaveable { mutableStateOf("") }
    val enabled = state.connected && !state.busy
    SocialUsername(
        username, { username = it },
        stringResource(R.string.friends_username),
        stringResource(R.string.friends_add),
        enabled,
    ) {
        chat.requestFriend(username) { username = "" }
    }
    SocialError(state.error)
    LazyColumn(Modifier.fillMaxWidth()) {
        if (state.friends.isEmpty()) item {
            EmptyState("♧", stringResource(R.string.friends_empty))
        }
        items(state.friends, key = { it.id }) { friend ->
            Row(
                Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 7.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                RoomAvatar(friend.displayName, "direct", 40.dp)
                Column(Modifier.weight(1f)) {
                    Text(friend.displayName, style = MaterialTheme.typography.titleMedium)
                    Text("@${friend.username}", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                when (friend.state) {
                    "incoming" -> {
                        TextButton(enabled = enabled, onClick = { chat.respondFriend(friend, true) }) { Text(stringResource(R.string.friends_accept)) }
                        TextButton(enabled = enabled, onClick = { chat.respondFriend(friend, false) }) { Text(stringResource(R.string.friends_decline)) }
                    }
                    "outgoing" -> Text(stringResource(R.string.friends_pending), style = MaterialTheme.typography.bodySmall)
                    "friend" -> TextButton(
                        enabled = enabled,
                        onClick = { chat.create("direct", "", friend.username, openChat) },
                    ) { Text(stringResource(R.string.friends_direct)) }
                }
            }
            HorizontalDivider(Modifier.padding(start = 68.dp))
        }
    }
}

@Composable
internal fun GroupMembersPanel(chat: ChatRepository, state: ChatState, room: Room) {
    var username by rememberSaveable(room.id) { mutableStateOf("") }
    var target by rememberSaveable(room.id) { mutableStateOf<String?>(null) }
    var operation by rememberSaveable(room.id) { mutableStateOf("") }
    val enabled = state.connected && !state.busy
    var joinRequests by remember(room.id) { mutableStateOf<List<String>>(emptyList()) }
    LaunchedEffect(room.id, room.canManage) {
        while (room.canManage) {
            try { joinRequests = chat.discussionJoinRequests(room.id) }
            catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { /* A later refresh can recover. */ }
            delay(5000)
        }
    }
    if (room.canManage) {
        SocialUsername(
            username, { username = it },
            stringResource(R.string.social_member_username),
            stringResource(R.string.social_invite),
            enabled,
        ) {
            chat.changeMember(room.id, username, "invite") { username = "" }
        }
    }
    SocialError(state.error)
    LazyColumn(Modifier.fillMaxWidth()) {
        if (state.membersRoom == room.id) {
            items(state.members, key = { it.username }) { member ->
                Row(
                    Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 7.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                ) {
                    RoomAvatar(member.displayName, "direct", 40.dp)
                    Column(Modifier.weight(1f)) {
                        val adminLabel = stringResource(R.string.social_admin)
                        val silencedLabel = stringResource(R.string.social_silenced)
                        Text(member.displayName, style = MaterialTheme.typography.titleMedium)
                        Text(
                            if (member.owner) "@${member.username} · ${stringResource(R.string.social_owner)}" else buildString {
                                append("@${member.username}")
                                if (member.administrator) append(" · ").append(adminLabel)
                                if (member.silenced) append(" · ").append(silencedLabel)
                            },
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                    if (room.canModerate && !member.owner && !member.robot && member.username != state.username &&
                        (room.canManage || !member.administrator)) {
                        var menu by remember(member.username) { mutableStateOf(false) }
                        androidx.compose.foundation.layout.Box {
                            TextButton(enabled = enabled, onClick = { menu = true }) { Text("⋮") }
                            androidx.compose.material3.DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                                val choices = buildList {
                                    add("remove")
                                    add(if (member.silenced) "unsilence" else "silence")
                                    if (room.canManage) {
                                        add(if (member.administrator) "demote" else "promote")
                                        add("transfer")
                                    }
                                }
                                choices.forEach { choice ->
                                    androidx.compose.material3.DropdownMenuItem(
                                        text = { Text(stringResource(groupMemberAction(choice))) },
                                        onClick = { menu = false; target = member.username; operation = choice },
                                    )
                                }
                            }
                        }
                    }
                }
                HorizontalDivider(Modifier.padding(start = 68.dp))
            }
        }
    }
    var reviewingJoins by remember(room.id) { mutableStateOf(false) }
    if (room.canManage && joinRequests.isNotEmpty()) {
        TextButton(onClick = { reviewingJoins = true }) { Text(stringResource(R.string.channel_join_requests)) }
    }
    if (room.canManage && reviewingJoins && joinRequests.isNotEmpty()) {
        AlertDialog(
            onDismissRequest = { reviewingJoins = false },
            title = { Text(stringResource(R.string.channel_join_requests)) },
            text = {
                val applicant = joinRequests.first()
                Column {
                    Text("@$applicant")
                    Row {
                        TextButton(enabled = enabled, onClick = {
                            chat.changeMember(room.id, applicant, "approve_join") { joinRequests = joinRequests - applicant }
                        }) { Text(stringResource(R.string.channel_join_approve)) }
                        TextButton(enabled = enabled, onClick = {
                            chat.changeMember(room.id, applicant, "decline_join") { joinRequests = joinRequests - applicant }
                        }) { Text(stringResource(R.string.channel_join_decline)) }
                    }
                }
            },
            confirmButton = { TextButton(onClick = { reviewingJoins = false }) { Text(stringResource(R.string.action_cancel)) } },
        )
    }
    val pending = target
    if (room.canModerate && pending != null) {
        val title = stringResource(groupMemberAction(operation))
        AlertDialog(
            onDismissRequest = { target = null },
            title = { Text(title) },
            text = { Text("@$pending") },
            dismissButton = { TextButton(onClick = { target = null }) { Text(stringResource(R.string.action_cancel)) } },
            confirmButton = {
                TextButton(enabled = enabled, onClick = {
                    chat.changeMember(room.id, pending, operation) { target = null }
                }) { Text(stringResource(R.string.social_confirm)) }
            },
        )
    }
}

private fun groupMemberAction(operation: String) = when (operation) {
    "remove" -> R.string.social_remove_member
    "transfer" -> R.string.social_transfer
    "promote" -> R.string.social_promote
    "demote" -> R.string.social_demote
    "silence" -> R.string.social_silence
    else -> R.string.social_unsilence
}

@Composable
private fun SocialUsername(value: String, update: (String) -> Unit, label: String, action: String,
                           enabled: Boolean, submit: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        OutlinedTextField(
            value, { if (it.length <= 32) update(it) }, Modifier.weight(1f),
            label = { Text(label) }, singleLine = true, shape = MaterialTheme.shapes.medium,
        )
        TextButton(enabled = enabled && value.isNotBlank(), onClick = submit) { Text(action) }
    }
}

@Composable
private fun SocialError(error: String?) {
    if (error != null && error != "Offline") {
        Text(
            error,
            Modifier.padding(horizontal = 16.dp, vertical = 4.dp),
            color = MaterialTheme.colorScheme.error,
            style = MaterialTheme.typography.bodySmall,
        )
    }
}
