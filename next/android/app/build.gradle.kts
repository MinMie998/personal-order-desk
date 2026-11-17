plugins { id("com.android.application");id("com.google.devtools.ksp");id("org.jetbrains.kotlin.plugin.compose") }
android {
 namespace="com.personalorderdesk.nativepreview"
 compileSdk { version=release(37){minorApiLevel=0} }
 defaultConfig {
  applicationId="com.personalorderdesk.nativepreview";minSdk=26;targetSdk=36;versionCode=7;versionName="3.0.0-preview.1"
  ndk { abiFilters += listOf("arm64-v8a","x86_64") }
  testInstrumentationRunner="com.personalorderdesk.nativepreview.SyncCoreProbe"
 }
 buildFeatures { compose=true;buildConfig=true }
 buildTypes { getByName("debug"){isDebuggable=false;isMinifyEnabled=true;proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"),"proguard-rules.pro")} }
 compileOptions { sourceCompatibility=JavaVersion.VERSION_17;targetCompatibility=JavaVersion.VERSION_17 }
 testOptions.unitTests.all {
  it.systemProperty("contracts.root",rootProject.file("../contracts").absolutePath)
  it.systemProperty("java.library.path",rootProject.file("../rust/target/release").absolutePath)
 }
}
dependencies {
 implementation(platform("androidx.compose:compose-bom:2025.04.01"))
 implementation("androidx.activity:activity-compose:1.10.1")
 implementation("androidx.compose.material3:material3")
 implementation("androidx.room:room-runtime:2.8.4")
 ksp("androidx.room:room-compiler:2.8.4")
 testImplementation("junit:junit:4.13.2")
 testImplementation("org.json:json:20250107")
}

ksp { arg("room.schemaLocation", "$projectDir/schemas") }
