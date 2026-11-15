package dev.littleloop.chat

import androidx.compose.ui.semantics.CustomAccessibilityAction
import androidx.compose.ui.semantics.customActions
import android.text.format.Formatter
import androidx.compose.foundation.background
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp

@Composable
internal fun PendingBubble(message: PendingMessage, upload: UploadProgress?, busy: Boolean, onRetry: () -> Unit, onDiscard: () -> Unit) {
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
        Column(
            Modifier.widthIn(max = 300.dp)
                .clip(RoundedCornerShape(16.dp, 16.dp, 4.dp, 16.dp))
                .background(MaterialTheme.colorScheme.primaryContainer)
                .padding(horizontal = 12.dp, vertical = 8.dp),
        ) {
            if (message.text.isNotBlank()) Text(message.text, style = MaterialTheme.typography.bodyLarge)
            message.fileName?.let { Text(it, style = MaterialTheme.typography.labelMedium) }
            val uploading = upload?.takeIf { it.id == message.id }
            if (uploading != null) {
                Text(stringResource(R.string.conv_uploading, uploading.percent), style = MaterialTheme.typography.labelMedium)
                LinearProgressIndicator(progress = { uploading.percent / 100f }, modifier = Modifier.width(160.dp))
            } else if (message.failure == null) {
                Text(stringResource(R.string.conv_pending), style = MaterialTheme.typography.labelMedium)
            } else {
                Text(stringResource(R.string.conv_failed, message.failure.toString()), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.labelMedium)
                Row {
                    TextButton(onRetry, enabled = !busy) { Text(stringResource(R.string.action_retry)) }
                    TextButton(onDiscard, enabled = !busy) { Text(stringResource(R.string.action_remove)) }
                }
            }
        }
    }
}

