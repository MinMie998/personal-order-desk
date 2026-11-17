param([string]$OutputDirectory='releases/Windows交付', [switch]$SkipBuild)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $projectRoot
if(!$SkipBuild){
 . "$PSScriptRoot/Rust-Env.ps1"
 npm ci --prefix next/web --no-audit --no-fund
 if($LASTEXITCODE -ne 0){throw 'npm ci failed'}
 npm run build --prefix next/web
 if($LASTEXITCODE -ne 0){throw 'Web build failed'}
 cargo build --release --locked --manifest-path next/rust/Cargo.toml -p desk-server
 if($LASTEXITCODE -ne 0){throw 'Backend build failed'}
 cargo build --release --locked --manifest-path next/desktop/Cargo.toml
 if($LASTEXITCODE -ne 0){throw 'Desktop build failed'}
}
$destination=[IO.Path]::GetFullPath($(if([IO.Path]::IsPathRooted($OutputDirectory)){$OutputDirectory}else{Join-Path $projectRoot $OutputDirectory}))
$releaseRoot=[IO.Path]::GetFullPath((Join-Path $projectRoot 'releases'))+[IO.Path]::DirectorySeparatorChar
if(!$destination.StartsWith($releaseRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Package output must stay inside releases'}
$stage=Join-Path $projectRoot ('tmp/package-windows-'+[Guid]::NewGuid().ToString())
$app=Join-Path $stage '个人接单工作台'
New-Item -ItemType Directory -Path "$app/web" -Force | Out-Null
# Explicit allowlist: never copy a running installation, user data or runtime credentials.
Copy-Item -LiteralPath next/desktop/target/release/desk-desktop.exe -Destination "$app/desk-desktop.exe"
Copy-Item -LiteralPath next/rust/target/release/desk-server.exe -Destination "$app/desk-server.exe"
Copy-Item next/web/dist/* "$app/web" -Recurse
$manifest=Get-Content next/desktop/tauri.conf.json -Raw | ConvertFrom-Json
$revision=(git rev-parse HEAD).Trim()
if($LASTEXITCODE -ne 0){throw 'Cannot read source revision'}
$files=@(Get-ChildItem -LiteralPath $app -Recurse -File)
if($files | Where-Object {$_.Name -match '\.(db|pem|deskkey|lnk)$|service\.bin|github-auth|workspace\.json|storage-location\.json'}){throw 'Private runtime file detected in staging'}
$readme=@'
个人接单工作台 Windows 预览版

完整解压后，双击 desk-desktop.exe。desk-server.exe 和 web 文件夹是必要配套，请一起保留。
无需安装 .NET、Node 或 Rust。界面使用系统 WebView2；缺少组件时请安装微软官方 WebView2 Runtime。

第一次使用会询问创建或打开已有账本。发布包不携带订单、登录凭据或恢复密钥。
更换程序文件不会自动搬移、删除或重建账本。已有用户请先在设置中备份，退出后台并关闭窗口后更新。

同步：日常选择“检查同步”；空白设备通过恢复密钥连接后获取远端副本。
高级操作“从远端替换本机”“以本机重置远端”会先预览，确认后才整本替换。
远端重置后，旧副本暂停合并；同步格式 v2 需要各设备升级到兼容版本。
程序源码公开仓库与私有账本同步仓库是两回事，不要把账本或密钥上传到公开仓库。

本包为 3.0 预发布版；Android 同版本安装包在 GitHub Releases 单独下载。请保留备份。
浏览器入口：http://127.0.0.1:15059（需先启动 Windows 工作台后台）。
'@
[IO.File]::WriteAllText((Join-Path $app '使用说明.txt'),$readme,[Text.UTF8Encoding]::new($false))
$metadata=@{product='PersonalOrderDesk';version=$manifest.version;sourceRevision=$revision;builtAt=[DateTime]::UtcNow.ToString('o');platform='windows-x64';syncRules=2;includesUserData=$false}
[IO.File]::WriteAllText((Join-Path $app 'version.json'),($metadata|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$zip=Join-Path $destination ('PersonalOrderDesk-'+$manifest.version+'-windows-x64.zip')
Compress-Archive -LiteralPath $app -DestinationPath $zip -Force
$hash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $destination 'SHA256SUMS.txt'),($hash+'  '+[IO.Path]::GetFileName($zip)+"`n"),[Text.Encoding]::ASCII)
Write-Output "Package: $zip"
Write-Output "Source revision: $revision"
Write-Output "SHA256: $hash"
