param([string]$OutputDirectory='')
$ErrorActionPreference='Stop'
$projectRoot=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $projectRoot
. "$PSScriptRoot/Rust-Env.ps1"
function Check-Exit {if($LASTEXITCODE -ne 0){throw "Build failed: $LASTEXITCODE"}}
npm ci --prefix next/web --no-audit --no-fund
Check-Exit
npm run build --prefix next/web
Check-Exit
cargo build --release --locked --manifest-path next/rust/Cargo.toml -p desk-server
Check-Exit
cargo build --release --locked --manifest-path next/desktop/Cargo.toml
Check-Exit
if(!$OutputDirectory){$OutputDirectory=Join-Path $projectRoot 'releases/Windows开发版'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
if(Test-Path -LiteralPath "$OutputDirectory/desk-server.exe"){
 New-Item -ItemType Directory -Path "$OutputDirectory/.update" -Force | Out-Null
 Copy-Item next/rust/target/release/desk-server.exe "$OutputDirectory/.update/desk-server.exe" -Force
}else{Copy-Item next/rust/target/release/desk-server.exe "$OutputDirectory/desk-server.exe" -Force}
New-Item -ItemType Directory -Path "$OutputDirectory/web" -Force | Out-Null
Copy-Item next/web/dist/* "$OutputDirectory/web" -Recurse -Force
Copy-Item next/rust/target/release/desk-server.exe next/desktop/target/release/desk-server.exe -Force
& "$PSScriptRoot/Publish-Desktop-UI.ps1" -OutputDirectory $OutputDirectory
Write-Output "Development build: $OutputDirectory/desk-desktop.exe"
Write-Output 'If the backend was already running, exit it in Settings and reopen the app to apply the staged update.'
Write-Output 'This build has not completed acceptance. It uses a separate PersonalOrderDesk.Next workspace.'
