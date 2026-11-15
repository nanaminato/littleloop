package dev.littleloop.chat

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.runtime.collectAsState
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

@Composable
internal fun SettingsScreen(
    chat: ChatRepository,
    state: ChatState,
    theme: String,
    onLanguage: (String) -> Unit,
) {
    val context = LocalContext.current
    val service = remember { Intent(context, ChatNotificationService::class.java) }
    val listening by chat.backgroundEnabled.collectAsState()
    val language by chat.language.collectAsState()
    val cloudEnabled by chat.push.enabled.collectAsState()
    val scope = rememberCoroutineScope()
    var cloudBusy by remember { mutableStateOf(false) }
    var cloudError by remember { mutableStateOf<String?>(null) }
    val cloudServerMissing = stringResource(R.string.settings_cloud_server_missing)
    val cloudFailed = stringResource(R.string.settings_cloud_failed)
    val cloudRegisterFailed = stringResource(R.string.settings_cloud_register_failed)
    val notificationsAllowed = stringResource(R.string.settings_cloud_notifications_allowed)

    val profile = state.settings.optJSONObject("profile")
    val savedName = profile?.optString("displayName").orEmpty()
    val savedBio = profile?.optString("bio").orEmpty()
    var name by rememberSaveable(savedName) { mutableStateOf(savedName) }
    var bio by rememberSaveable(savedBio) { mutableStateOf(savedBio) }
    val notifications = state.settings.optJSONObject("notifications")

    val setCloud: (Boolean) -> Unit = { value ->
        if (!cloudBusy) {
            cloudBusy = true
            cloudError = null
            scope.launch {
                try {
                    chat.push.setEnabled(value)
                    if (value && chat.push.enabled.value) context.stopService(service)
                } catch (cancelled: CancellationException) {
                    throw cancelled
                } catch (error: ChatRequestException) {
                    cloudError = if (error.status == 404 || error.status == 400) cloudServerMissing
                    else cloudFailed
                } catch (_: Exception) {
                    cloudError = cloudRegisterFailed
                } finally {
                    cloudBusy = false
                }
            }
        }
    }
    val cloudPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) {
        if (it) setCloud(true) else cloudError = notificationsAllowed
    }
    val backgroundPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) {
        if (it) context.startForegroundService(service) else cloudError = notificationsAllowed
    }

    Column(
        Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = 12.dp, vertical = 10.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 4.dp, vertical = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Box(
                Modifier.size(56.dp).background(MaterialTheme.colorScheme.primary, CircleShape),
                contentAlignment = Alignment.Center,
            ) {
                Text(
                    (savedName.ifBlank { state.username }).take(1).uppercase(),
                    color = MaterialTheme.colorScheme.onPrimary,
                    style = MaterialTheme.typography.titleLarge,
                )
            }
            Column(Modifier.weight(1f)) {
                Text(savedName.ifBlank { state.username }, style = MaterialTheme.typography.titleMedium)
                Text("@${state.username}", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }

        SectionCard(stringResource(R.string.settings_profile)) {
            Column(Modifier.padding(horizontal = 16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                OutlinedTextField(
                    name, { if (it.length <= 60) name = it }, Modifier.fillMaxWidth(),
                    label = { Text(stringResource(R.string.settings_display_name)) },
                    singleLine = true, shape = MaterialTheme.shapes.medium,
                )
                OutlinedTextField(
                    bio, { if (it.length <= 240) bio = it }, Modifier.fillMaxWidth(),
                    label = { Text(stringResource(R.string.settings_bio)) },
                    maxLines = 3, shape = MaterialTheme.shapes.medium,
                )
                Button(
                    onClick = { chat.profile(name, bio) },
                    enabled = !state.busy && (name != savedName || bio != savedBio),
                    modifier = Modifier.fillMaxWidth().height(46.dp),
                ) { Text(stringResource(R.string.settings_save_profile)) }
            }
        }

        SectionCard(stringResource(R.string.settings_notifications)) {
            SettingToggle(stringResource(R.string.settings_notify_direct), notifications?.optBoolean("direct", true) ?: true, !state.busy) {
                chat.save("notifications", "direct", it)
            }
            SettingToggle(stringResource(R.string.settings_notify_groups), notifications?.optBoolean("groups", true) ?: true, !state.busy) {
                chat.save("notifications", "groups", it)
            }
            SettingToggle(stringResource(R.string.settings_notify_channels), notifications?.optBoolean("channels", true) ?: true, !state.busy) {
                chat.save("notifications", "channels", it)
            }
            SettingToggle(stringResource(R.string.settings_notify_preview), notifications?.optBoolean("preview", true) ?: true, !state.busy) {
                chat.save("notifications", "preview", it)
            }
            if (chat.push.configured) {
                SettingToggle(stringResource(R.string.settings_cloud_notifications), cloudEnabled, !cloudBusy) { value ->
                    if (value && Build.VERSION.SDK_INT >= 33 &&
                        context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
                    ) {
                        cloudPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
                    } else {
                        setCloud(value)
                    }
                }
            } else {
                Text(
                    stringResource(R.string.settings_cloud_unconfigured),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(horizontal = 16.dp),
                )
            }
            if (!cloudEnabled) {
                SettingToggle(stringResource(R.string.settings_background_notifications), listening, true) { enabled ->
                    if (!enabled) {
                        context.stopService(service)
                    } else if (Build.VERSION.SDK_INT >= 33 &&
                        context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
                    ) {
                        backgroundPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
                    } else {
                        context.startForegroundService(service)
                    }
                }
                Text(
                    stringResource(R.string.settings_background_note),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(horizontal = 16.dp, vertical = 2.dp),
                )
            }
            cloudError?.let {
                Text(
                    it,
                    color = MaterialTheme.colorScheme.error,
                    style = MaterialTheme.typography.bodySmall,
                    modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp),
                )
            }
        }

        SectionCard(stringResource(R.string.settings_appearance)) {
            FlowRow(
                Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 6.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                for ((value, label) in listOf(
                    "system" to stringResource(R.string.settings_theme_system),
                    "light" to stringResource(R.string.settings_theme_light),
                    "dark" to stringResource(R.string.settings_theme_dark),
                )) {
                    ChoiceChip(theme == value, { chat.theme(value) }, label)
                }
            }
        }

        SectionCard(stringResource(R.string.settings_language)) {
            FlowRow(
                Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 6.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                for ((value, label) in listOf(
                    "system" to stringResource(R.string.settings_language_system),
                    "zh" to stringResource(R.string.settings_language_zh),
                    "en" to stringResource(R.string.settings_language_en),
                )) {
                    ChoiceChip(language == value, { onLanguage(value) }, label)
                }
            }
        }

        state.error?.let {
            Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(horizontal = 8.dp))
        }

        TextButton(
            onClick = {
                context.stopService(service)
                chat.logout()
            },
            enabled = !state.busy,
            modifier = Modifier.fillMaxWidth(),
        ) { Text(stringResource(R.string.settings_logout), color = MaterialTheme.colorScheme.error) }
    }
}