@Composable
internal fun MessageBubble(
    username: String,
    enabled: Boolean,
    permissions: MessagePermissions,
    room: Room,
    message: ChatMessage,
    older: ChatMessage?,
    replyText: String?,
    highlighted: Boolean,
    menuOpen: Boolean,
    onReply: () -> Unit,
    onMenu: () -> Unit,
    onDismissMenu: () -> Unit,
    onOperation: (MessageOperation) -> Unit,
    onPreview: () -> Unit,
    onCard: () -> Unit,
    discussionSummary: ChannelDiscussionSummary?,
    onComments: (() -> Unit)?,
) {
    val context = LocalContext.current
    val actionsLabel = stringResource(R.string.status_message_actions)
    val broadcast = room.type == "channel"
    val own = !broadcast && message.sender == username
    val sender = if (broadcast) room.title else message.sender
    val dayLabel = messageDay(message.sentAt)
    val olderDayLabel = older?.let { messageDay(it.sentAt) }
    val continued = dayLabel.isNotBlank() && older != null &&
        (broadcast || older.sender == message.sender) && olderDayLabel == dayLabel
    val newDay = dayLabel.isNotBlank() && olderDayLabel != dayLabel
    val dark = MaterialTheme.colorScheme.background.luminance() < .5f
    val shape = when {
        own && continued -> RoundedCornerShape(16.dp, 6.dp, 6.dp, 16.dp)
        own -> RoundedCornerShape(16.dp, 16.dp, 4.dp, 16.dp)
        continued -> RoundedCornerShape(6.dp, 16.dp, 16.dp, 6.dp)
        else -> RoundedCornerShape(16.dp, 16.dp, 16.dp, 4.dp)
    }
    val bubbleColor = when {
        highlighted -> MaterialTheme.colorScheme.primaryContainer
        own && dark -> Color(0xff24506a)
        own -> Color(0xffe2f4d7)
        else -> MaterialTheme.colorScheme.surface
    }

    Column(Modifier.fillMaxWidth()) {
        if (newDay) {
            Box(Modifier.fillMaxWidth().padding(vertical = 8.dp), contentAlignment = Alignment.Center) {
                Surface(color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = .78f), shape = CircleShape) {
                    Text(
                        dayLabel,
                        color = MaterialTheme.colorScheme.surface,
                        style = MaterialTheme.typography.labelMedium,
                        modifier = Modifier.padding(horizontal = 12.dp, vertical = 3.dp),
                    )
                }
            }
        }
        Row(
            Modifier.fillMaxWidth(),
            horizontalArrangement = if (own) Arrangement.End else Arrangement.Start,
            verticalAlignment = Alignment.Bottom,
        ) {
            if (!own) {
                if (!continued) RoomAvatar(sender, if (broadcast) "channel" else "direct", 28.dp) else Spacer(Modifier.width(28.dp))
                Spacer(Modifier.width(7.dp))
            }
            Column(
                Modifier.widthIn(max = 316.dp)
                    .clip(shape)
                    .background(bubbleColor)
                    .semantics {
                        if (!message.deleted) customActions = listOf(CustomAccessibilityAction(actionsLabel) { onMenu(); true })
                    }
                    .combinedClickable(
                        enabled = !message.deleted,
                        onClick = {},
                        onLongClickLabel = stringResource(R.string.status_message_actions),
                        onLongClick = onMenu,
                    )
                    .padding(horizontal = 10.dp, vertical = 6.dp),
            ) {
                if (!own && !continued) {
                    Text(sender, color = MaterialTheme.colorScheme.primary, style = MaterialTheme.typography.labelMedium)
                }
                message.forwardedFrom?.let {
                    Text(
                        stringResource(R.string.conv_forwarded_from, it),
                        color = MaterialTheme.colorScheme.primary,
                        style = MaterialTheme.typography.labelSmall,
                    )
                }
                message.replyTo?.let { sequence ->
                    Text(
                        replyText ?: stringResource(R.string.conv_quoted, sequence.toInt()),
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                        color = MaterialTheme.colorScheme.primary,
                        style = MaterialTheme.typography.bodySmall,
                        modifier = Modifier.padding(bottom = 3.dp),
                    )
                }
                if (message.deleted) {
                    Text(stringResource(R.string.conv_deleted), color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.bodyMedium)
                    if (message.discussionRoot && onComments != null) TextButton(onClick = onComments) {
                        Text(stringResource(R.string.channel_comments))
                    }
                } else {
                    if (message.text.isNotBlank()) Text(message.text, style = MaterialTheme.typography.bodyLarge)
                    message.attachment?.let { attachment ->
                        Surface(
                            onClick = onPreview,
                            color = Color.Transparent,
                            shape = RoundedCornerShape(10.dp),
                            modifier = Modifier.widthIn(max = 260.dp),
                        ) {
                            Row(Modifier.padding(vertical = 8.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                                Box(Modifier.size(42.dp).background(MaterialTheme.colorScheme.primary.copy(alpha = .12f), CircleShape), contentAlignment = Alignment.Center) {
                                    Text("↓", color = MaterialTheme.colorScheme.primary, style = MaterialTheme.typography.titleLarge)
                                }
                                Column(Modifier.weight(1f, fill = false)) {
                                    Text(attachment.name, maxLines = 2, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.primary)
                                    Text(Formatter.formatShortFileSize(context, attachment.size), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                                }
                            }
                        }
                    }
                    message.card?.let {
                        TextButton(onClick = onCard) { Text(cardTitle(it.kind)) }
                    }
                    if (message.forwardedFrom == null && message.text.isBlank() && message.attachment == null && message.card == null) {
                        Text(" ", style = MaterialTheme.typography.bodyLarge)
                    }
                    if (onComments != null) TextButton(onClick = onComments) {
                        Text(stringResource(R.string.channel_comments))
                        if (discussionSummary != null) {
                            Text(" · " + discussionSummary.count)
                            if (discussionSummary.unread > 0) Text(" · " + stringResource(
                                R.string.channel_comments_unread, discussionSummary.unread))
                        }
                    }
                    Row(
                        modifier = Modifier.align(Alignment.End).padding(top = 2.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(4.dp),
                    ) {
                        if (broadcast && room.signPosts) Text(message.sender,
                            modifier = Modifier.weight(1f, fill = false), maxLines = 1, overflow = TextOverflow.Ellipsis,
                            color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.labelSmall)
                        if (message.edited) Text(stringResource(R.string.conv_edited), color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.labelSmall)
                        messageTime(message.sentAt).takeIf(String::isNotEmpty)?.let {
                            Text(it, color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.labelSmall)
                        }
                    }
                    if (menuOpen) {
                        MessageMenu(
                            canEdit = permissions.edit,
                            canDelete = permissions.delete,
                            canPin = permissions.pin,
                            pinned = room.pinnedMessage == message.sequence,
                            enabled = enabled,
                            dismiss = onDismissMenu,
                            reply = if (permissions.reply) ({ onDismissMenu(); onReply() }) else null,
                        ) { operation -> onOperation(operation) }
                    }
                }
            }
        }
    }
}

private fun Color.luminance(): Float = .2126f * red + .7152f * green + .0722f * blue
