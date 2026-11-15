package dev.littleloop.chat

import androidx.compose.foundation.layout.Box
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics

@Composable
internal fun ConversationHeader(room: Room, connected: Boolean, unread: Long, robot: BotRoom?, installing: Boolean,
    onBack: () -> Unit, onSearch: () -> Unit, onMute: () -> Unit, onChannelSettings: () -> Unit,
    onTopics: () -> Unit, onMembers: () -> Unit, onAsk: () -> Unit, onInstall: () -> Unit) {
    var headerMenu by remember(room.id) { mutableStateOf(false) }
    val searchLabel = stringResource(R.string.conv_search_in)
    val moreLabel = stringResource(R.string.conv_more_actions)
    ChatTopBar(
        title = room.title,
        subtitle = roomTypeLabel(room.type).takeUnless { room.type == "saved" || it == room.title },
        back = onBack,
        actions = {
            TextButton(onClick = onSearch, modifier = Modifier.semantics {
                contentDescription = searchLabel
            }) { Text("⌕", style = MaterialTheme.typography.titleLarge) }
            Box {
                TextButton(onClick = { headerMenu = true }, modifier = Modifier.semantics {
                    contentDescription = moreLabel
                }) { Text("⋮", style = MaterialTheme.typography.titleLarge) }
                DropdownMenu(
                    expanded = headerMenu,
                    onDismissRequest = { headerMenu = false },
                    containerColor = MaterialTheme.colorScheme.surface,
                ) {
                    DropdownMenuItem(
                        text = { Text(stringResource(if (room.muted) R.string.action_unmute else R.string.action_mute)) },
                        onClick = { headerMenu = false; onMute() },
                    )
                    if (room.type == "channel") DropdownMenuItem(
                        text = { Text(stringResource(R.string.channel_settings)) },
                        onClick = { headerMenu = false; onChannelSettings() },
                    )
                    if (room.type == "group") {
                        DropdownMenuItem(
                            text = {
                                Text(
                                    if (unread == 0L) stringResource(R.string.conv_topics)
                                    else stringResource(R.string.conv_topics_unread, unread.toInt()),
                                )
                            },
                            onClick = { headerMenu = false; onTopics() },
                        )
                        DropdownMenuItem(
                            text = { Text(stringResource(R.string.conv_members)) },
                            onClick = { headerMenu = false; onMembers() },
                        )
                    }
                    if (connected && robot?.canOperate == true) {
                        if (robot?.installed == true) {
                            DropdownMenuItem(
                                text = { Text(stringResource(R.string.conv_bot_ask)) },
                                onClick = { headerMenu = false; onAsk() },
                            )
                        } else if (room.type != "topic") {
                            DropdownMenuItem(
                                text = { Text(stringResource(R.string.conv_bot_install)) },
                                enabled = !installing,
                                onClick = {
                                    headerMenu = false
                                    onInstall()
                                },
                            )
                        }
                    }
                }
            }
        },
    )
}
