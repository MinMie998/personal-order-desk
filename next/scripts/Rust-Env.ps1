$projectRoot=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if(Test-Path -LiteralPath (Join-Path $projectRoot '.tools/rust/cargo/bin/cargo.exe')){
 $env:RUSTUP_HOME=Join-Path $projectRoot '.tools/rust/rustup'
 $env:CARGO_HOME=Join-Path $projectRoot '.tools/rust/cargo'
 $env:PATH=(Join-Path $env:CARGO_HOME 'bin')+';'+$env:PATH
}
# CI and other developers use their installed Rust toolchain when no local SDK exists.
