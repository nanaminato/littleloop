package dev.littleloop.chat

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp

@Composable
internal fun ColumnScope.GroupMembersContent(username: String, members: List<ChatMember>, enabled: Boolean, error: String?,
    permissions: GroupPermissions, operations: (ChatMember) -> List<GroupMemberOperation>,
    onUsername: (String) -> Unit, onInvite: () -> Unit, onSelect: (String, GroupMemberOperation) -> Unit) {
    if (permissions.invite) SocialUsername(username, onUsername, stringResource(R.string.social_member_username),
        stringResource(R.string.social_invite), enabled, onInvite)
    SocialError(error)
    LazyColumn(Modifier.fillMaxWidth().weight(1f)) {
        items(members, key = ChatMember::username) { member ->
            GroupMemberRow(member, operations(member), enabled, onSelect)
            HorizontalDivider(Modifier.padding(start = 68.dp))
        }
    }
}

@Composable
private fun GroupMemberRow(member: ChatMember, choices: List<GroupMemberOperation>, enabled: Boolean,
    onSelect: (String, GroupMemberOperation) -> Unit) {
    Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 7.dp),
        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        RoomAvatar(member.displayName, "direct", 40.dp)
        Column(Modifier.weight(1f)) {
            val adminLabel = stringResource(R.string.social_admin)
            val silencedLabel = stringResource(R.string.social_silenced)
            Text(member.displayName, style = MaterialTheme.typography.titleMedium)
            Text(if (member.owner) "@${member.username} · ${stringResource(R.string.social_owner)}" else buildString {
                append("@${member.username}")
                if (member.administrator) append(" · ").append(adminLabel)
                if (member.silenced) append(" · ").append(silencedLabel)
            }, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        if (choices.isNotEmpty()) {
            var menu by remember(member.username) { mutableStateOf(false) }
            Box {
                TextButton(enabled = enabled, onClick = { menu = true }) { Text("⋮") }
                DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                    choices.forEach { choice ->
                        DropdownMenuItem(text = { Text(stringResource(groupMemberAction(choice))) },
                            enabled = enabled, onClick = { menu = false; onSelect(member.username, choice) })
                    }
                }
            }
        }
    }
}

internal fun groupMemberAction(operation: GroupMemberOperation) = when (operation) {
    GroupMemberOperation.Remove -> R.string.social_remove_member
    GroupMemberOperation.Transfer -> R.string.social_transfer
    GroupMemberOperation.Promote -> R.string.social_promote
    GroupMemberOperation.Demote -> R.string.social_demote
    GroupMemberOperation.Silence -> R.string.social_silence
    GroupMemberOperation.Unsilence -> R.string.social_unsilence
}
