param([string]$AndroidHome="$env:LOCALAPPDATA\Android\Sdk",[string]$NdkVersion='28.2.13676358',
 [ValidateSet('arm64-v8a','x86_64')][string[]]$Abis=@('arm64-v8a','x86_64'))
$ErrorActionPreference='Stop'
. "$PSScriptRoot/Rust-Env.ps1"
$projectRoot=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$toolchain=Join-Path $AndroidHome "ndk/$NdkVersion/toolchains/llvm/prebuilt/windows-x86_64/bin"
$targets=@{'arm64-v8a'='aarch64-linux-android';'x86_64'='x86_64-linux-android'}
foreach($abi in $Abis){
 $target=$targets[$abi]
 $linker=Join-Path $toolchain "$($target)26-clang.cmd"
 if(!(Test-Path -LiteralPath $linker)){throw "Android NDK compiler missing for $abi."}
 $linkerVariable='CARGO_TARGET_'+$target.ToUpperInvariant().Replace('-','_')+'_LINKER'
 $previousLinker=[Environment]::GetEnvironmentVariable($linkerVariable,'Process')
 try{
  [Environment]::SetEnvironmentVariable($linkerVariable,$linker,'Process')
  & cargo build --locked --manifest-path "$projectRoot/next/rust/Cargo.toml" -p desk-sync-jni --release --target $target
  if($LASTEXITCODE -ne 0){throw "Android sync core build failed. Install target with rustup target add $target."}
  $destination=Join-Path $projectRoot "next/android/app/src/main/jniLibs/$abi"
  New-Item -ItemType Directory -Force -Path $destination | Out-Null
  Copy-Item -LiteralPath "$projectRoot/next/rust/target/$target/release/libdesk_sync_jni.so" -Destination $destination -Force
  Write-Output "$abi sync core: $((Get-Item "$destination/libdesk_sync_jni.so").Length) bytes"
 }finally{[Environment]::SetEnvironmentVariable($linkerVariable,$previousLinker,'Process')}
}
