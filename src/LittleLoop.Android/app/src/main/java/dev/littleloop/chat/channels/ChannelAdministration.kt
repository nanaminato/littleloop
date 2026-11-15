package dev.littleloop.chat

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp

@Composable
internal fun ChannelAdministration(chat: ChatRepository, state: ChatState, room: Room, dismiss: () -> Unit) {
    val controller = rememberChannelAdministration(chat, state, room, dismiss)
    val ui = controller.ui
    val permissions = controller.permissions
    AlertDialog(onDismissRequest = dismiss, title = { Text(stringResource(R.string.channel_settings)) },
        text = { ChannelAdministrationContent(ui, permissions, state.rooms, controller) },
        confirmButton = { TextButton(onClick = dismiss) { Text(stringResource(R.string.action_cancel)) } })
    ChannelAdministrationConfirmations(ui, permissions, controller)
}

@Composable
private fun ChannelAdministrationContent(ui: ChannelAdministrationUiState, permissions: ChannelPermissions,
    rooms: List<Room>, actions: ChannelAdministrationActions) {
    Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        if (ui.error) Text(stringResource(R.string.channel_settings_failed), color = MaterialTheme.colorScheme.error)
        ui.settings?.let { current ->
            if (permissions.profile) ChannelProfileFields(ui, actions)
            ChannelAdministratorFields(ui, current.grants, permissions.owner, actions)
            if (permissions.owner) ChannelOwnershipFields(ui, actions)
            if (permissions.subscribers) ChannelSubscriberFields(ui, actions)
            if (permissions.owner) ChannelAudit(ui, actions)
            ChannelDiscussionFields(ui, current, permissions.discussion, rooms, actions)
            if (permissions.owner) TextButton(enabled = !ui.busy, onClick = actions::requestDeletion) {
                Text(stringResource(R.string.channel_delete_room), color = MaterialTheme.colorScheme.error)
            }
        }
    }
}

@Composable
private fun ChannelProfileFields(ui: ChannelAdministrationUiState, actions: ChannelAdministrationActions) {
    OutlinedTextField(ui.title, actions::title, singleLine = true,
        label = { Text(stringResource(R.string.channel_title)) })
    Row(verticalAlignment = Alignment.CenterVertically) {
        Checkbox(checked = ui.signPosts, onCheckedChange = actions::signatures)
        Text(stringResource(R.string.channel_sign_posts))
    }
    TextButton(enabled = !ui.busy && ui.title.isNotBlank(), onClick = actions::saveProfile) {
        Text(stringResource(R.string.action_save))
    }
}

private val channelRightLabels = listOf(
    ChannelRight.Publish to R.string.channel_publish, ChannelRight.Edit to R.string.channel_edit,
    ChannelRight.Delete to R.string.channel_delete, ChannelRight.Pin to R.string.action_pin,
    ChannelRight.Profile to R.string.channel_profile, ChannelRight.Subscribers to R.string.channel_subscribers,
    ChannelRight.Discussion to R.string.channel_discussion,
)

@Composable
private fun ChannelAdministratorFields(ui: ChannelAdministrationUiState, grants: List<ChannelGrant>,
    owner: Boolean, actions: ChannelAdministrationActions) {
    for (grant in grants) {
        val granted = channelRightLabels.mapNotNull { (right, label) ->
            if (grant.rights and right.bit != 0) stringResource(label) else null
        }.joinToString(" · ")
        Text(grant.name, style = MaterialTheme.typography.titleSmall)
        Text(granted, style = MaterialTheme.typography.bodySmall)
        if (owner) TextButton(onClick = { actions.editGrant(grant) }) { Text(stringResource(R.string.action_edit)) }
    }
    if (owner) {
        OutlinedTextField(ui.name, actions::administrator, singleLine = true,
            label = { Text(stringResource(R.string.channel_admin_name)) })
        channelRightLabels.forEach { (right, label) ->
            Row(verticalAlignment = Alignment.CenterVertically) {
                Checkbox(checked = ui.rights and right.bit != 0, onCheckedChange = { actions.toggleRight(right.bit) })
                Text(stringResource(label))
            }
        }
        TextButton(enabled = !ui.busy && ui.name.isNotBlank() && ui.rights != 0, onClick = actions::saveGrant) {
            Text(stringResource(R.string.action_save))
        }
        if (ui.editing != null) TextButton(enabled = !ui.busy, onClick = actions::revokeGrant) {
            Text(stringResource(R.string.channel_revoke))
        }
    }
}

