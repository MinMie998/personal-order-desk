param([string]$OutputDirectory='releases/Windows开发版')
$ErrorActionPreference='Stop'
$projectRoot=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$destination=[IO.Path]::GetFullPath($(if([IO.Path]::IsPathRooted($OutputDirectory)){$OutputDirectory}else{Join-Path $projectRoot $OutputDirectory}))
$releaseRoot=[IO.Path]::GetFullPath((Join-Path $projectRoot 'releases'))+[IO.Path]::DirectorySeparatorChar
if(!$destination.StartsWith($releaseRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'UI package must stay under project releases.'}
if(!(Test-Path -LiteralPath (Join-Path $destination 'desk-server.exe'))){throw 'This UI-only publisher requires an existing compatible backend package.'}
$source=Join-Path $projectRoot 'next/desktop/target/release/desk-desktop.exe'
New-Item -ItemType Directory -Force -Path (Join-Path $destination 'web')|Out-Null
$entry='desk-desktop.exe'
$target=Join-Path $destination $entry
$same=(Test-Path -LiteralPath $target) -and ((Get-FileHash -LiteralPath $source).Hash -eq (Get-FileHash -LiteralPath $target).Hash)
if(!$same){try{Copy-Item -LiteralPath $source -Destination $target -Force -ErrorAction Stop}catch{throw 'Close the workbench window before updating desk-desktop.exe. No alternate executable was created.'}}
Copy-Item (Join-Path $projectRoot 'next/web/dist/*') (Join-Path $destination 'web') -Recurse -Force
# One direct EXE entry. Never create update-a/update-b copies or a CMD launcher.
Write-Output "Application: $target"
