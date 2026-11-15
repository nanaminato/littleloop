package dev.littleloop.chat

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Shapes
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

private val LightColors = lightColorScheme(
    primary = Color(0xff1479b0),
    onPrimary = Color.White,
    primaryContainer = Color(0xffd8ecf9),
    onPrimaryContainer = Color(0xff0b3b57),
    secondary = Color(0xff2f7d6b),
    onSecondary = Color.White,
    secondaryContainer = Color(0xffddf3ec),
    onSecondaryContainer = Color(0xff123f35),
    tertiary = Color(0xff8a5a1c),
    background = Color(0xffeef4f8),
    onBackground = Color(0xff142530),
    surface = Color(0xfffbfdff),
    onSurface = Color(0xff142530),
    surfaceVariant = Color(0xffe3eef5),
    onSurfaceVariant = Color(0xff526b7b),
    surfaceTint = Color(0xff1479b0),
    surfaceContainerLowest = Color(0xffffffff),
    surfaceContainerLow = Color(0xfff5f9fc),
    surfaceContainer = Color(0xffeef4f8),
    surfaceContainerHigh = Color(0xffe8f0f6),
    surfaceContainerHighest = Color(0xffe2ebf2),
    inverseSurface = Color(0xff22323d),
    inverseOnSurface = Color(0xffeef6fb),
    outline = Color(0xffc9d9e3),
    outlineVariant = Color(0xffdbe7ef),
    error = Color(0xffb83a33),
    onError = Color.White,
    errorContainer = Color(0xfffbe4e1),
    onErrorContainer = Color(0xff6b1a15),
)

private val DarkColors = darkColorScheme(
    primary = Color(0xff7cc4f0),
    onPrimary = Color(0xff06293d),
    primaryContainer = Color(0xff1e3a4b),
    onPrimaryContainer = Color(0xffc7e7fa),
    secondary = Color(0xff7fd3bd),
    onSecondary = Color(0xff09372c),
    secondaryContainer = Color(0xff1d3b36),
    onSecondaryContainer = Color(0xffbcefe2),
    tertiary = Color(0xffe8bf82),
    background = Color(0xff101c24),
    onBackground = Color(0xffedf6fc),
    surface = Color(0xff182630),
    onSurface = Color(0xffedf6fc),
    surfaceVariant = Color(0xff223844),
    onSurfaceVariant = Color(0xffa8bdcc),
    surfaceTint = Color(0xff7cc4f0),
    surfaceContainerLowest = Color(0xff0b141a),
    surfaceContainerLow = Color(0xff131f27),
    surfaceContainer = Color(0xff182630),
    surfaceContainerHigh = Color(0xff1f2f3a),
    surfaceContainerHighest = Color(0xff263946),
    inverseSurface = Color(0xffe8f0f6),
    inverseOnSurface = Color(0xff1b2a34),
    outline = Color(0xff3e5a6a),
    outlineVariant = Color(0xff2c4250),
    error = Color(0xffff9187),
    onError = Color(0xff4a0d09),
    errorContainer = Color(0xff5a1d19),
    onErrorContainer = Color(0xffffdad5),
)

private val ChatShapes = Shapes(
    extraSmall = RoundedCornerShape(8.dp),
    small = RoundedCornerShape(10.dp),
    medium = RoundedCornerShape(14.dp),
    large = RoundedCornerShape(20.dp),
    extraLarge = RoundedCornerShape(26.dp),
)

private val ChatTypography = Typography(
    titleLarge = TextStyle(fontSize = 21.sp, lineHeight = 27.sp, fontWeight = FontWeight.SemiBold),
    titleMedium = TextStyle(fontSize = 16.sp, lineHeight = 22.sp, fontWeight = FontWeight.SemiBold),
    bodyLarge = TextStyle(fontSize = 16.sp, lineHeight = 23.sp),
    bodyMedium = TextStyle(fontSize = 14.sp, lineHeight = 20.sp),
    labelLarge = TextStyle(fontSize = 14.sp, lineHeight = 20.sp, fontWeight = FontWeight.Medium),
    labelMedium = TextStyle(fontSize = 12.sp, lineHeight = 16.sp),
)

@Composable
internal fun LittleLoopTheme(theme: String, content: @Composable () -> Unit) {
    val dark = theme == "dark" || theme == "system" && isSystemInDarkTheme()
    MaterialTheme(
        colorScheme = if (dark) DarkColors else LightColors,
        shapes = ChatShapes,
        typography = ChatTypography,
        content = content,
    )
}
