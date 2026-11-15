package dev.littleloop.chat

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp

@Composable
internal fun SocialUsername(value: String, update: (String) -> Unit, label: String, action: String,
                           enabled: Boolean, submit: () -> Unit) {
    Column(
        Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
        verticalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        OutlinedTextField(
            value, { if (it.length <= 32) update(it) }, Modifier.fillMaxWidth(),
            label = { Text(label) }, singleLine = true, shape = MaterialTheme.shapes.medium,
        )
        TextButton(enabled = enabled && value.isNotBlank(), onClick = submit, modifier = Modifier.align(Alignment.End)) { Text(action) }
    }
}

@Composable
internal fun SocialError(error: String?) {
    if (error != null && error != "Offline") {
        Text(
            error,
            Modifier.padding(horizontal = 16.dp, vertical = 4.dp),
            color = MaterialTheme.colorScheme.error,
            style = MaterialTheme.typography.bodySmall,
        )
    }
}
