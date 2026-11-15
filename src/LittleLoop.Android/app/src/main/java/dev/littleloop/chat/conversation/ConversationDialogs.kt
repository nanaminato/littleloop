package dev.littleloop.chat

import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember

@Composable
internal fun ConversationDialogs(chat: ChatRepository, state: ChatState, room: Room,
    ui: ConversationUiState, robot: BotRoom?) {
    val bySequence = remember(state.messages) { state.messages.associateBy(ChatMessage::sequence) }
    if (ui.channelSettings) ChannelAdministration(chat, state, room) { ui.channelSettings = false }
    bySequence[ui.commentPost]?.let { ChannelComments(chat, state, room, it) { ui.commentPost = null } }
    bySequence[ui.preview]?.takeUnless(ChatMessage::deleted)?.let { message ->
        AttachmentDialog(chat, room.id, message) { ui.preview = null }
    }
    ui.messageOperation?.let { (sequence, operation) ->
        bySequence[sequence]?.let { message ->
            MessageOperationDialog(chat, state, room, message, operation, dismiss = { ui.messageOperation = null }) { result ->
                ui.messageOperation = null
                ui.operationResult = result
            }
        }
    }
    bySequence[ui.botDetails]?.takeUnless(ChatMessage::deleted)?.card?.let { card ->
        BotDetails(chat.bot, card) { ui.botDetails = null }
    }
    robot?.providers?.takeIf { it.isNotEmpty() }?.let { providers ->
        if (ui.asking) BotRequestDialog(chat.bot, room.id, providers, null) { ui.asking = false }
        bySequence[ui.scheduleFrom]?.takeUnless(ChatMessage::deleted)?.let { message ->
            BotRequestDialog(chat.bot, room.id, providers, message) { ui.scheduleFrom = null }
        }
    }
}
