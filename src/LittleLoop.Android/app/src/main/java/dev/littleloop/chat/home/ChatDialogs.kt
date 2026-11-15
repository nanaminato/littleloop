package dev.littleloop.chat

import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp

@Composable
internal fun CreateRoom(busy: Boolean, error: String?, cancel: () -> Unit, confirm: (String, String, String) -> Unit) {
    var group by rememberSaveable { mutableStateOf(false) }
    var title by rememberSaveable { mutableStateOf("") }
    var members by rememberSaveable { mutableStateOf("") }
    AlertDialog(
        onDismissRequest = cancel,
        title = { Text(stringResource(R.string.home_new_chat)) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    FilterChip(!group, { group = false }, { Text(stringResource(R.string.home_direct)) })
                    FilterChip(group, { group = true }, { Text(stringResource(R.string.home_group)) })
                }
                if (group) {
                    OutlinedTextField(
                        title, { if (it.length <= 100) title = it }, Modifier.fillMaxWidth(),
                        label = { Text(stringResource(R.string.home_group_name)) },
                        singleLine = true, shape = MaterialTheme.shapes.medium,
                    )
                }
                OutlinedTextField(
                    members, { members = it }, Modifier.fillMaxWidth(),
                    label = { Text(stringResource(if (group) R.string.home_members_hint else R.string.home_peer_hint)) },
                    supportingText = if (group) { { Text(stringResource(R.string.home_members_optional)) } } else null,
                    singleLine = true, shape = MaterialTheme.shapes.medium,
                )
                error?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
            }
        },
        confirmButton = {
            TextButton(
                onClick = { confirm(if (group) "group" else "direct", title, members) },
                enabled = !busy && (if (group) title.isNotBlank() else members.isNotBlank()),
            ) { Text(stringResource(R.string.action_create)) }
        },
        dismissButton = { TextButton(onClick = cancel) { Text(stringResource(R.string.action_cancel)) } },
    )
}
