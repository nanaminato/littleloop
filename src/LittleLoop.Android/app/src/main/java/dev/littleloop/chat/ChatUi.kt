package dev.littleloop.chat

import androidx.compose.foundation.background
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Badge
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FilterChipDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import java.text.DateFormat
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Date

private val messageTime = DateTimeFormatter.ofPattern("HH:mm")

internal fun messageTime(sentAt: String): String =
    runCatching { messageTime.format(Instant.parse(sentAt).atZone(ZoneId.systemDefault())) }.getOrDefault("")

internal fun messageDay(sentAt: String): String = runCatching {
    DateFormat.getDateInstance(DateFormat.MEDIUM).format(Date.from(Instant.parse(sentAt)))
}.getOrDefault("")

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun ChatTopBar(
    title: String,
    subtitle: String? = null,
    back: (() -> Unit)? = null,
    actions: @Composable RowScope.() -> Unit = {},
) {
    TopAppBar(
        navigationIcon = {
            if (back != null) {
                TextButton(onClick = back) {
                    Text("‹", fontSize = 30.sp, lineHeight = 30.sp)
                }
            }
        },
        title = {
            Column(Modifier.padding(start = if (back == null) 6.dp else 0.dp)) {
                Text(title, maxLines = 1, overflow = TextOverflow.Ellipsis)
                if (subtitle != null) {
                    Text(
                        subtitle,
                        style = MaterialTheme.typography.labelMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
            }
        },
        actions = actions,
        windowInsets = WindowInsets(0),
        colors = TopAppBarDefaults.topAppBarColors(
            containerColor = MaterialTheme.colorScheme.surface,
            titleContentColor = MaterialTheme.colorScheme.onSurface,
            navigationIconContentColor = MaterialTheme.colorScheme.primary,
            actionIconContentColor = MaterialTheme.colorScheme.primary,
        ),
    )
}

@Composable
internal fun ChatScreenScaffold(
    title: String,
    subtitle: String? = null,
    back: (() -> Unit)? = null,
    actions: @Composable RowScope.() -> Unit = {},
    content: @Composable ColumnScope.() -> Unit,
) {
    Column(Modifier.fillMaxSize()) {
        ChatTopBar(title, subtitle, back, actions = actions)
        content()
    }
}

@Composable
internal fun BrandMark(size: Dp = 40.dp) {
    Box(
        Modifier.size(size).clip(RoundedCornerShape(size * 0.32f))
            .background(Brush.linearGradient(listOf(Color(0xff2a94ca), Color(0xff0e5c87)))),
        contentAlignment = Alignment.Center,
    ) {
        Text("L", color = Color.White, fontWeight = FontWeight.ExtraBold, fontSize = (size.value * 0.46f).sp)
    }
}

@Composable
internal fun RoomAvatar(title: String, type: String, size: Dp = 52.dp) {
    val colors = when (type) {
        "group", "topic" -> listOf(Color(0xff58a58f), Color(0xff337a68))
        "channel" -> listOf(Color(0xffa283bd), Color(0xff74549b))
        "saved" -> listOf(Color(0xffd7a45c), Color(0xffad7430))
        else -> listOf(Color(0xff4a8fc0), Color(0xff2b6395))
    }
    Box(
        Modifier.size(size).background(Brush.linearGradient(colors), CircleShape),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            title.trim().firstOrNull()?.uppercase() ?: "?",
            color = Color.White,
            fontWeight = FontWeight.Bold,
            fontSize = (size.value * 0.36f).sp,
        )
    }
}

@Composable
internal fun roomTypeLabel(type: String): String = when (type) {
    "direct" -> androidx.compose.ui.res.stringResource(R.string.home_direct)
    "group" -> androidx.compose.ui.res.stringResource(R.string.home_group)
    "topic" -> androidx.compose.ui.res.stringResource(R.string.room_topic)
    "channel" -> androidx.compose.ui.res.stringResource(R.string.room_channel)
    "saved" -> androidx.compose.ui.res.stringResource(R.string.action_saved_messages)
    else -> androidx.compose.ui.res.stringResource(R.string.room_history)
}

@Composable
internal fun UnreadBadge(count: Long, muted: Boolean) {
    if (count <= 0) return
    Badge(
        containerColor = if (muted) MaterialTheme.colorScheme.onSurfaceVariant else MaterialTheme.colorScheme.primary,
        contentColor = MaterialTheme.colorScheme.onPrimary,
    ) {
        Text(if (count > 99) "99+" else count.toString(), style = MaterialTheme.typography.labelMedium)
    }
}

@Composable
internal fun RoomRow(room: Room, selected: Boolean = false, onClick: () -> Unit) {
    Surface(
        color = if (selected) MaterialTheme.colorScheme.primaryContainer.copy(alpha = .55f) else Color.Transparent,
        shape = MaterialTheme.shapes.medium,
        modifier = Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 2.dp),
    ) {
        Row(
            Modifier.fillMaxWidth().clickable(onClick = onClick).padding(horizontal = 10.dp, vertical = 9.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            RoomAvatar(room.title, room.type)
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        room.title,
                        modifier = Modifier.weight(1f, fill = false),
                        fontWeight = FontWeight.SemiBold,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                    if (room.pinnedMessage != null) {
                        Spacer(Modifier.width(4.dp))
                        Text("⌖", color = MaterialTheme.colorScheme.onSurfaceVariant, fontSize = 12.sp)
                    }
                    if (room.muted) {
                        Spacer(Modifier.width(4.dp))
                        Text("◌", color = MaterialTheme.colorScheme.onSurfaceVariant, fontSize = 12.sp)
                    }
                }
                Text(
                    if (room.previewDeleted) androidx.compose.ui.res.stringResource(R.string.conv_deleted)
                    else room.preview.ifBlank { roomTypeLabel(room.type) },
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
            UnreadBadge(room.unread, room.muted)
        }
    }
}

@Composable
internal fun SectionCard(title: String? = null, content: @Composable () -> Unit) {
    Surface(
        color = MaterialTheme.colorScheme.surface,
        shape = MaterialTheme.shapes.large,
        tonalElevation = 1.dp,
        shadowElevation = 1.dp,
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outlineVariant),
        modifier = Modifier.fillMaxWidth(),
    ) {
        Column(Modifier.padding(vertical = 6.dp)) {
            if (title != null) {
                Text(
                    title.uppercase(),
                    style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp),
                )
            }
            content()
        }
    }
}

