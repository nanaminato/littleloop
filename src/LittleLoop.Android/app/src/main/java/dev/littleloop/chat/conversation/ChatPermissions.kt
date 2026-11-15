package dev.littleloop.chat

/** Client affordances only. The server remains authoritative for every mutation. */
internal enum class ChannelRight(val bit: Int) {
    Publish(1), Edit(2), Delete(4), Pin(8), Profile(16), Subscribers(32), Discussion(64),
}

internal fun Room.hasRight(right: ChannelRight) = type == "channel" && channelRights and right.bit != 0

internal data class MessagePermissions(
    val reply: Boolean, val edit: Boolean, val delete: Boolean, val pin: Boolean,
    val forward: Boolean, val save: Boolean,
) {
    fun allows(operation: MessageOperation) = when (operation) {
        MessageOperation.Edit -> edit
        MessageOperation.Delete -> delete
        MessageOperation.Pin -> pin
        MessageOperation.Forward -> forward
        MessageOperation.Save -> save
    }
}

internal fun Room.messagePermissions(message: ChatMessage, username: String): MessagePermissions {
    val available = !message.deleted
    val editable = available && message.card == null && !message.discussionRoot
    val own = message.sender == username
    return MessagePermissions(
        reply = available && canPost,
        edit = editable && if (type == "channel") hasRight(ChannelRight.Edit) else own,
        delete = editable && if (type == "channel") hasRight(ChannelRight.Delete) else own || canModerate,
        pin = available && if (type == "channel") hasRight(ChannelRight.Pin) else canPost,
        forward = available,
        save = available,
    )
}

internal data class ChannelPermissions(val profile: Boolean, val subscribers: Boolean,
    val discussion: Boolean, val owner: Boolean)

internal fun Room.channelPermissions(settings: ChannelSettings?) = ChannelPermissions(
    hasRight(ChannelRight.Profile), hasRight(ChannelRight.Subscribers),
    hasRight(ChannelRight.Discussion), settings?.owner == true,
)

internal fun Room.canForwardTo() = canPost && type !in setOf("saved", "legacy") && !archived