@Composable
private fun ChannelOwnershipFields(ui: ChannelAdministrationUiState, actions: ChannelAdministrationActions) {
    OutlinedTextField(ui.newOwner, actions::newOwner, singleLine = true,
        label = { Text(stringResource(R.string.channel_new_owner)) })
    TextButton(enabled = !ui.busy && ui.newOwner.isNotBlank(), onClick = actions::requestTransfer) {
        Text(stringResource(R.string.channel_transfer))
    }
}

@Composable
private fun ChannelSubscriberFields(ui: ChannelAdministrationUiState, actions: ChannelAdministrationActions) {
    OutlinedTextField(ui.subscriber, actions::subscriber, singleLine = true,
        label = { Text(stringResource(R.string.channel_admin_name)) })
    TextButton(enabled = !ui.busy && ui.subscriber.isNotBlank(), onClick = actions::removeSubscriber) {
        Text(stringResource(R.string.channel_remove_subscriber))
    }
}

@Composable
private fun ChannelAudit(ui: ChannelAdministrationUiState, actions: ChannelAdministrationActions) {
    TextButton(enabled = !ui.auditing, onClick = { actions.loadAudit() }) { Text(stringResource(R.string.channel_audit)) }
    ui.audit?.let { entries ->
        if (entries.isEmpty()) Text(stringResource(R.string.channel_audit_empty))
        for (entry in entries) {
            Text(entry.actor + " · " + channelAuditLabel(entry.action), style = MaterialTheme.typography.labelLarge)
            Text(entry.detail + " · " + messageDay(entry.createdAt) + " " + messageTime(entry.createdAt),
                style = MaterialTheme.typography.bodySmall)
        }
        if (ui.auditMore) TextButton(enabled = !ui.auditing, onClick = { actions.loadAudit(entries.lastOrNull()?.id) }) {
            Text(stringResource(R.string.channel_comments_older))
        }
    }
}

@Composable
private fun ChannelDiscussionFields(ui: ChannelAdministrationUiState, current: ChannelSettings,
    canLink: Boolean, rooms: List<Room>, actions: ChannelAdministrationActions) {
    val linked = rooms.firstOrNull { it.id == current.group }
    Text(linked?.title ?: stringResource(if (current.group == null) R.string.channel_no_discussion else R.string.channel_linked))
    if (linked != null) TextButton(onClick = actions::openDiscussion) { Text(stringResource(R.string.channel_open_group)) }
    if (current.group != null && linked == null) {
        val pending = ui.requestedGroup == current.group
        TextButton(enabled = !ui.busy && !pending, onClick = actions::requestMembership) {
            Text(stringResource(if (pending) R.string.channel_join_requested else R.string.channel_request_join))
        }
    }
    if (canLink) {
        Text(stringResource(R.string.channel_link_notice))
        TextButton(enabled = !ui.busy, onClick = actions::togglePicker) { Text(stringResource(R.string.channel_link)) }
        if (ui.picker) {
            for (group in rooms.filter { it.type == "group" && it.canManage }) {
                TextButton(enabled = !ui.busy, onClick = { actions.linkDiscussion(group.id) }) { Text(group.title) }
            }
            if (current.group != null) TextButton(enabled = !ui.busy, onClick = { actions.linkDiscussion(null) }) {
                Text(stringResource(R.string.channel_unlink))
            }
        }
    }
}

@Composable
private fun ChannelAdministrationConfirmations(ui: ChannelAdministrationUiState, permissions: ChannelPermissions,
    actions: ChannelAdministrationActions) {
    ui.transferTarget?.let { target ->
        AlertDialog(onDismissRequest = actions::cancelTransfer,
            title = { Text(stringResource(R.string.channel_transfer)) },
            text = { Text(stringResource(R.string.channel_transfer_notice, target)) },
            confirmButton = {
                TextButton(enabled = !ui.busy && permissions.owner, onClick = actions::transfer) { Text(stringResource(R.string.social_confirm)) }
            },
            dismissButton = { TextButton(onClick = actions::cancelTransfer) { Text(stringResource(R.string.action_cancel)) } })
    }
    if (ui.confirmingDeletion) {
        AlertDialog(onDismissRequest = { if (!ui.busy) actions.cancelDeletion() },
            title = { Text(stringResource(R.string.channel_delete_room)) },
            text = { Text(stringResource(if (ui.error) R.string.channel_delete_failed else R.string.channel_delete_notice)) },
            confirmButton = {
                TextButton(enabled = !ui.busy && permissions.owner, onClick = actions::deleteChannel) {
                    Text(stringResource(R.string.channel_delete_confirm), color = MaterialTheme.colorScheme.error)
                }
            },
            dismissButton = {
                TextButton(enabled = !ui.busy, onClick = actions::cancelDeletion) { Text(stringResource(R.string.action_cancel)) }
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
