package dev.littleloop.chat

internal enum class CreationKind { Direct, Group, Channel, Topic }

/** Membership, not posting/moderation rights, controls topic creation on the server. */
internal data class CreationAccess(val username: String, val connected: Boolean, val busy: Boolean, val rooms: List<Room>) {
    fun allows(kind: CreationKind, parent: String?) = username.isNotEmpty() && connected && !busy &&
        (kind != CreationKind.Topic || rooms.any { it.id == parent && it.type == "group" })
}

internal fun ChatState.creationAccess() = CreationAccess(username, connected, busy, rooms)

internal data class DirectoryPermissions(val open: Boolean, val subscribe: Boolean)
internal fun ChatChannel.directoryPermissions() = DirectoryPermissions(subscribed, !owner)
