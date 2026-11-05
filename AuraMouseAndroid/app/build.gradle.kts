import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// The signing key stays outside the project, so it can never end up in the repository.
// AURAMOUSE_SIGNING points at a properties file with storeFile, storePassword, keyAlias and keyPassword.
val signing = System.getenv("AURAMOUSE_SIGNING")?.let(::File)?.takeIf { it.exists() }?.let { file ->
    Properties().apply { file.inputStream().use { load(it) } }
}

android {
    namespace = "com.auramouse.app"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.auramouse.app"
        minSdk = 29 // Android 10: saving to Downloads needs no storage permission from here on
        targetSdk = 34
        versionCode = 2
        versionName = "1.1"
    }

    signingConfigs {
        if (signing != null) create("release") {
            storeFile = File(signing.getProperty("storeFile"))
            storePassword = signing.getProperty("storePassword")
            keyAlias = signing.getProperty("keyAlias")
            keyPassword = signing.getProperty("keyPassword")
        }
    }

    buildTypes {
        release {
            if (signing != null) signingConfig = signingConfigs.getByName("release")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }

    testOptions {
        unitTests.all {
            it.testLogging { events("passed", "failed"); showStandardStreams = true }
            it.outputs.upToDateWhen { false }
        }
    }
}

dependencies {
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.json:json:20240303") // Android has this built in; plain JVM tests don't
}
