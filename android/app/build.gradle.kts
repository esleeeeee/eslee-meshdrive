plugins { id("com.android.application") }
android {
    namespace = "com.eslee.meshdrive"
    compileSdk = 36
    defaultConfig { applicationId = "com.eslee.meshdrive"; minSdk = 26; targetSdk = 36; versionCode = 1; versionName = "0.2.0" }
    compileOptions { sourceCompatibility = JavaVersion.VERSION_21; targetCompatibility = JavaVersion.VERSION_21 }
    testOptions {
        unitTests.isIncludeAndroidResources = true
        unitTests.all { it.jvmArgs("--add-opens=java.base/java.lang=ALL-UNNAMED", "--add-opens=java.base/java.util=ALL-UNNAMED", "--add-opens=java.base/java.io=ALL-UNNAMED", "--add-opens=java.base/java.net=ALL-UNNAMED", "--add-opens=java.base/java.security=ALL-UNNAMED", "--add-opens=java.base/java.text=ALL-UNNAMED", "--add-opens=java.base/jdk.internal.access=ALL-UNNAMED", "--add-opens=java.desktop/java.awt.font=ALL-UNNAMED", "--add-opens=jdk.compiler/com.sun.tools.javac.api=ALL-UNNAMED") }
    }
}
dependencies {
    implementation("androidx.core:core-ktx:1.17.0")
    implementation("androidx.documentfile:documentfile:1.1.0")
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.json:json:20250517")
    testImplementation("org.robolectric:robolectric:4.16.1")
}
