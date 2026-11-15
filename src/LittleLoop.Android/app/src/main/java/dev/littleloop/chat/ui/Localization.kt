package dev.littleloop.chat

import android.content.Context
import android.content.res.Configuration
import java.util.Locale

/**
 * App-level language preference. Android 13 per-app locales would cover newer
 * devices, but the app supports API 26, so the activity context and the
 * application services share this small configuration wrapper instead.
 */
internal object Localization {
    const val PREFERENCES = "chat_preferences"
    const val KEY = "language"

    fun saved(base: Context): String =
        base.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE).getString(KEY, "system") ?: "system"

    fun context(base: Context, language: String): Context {
        if (language != "zh" && language != "en") return base
        val locale = if (language == "zh") Locale.SIMPLIFIED_CHINESE else Locale.ENGLISH
        val configuration = Configuration(base.resources.configuration)
        configuration.setLocale(locale)
        return base.createConfigurationContext(configuration)
    }

    fun localized(base: Context): Context = context(base, saved(base))

    fun localeFor(language: String, system: Locale): Locale = when (language) {
        "zh" -> Locale.SIMPLIFIED_CHINESE
        "en" -> Locale.ENGLISH
        else -> system
    }
}
