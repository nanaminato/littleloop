package dev.littleloop.chat

import androidx.compose.foundation.text.BasicTextField
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp

@Composable
internal fun ConversationComposer(room: Room, busy: Boolean, draft: String, replyTo: Long?, replyText: String?,
    file: SelectedAttachment?, onDraft: (String) -> Unit, onClearReply: () -> Unit,
    onRemoveAttachment: () -> Unit, onPickAttachment: () -> Unit, onSend: () -> Unit,
    onChannelSettings: () -> Unit) {
    if (room.canPost && replyTo != null) {
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(
                replyText ?: stringResource(R.string.conv_replying_to, (replyTo ?: 0).toInt()),
                Modifier.weight(1f),
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                style = MaterialTheme.typography.bodySmall,
            )
            TextButton(onClick = { onClearReply() }) { Text(stringResource(R.string.conv_cancel_reply)) }
        }
    }
    if (room.canPost && file != null) {
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(file?.name.orEmpty(), Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodySmall)
            TextButton(onClick = { onRemoveAttachment() }, enabled = !busy) { Text(stringResource(R.string.conv_remove_attachment)) }
        }
    }

    if (room.canPost) {
        Surface(color = MaterialTheme.colorScheme.surface, shadowElevation = 1.dp) {
            Row(
                Modifier.fillMaxWidth().padding(horizontal = 4.dp, vertical = 4.dp),
                verticalAlignment = Alignment.Bottom,
                horizontalArrangement = Arrangement.spacedBy(6.dp),
            ) {
                IconButton(onClick = { onPickAttachment() }, enabled = !busy) {
                    Text("＋", style = MaterialTheme.typography.titleLarge, color = MaterialTheme.colorScheme.primary)
                }
                BasicTextField(
                    value = draft,
                    onValueChange = { if (it.length <= 4000) onDraft(it) },
                    modifier = Modifier.weight(1f).heightIn(min = 48.dp).padding(vertical = 12.dp),
                    textStyle = MaterialTheme.typography.bodyLarge.copy(color = MaterialTheme.colorScheme.onSurface),
                    cursorBrush = SolidColor(MaterialTheme.colorScheme.primary),
                    maxLines = 4,
                    keyboardOptions = KeyboardOptions(imeAction = ImeAction.Default),
                    decorationBox = { inner ->
                        Box {
                            if (draft.isEmpty()) Text(stringResource(R.string.conv_message_hint), color = MaterialTheme.colorScheme.onSurfaceVariant)
                            inner()
                        }
                    },
                )
                IconButton(
                    onClick = onSend,
                    enabled = (draft.isNotBlank() || file != null) && !busy,
                    modifier = Modifier.size(48.dp),
                ) {
                    Text("➤", style = MaterialTheme.typography.titleLarge)
                }
            }
        }
    } else {
        Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(
                stringResource(if (room.type == "channel") R.string.conv_channel_read_only else R.string.conv_group_silenced),
                Modifier.weight(1f),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            if (room.type == "channel") TextButton(onClick = { onChannelSettings() }) {
                Text(stringResource(R.string.channel_discussion_entry))
            }
        }
    }
}
