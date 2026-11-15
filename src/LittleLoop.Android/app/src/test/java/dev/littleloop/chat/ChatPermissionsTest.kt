package dev.littleloop.chat

import org.junit.Assert.*
import org.junit.Test

class ChatPermissionsTest {
    private fun room(type: String = "group", rights: Int = 0, canPost: Boolean = true, moderator: Boolean = false) =
        Room("room", "Room", type, 0, 0, 0, false, false, canPost, "", 0, null, null,
            channelRights = rights, canModerate = moderator)
    private val peerMessage = ChatMessage(1, "request", "peer", "message", false, "2026-11-15T00:00:00Z",
        null, null, null, false, null)

    @Test fun ordinaryMemberCannotEditOrDeletePeersButModeratorCanDelete() {
        val member = room().messagePermissions(peerMessage, "viewer")
        assertFalse(member.edit); assertFalse(member.delete); assertTrue(member.reply)
        val moderator = room(moderator = true).messagePermissions(peerMessage, "viewer")
        assertFalse(moderator.edit); assertTrue(moderator.delete)
    }

    @Test fun silencingStopsRepliesAndPinsWithoutRemovingOwnershipOfOldMessages() {
        val permissions = room(canPost = false).messagePermissions(peerMessage, "peer")
        assertFalse(permissions.reply); assertFalse(permissions.pin)
        assertTrue(permissions.edit); assertTrue(permissions.delete); assertTrue(permissions.forward)
    }

    @Test fun channelAuthorshipDoesNotGrantAdministrativeRights() {
        val permissions = room("channel", canPost = false).messagePermissions(peerMessage, "peer")
        assertFalse(permissions.edit); assertFalse(permissions.delete); assertFalse(permissions.pin)
        assertTrue(permissions.save)
    }

    @Test fun eachChannelGrantOnlyEnablesItsOwnMessageOperation() {
        val expected = mapOf(1 to emptySet(), 2 to setOf(MessageOperation.Edit),
            4 to setOf(MessageOperation.Delete), 8 to setOf(MessageOperation.Pin),
            16 to emptySet(), 32 to emptySet(), 64 to emptySet())
        for ((rights, allowed) in expected) {
            val permissions = room("channel", rights, canPost = rights == 1).messagePermissions(peerMessage, "viewer")
            for (operation in listOf(MessageOperation.Edit, MessageOperation.Delete, MessageOperation.Pin)) {
                assertEquals("grant=$rights, operation=$operation", operation in allowed, permissions.allows(operation))
            }
        }
    }

    @Test fun fullChannelGrantIsNotOwnership() {
        val permissions = room("channel", 127).channelPermissions(ChannelSettings(false, null, emptyList()))
        assertTrue(permissions.profile); assertTrue(permissions.subscribers); assertTrue(permissions.discussion)
        assertFalse(permissions.owner)
    }

    @Test fun protectedCardsAndMirroredPostsCannotBeEditedOrDeletedEvenByOwners() {
        for (message in listOf(peerMessage.copy(card = BotCard("task", null, null)), peerMessage.copy(discussionRoot = true))) {
            for (room in listOf(room(moderator = true), room("channel", 127))) {
                val permissions = room.messagePermissions(message, "peer")
                assertFalse(permissions.edit); assertFalse(permissions.delete)
            }
        }
    }

    @Test fun deletionOrRevocationInvalidatesOpenOperationPermissions() {
        val channel = room("channel", 127)
        assertTrue(channel.messagePermissions(peerMessage, "viewer").edit)
        val revoked = channel.copy(channelRights = 0, canPost = false).messagePermissions(peerMessage, "viewer")
        assertFalse(revoked.allows(MessageOperation.Edit)); assertFalse(revoked.allows(MessageOperation.Delete))
        val deleted = channel.messagePermissions(peerMessage.copy(deleted = true), "viewer")
        MessageOperation.entries.forEach { assertFalse(deleted.allows(it)) }
        assertFalse(deleted.reply)
    }

    @Test fun forwardTargetsExcludeUnavailableAndSpecialRooms() {
        assertTrue(room().canForwardTo())
        assertFalse(room(canPost = false).canForwardTo())
        assertFalse(room("saved").canForwardTo()); assertFalse(room("legacy").canForwardTo())
        assertFalse(room().copy(archived = true).canForwardTo())
    }
}
