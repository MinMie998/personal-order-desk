$ErrorActionPreference='Stop'
$projectRoot=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $projectRoot
. "$PSScriptRoot/Rust-Env.ps1"
function Check-Exit { if($LASTEXITCODE -ne 0){throw "Build failed: $LASTEXITCODE"} }
npm ci --prefix next/web --no-audit --no-fund
Check-Exit
npm run build --prefix next/web
Check-Exit
cargo test --locked --manifest-path next/rust/Cargo.toml
Check-Exit
cargo build --release --locked --manifest-path next/rust/Cargo.toml -p desk-server
Check-Exit
cargo test --release --locked --manifest-path next/desktop/Cargo.toml
Check-Exit
cargo build --release --locked --manifest-path next/desktop/Cargo.toml
Check-Exit
Copy-Item next/rust/target/release/desk-server.exe next/desktop/target/release/desk-server.exe -Force
node next/scripts/Test-Probe.mjs
Check-Exit
