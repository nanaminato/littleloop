package dev.littleloop.chat

import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp

@Composable
internal fun CreateSpaceDialog(
    topic: Boolean, title: String, busy: Boolean, error: String?, canSubmit: Boolean,
    onTitle: (String) -> Unit, dismiss: () -> Unit, create: () -> Unit,
) {
    AlertDialog(
        onDismissRequest = { if (!busy) dismiss() },
        title = { Text(stringResource(if (topic) R.string.topics_new else R.string.channels_new)) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedTextField(
                    title, onTitle, Modifier.fillMaxWidth(),
                    label = { Text(stringResource(if (topic) R.string.topics_title else R.string.channels_name)) },
                    singleLine = true, enabled = !busy, shape = MaterialTheme.shapes.medium,
                )
                error?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
            }
        },
        confirmButton = {
            TextButton(onClick = create, enabled = canSubmit) {
                Text(stringResource(if (topic) R.string.topics_create else R.string.channels_create))
            }
        },
        dismissButton = { TextButton(onClick = dismiss, enabled = !busy) { Text(stringResource(R.string.action_cancel)) } },
    )
}
