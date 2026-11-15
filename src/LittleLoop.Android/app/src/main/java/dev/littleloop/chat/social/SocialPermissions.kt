package dev.littleloop.chat

internal enum class GroupMemberOperation(val wire: String) {
    Remove("remove"), Silence("silence"), Unsilence("unsilence"),
    Promote("promote"), Demote("demote"), Transfer("transfer"),
}

internal data class FriendPermissions(val respond: Boolean, val openChat: Boolean)
internal fun ChatFriend.permissions() = FriendPermissions(state == "incoming", state == "friend")

/** Affordances and submission guards share these rules; the server authorizes mutations. */
internal data class GroupPermissions(val invite: Boolean, val reviewJoins: Boolean)
internal fun Room.groupPermissions() = GroupPermissions(type == "group" && canManage, type == "group" && canManage)

internal fun Room.memberOperations(member: ChatMember, username: String): List<GroupMemberOperation> {
    if (type != "group" || !canModerate || member.owner || member.robot || member.username == username ||
        (!canManage && member.administrator)) return emptyList()
    return buildList {
        add(GroupMemberOperation.Remove)
        add(if (member.silenced) GroupMemberOperation.Unsilence else GroupMemberOperation.Silence)
        if (canManage) {
            add(if (member.administrator) GroupMemberOperation.Demote else GroupMemberOperation.Promote)
            add(GroupMemberOperation.Transfer)
        }
    }
}
