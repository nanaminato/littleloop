package dev.littleloop.chat

import androidx.compose.runtime.saveable.SaverScope
import org.junit.Assert.*
import org.junit.Test

class ConversationStateTest {
    @Test fun recreationPreservesDraftAndDeepLinkStateButNotTransientOperations() {
        val original = ConversationUiState().apply {
            draft = "draft written while offline"; replyTo = 7; commentPost = 12; preview = 3
            asking = true; scheduleFrom = 9; botDetails = 15
            menu = 7; channelSettings = true; messageOperation = 7L to MessageOperation.Delete
        }
        val scope = object : SaverScope { override fun canBeSaved(value: Any) = true }
        val saved = with(ConversationUiState.Saver) { scope.save(original) }!!
        val restored = ConversationUiState.Saver.restore(saved)!!
        assertEquals(original.draft, restored.draft); assertEquals(original.replyTo, restored.replyTo)
        assertEquals(original.commentPost, restored.commentPost); assertEquals(original.preview, restored.preview)
        assertEquals(original.asking, restored.asking); assertEquals(original.scheduleFrom, restored.scheduleFrom)
        assertEquals(original.botDetails, restored.botDetails)
        assertNull(restored.menu); assertNull(restored.messageOperation); assertNull(restored.file)
        assertFalse(restored.channelSettings)
    }
}