@Composable
internal fun ChoiceChip(selected: Boolean, onClick: () -> Unit, label: String) {
    FilterChip(
        selected = selected,
        onClick = onClick,
        label = { Text(label) },
        colors = FilterChipDefaults.filterChipColors(
            selectedContainerColor = MaterialTheme.colorScheme.primaryContainer,
            selectedLabelColor = MaterialTheme.colorScheme.onPrimaryContainer,
        ),
    )
}

@Composable
internal fun SettingToggle(label: String, checked: Boolean, enabled: Boolean, changed: (Boolean) -> Unit) {
    Row(
        Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(label, Modifier.weight(1f), style = MaterialTheme.typography.bodyLarge)
        Switch(checked, changed, enabled = enabled)
    }
}

@Composable
internal fun EmptyState(mark: String, title: String, hint: String? = null) {
    Column(
        Modifier.fillMaxWidth().padding(32.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Box(
            Modifier.size(58.dp).background(MaterialTheme.colorScheme.primaryContainer, CircleShape),
            contentAlignment = Alignment.Center,
        ) {
            Text(mark, color = MaterialTheme.colorScheme.primary, fontSize = 24.sp)
        }
        Text(title, style = MaterialTheme.typography.titleMedium)
        if (hint != null) {
            Text(
                hint,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

@Composable
internal fun ConnectionDot(connected: Boolean) {
    Box(
        Modifier.size(9.dp).background(
            if (connected) Color(0xff2fa36b) else Color(0xffd07a3a),
            CircleShape,
        ),
    )
}

@Composable
internal fun ChatDivider(modifier: Modifier = Modifier) {
    Box(modifier.fillMaxWidth().height(1.dp).background(MaterialTheme.colorScheme.outlineVariant))
}
