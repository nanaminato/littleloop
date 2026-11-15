plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.plugin.compose")
    id("com.google.gms.google-services") version "4.4.4" apply false
}
if (file("google-services.json").isFile) apply(plugin = "com.google.gms.google-services")
android {
    namespace = "dev.littleloop.chat"
    compileSdk = 36
    defaultConfig {
        applicationId = "dev.littleloop.chat"
        minSdk = 26
        targetSdk = 36
        versionCode = 1
        versionName = "0.1.0"
    }
    buildFeatures {
        compose = true
        buildConfig = true
    }
    bundle {
        language {
            // The app offers an in-app language switch, so every release APK/AAB
            // must contain both the English and Simplified-Chinese resources.
            enableSplit = false
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}
dependencies {
    testImplementation("junit:junit:4.13.2")
    implementation(platform("androidx.compose:compose-bom:2025.12.01"))
    implementation("androidx.activity:activity-compose:1.12.2")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.foundation:foundation")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("com.google.firebase:firebase-messaging:26.0.2")
    implementation("androidx.work:work-runtime-ktx:2.11.2")
}
