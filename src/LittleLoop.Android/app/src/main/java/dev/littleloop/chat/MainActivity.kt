package dev.littleloop.chat

import android.content.Context
import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.toArgb
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.ui.Modifier
import androidx.core.view.WindowCompat
import java.util.Locale
import java.util.UUID

class MainActivity : ComponentActivity() {
    private val chat get() = (application as ChatApplication).chat

    override fun attachBaseContext(newBase: Context) {
        val language = Localization.saved(newBase)
        val system = newBase.resources.configuration.locales[0]
        Locale.setDefault(Localization.localeFor(language, system))
        super.attachBaseContext(Localization.context(newBase, language))
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            val state by chat.state.collectAsState()
            val theme by chat.theme.collectAsState()
            LittleLoopTheme(theme) {
                val dark = MaterialTheme.colorScheme.background.luminance() < .5f
                val surface = MaterialTheme.colorScheme.surface
                SideEffect {
                    window.statusBarColor = Color.Transparent.toArgb()
                    window.navigationBarColor = surface.toArgb()
                    WindowCompat.getInsetsController(window, window.decorView).isAppearanceLightStatusBars = !dark
                    WindowCompat.getInsetsController(window, window.decorView).isAppearanceLightNavigationBars = !dark
                }
                Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
                    ChatApp(chat, state, theme, onLanguage = { language ->
                        chat.language(language)
                        recreate()
                    })
                }
            }
        }
        openNotification(intent)
    }

    override fun onResume() { super.onResume(); chat.foreground(true) }
    override fun onPause() { chat.foreground(false); super.onPause() }
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        openNotification(intent)
    }

    private fun openNotification(intent: Intent?) {
        val supplied = intent?.getStringExtra("room") ?: return
        val room = runCatching { UUID.fromString(supplied).toString() }.getOrNull() ?: return
        val scope = intent.getStringExtra("pushScope")
        val sequence = intent.getLongExtra("sequence", 0)
        val comments = intent.getBooleanExtra("comments", false)
        if (scope != null) chat.openNotification(room, sequence, intent.getStringExtra("work"), scope, comments)
        else if (comments && sequence > 0) chat.openDiscussionLink(room, sequence)
        else chat.select(room)
        intent.removeExtra("room")
    }
}

private fun androidx.compose.ui.graphics.Color.luminance(): Float =
    .2126f * red + .7152f * green + .0722f * blue
