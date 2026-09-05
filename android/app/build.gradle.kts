plugins { id("com.android.application") }
android {
    namespace = "com.eslee.meshdrive"
    compileSdk = 36
    defaultConfig { applicationId = "com.eslee.meshdrive"; minSdk = 26; targetSdk = 36; versionCode = 1; versionName = "0.2.0" }
    compileOptions { sourceCompatibility = JavaVersion.VERSION_21; targetCompatibility = JavaVersion.VERSION_21 }
}
dependencies {
    implementation("androidx.core:core-ktx:1.17.0")
    implementation("androidx.documentfile:documentfile:1.1.0")
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.json:json:20250517")
}
