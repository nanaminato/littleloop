package dev.littleloop.chat

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.res.stringResource

@Composable
internal fun GroupMemberDialogs(ui: GroupMembersUiState, pending: PendingMemberAction?, permissions: GroupPermissions,
    enabled: Boolean, onDismiss: () -> Unit, onConfirm: () -> Unit, onReview: (Boolean) -> Unit,
    onRespondJoin: (String, Boolean) -> Unit) {
    if (permissions.reviewJoins && ui.joinRequests.isNotEmpty()) {
        TextButton(onClick = { onReview(true) }) { Text(stringResource(R.string.channel_join_requests)) }
        if (ui.reviewingJoins) JoinRequestDialog(ui.joinRequests.first(), enabled, { onReview(false) }, onRespondJoin)
    }
    pending?.let { action ->
        AlertDialog(onDismissRequest = onDismiss,
            title = { Text(stringResource(groupMemberAction(action.operation))) },
            text = { Text("@${action.username}") },
            dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.action_cancel)) } },
            confirmButton = { TextButton(enabled = enabled, onClick = onConfirm) { Text(stringResource(R.string.social_confirm)) } })
    }
}

@Composable
private fun JoinRequestDialog(applicant: String, enabled: Boolean, dismiss: () -> Unit, onRespond: (String, Boolean) -> Unit) {
    AlertDialog(onDismissRequest = dismiss, title = { Text(stringResource(R.string.channel_join_requests)) },
        text = {
            Column {
                Text("@$applicant")
                Row {
                    TextButton(enabled = enabled, onClick = { onRespond(applicant, true) }) { Text(stringResource(R.string.channel_join_approve)) }
                    TextButton(enabled = enabled, onClick = { onRespond(applicant, false) }) { Text(stringResource(R.string.channel_join_decline)) }
                }
            }
        }, confirmButton = { TextButton(onClick = dismiss) { Text(stringResource(R.string.action_cancel)) } })
}
