param(
    [string]$JavaHome='D:\Dev\Android\Android Studio\jbr',
    [string]$AndroidHome="$env:LOCALAPPDATA\Android\Sdk",
    [ValidatePattern('^[A-Z]$')][string]$Drive='Q',
    [switch]$RunChecks,
    [switch]$BuildProbe
)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if(!(Test-Path "$JavaHome/bin/java.exe")){throw 'Specify a valid -JavaHome.'}
if(!(Test-Path $AndroidHome)){throw 'Specify a valid -AndroidHome.'}
& "$PSScriptRoot/Build-Android-SyncCore.ps1" -AndroidHome $AndroidHome
if($RunChecks){
 . "$PSScriptRoot/Rust-Env.ps1"
 & cargo build --locked --manifest-path "$projectRoot/next/rust/Cargo.toml" -p desk-sync-jni --release
 if($LASTEXITCODE -ne 0){throw 'Host JNI build failed.'}
}
# AGP requires an ASCII project path. Never replace an existing drive mapping.
$mappedRoot="${Drive}:\"
$created=$false
if(Test-Path $mappedRoot){
    throw "Drive $Drive is occupied; choose a free -Drive. No mapping changed."
}else{
    & subst "${Drive}:" $projectRoot
    if($LASTEXITCODE -ne 0){throw 'Cannot create ASCII drive mapping.'}
    $created=$true
}
try{
    $env:JAVA_HOME=$JavaHome
    $env:ANDROID_HOME=$AndroidHome
    $tasks=@(':app:assembleDebug')
    if($RunChecks){$tasks+=':app:testDebugUnitTest'}
    if($BuildProbe){$tasks+=':app:assembleDebugAndroidTest'}
    & "${Drive}:/next/android/gradlew.bat" -p "${Drive}:/next/android" @tasks --console=plain
    if($LASTEXITCODE -ne 0){throw 'Android build or requested checks failed.'}
    $archive=[System.IO.Compression.ZipFile]::OpenRead("$projectRoot/next/android/app/build/outputs/apk/debug/app-debug.apk")
    try{
     foreach($abi in @('arm64-v8a','x86_64')){
      if($null -eq $archive.GetEntry("lib/$abi/libdesk_sync_jni.so")){throw "Packaged APK is missing sync core for $abi."}
     }
    }finally{$archive.Dispose()}
    $apk=Get-Item "$projectRoot/next/android/app/build/outputs/apk/debug/app-debug.apk"
    Write-Output "Native development APK: $($apk.Length) bytes ($($apk.FullName))"
}finally{
    if($created){& subst "${Drive}:" /D}
}
