param([ValidateSet('Windows','Android')][string]$Platform='Windows',[string]$Version='2.0.0-preview.1',[string]$Dotnet='dotnet',[string]$AndroidSdkDirectory=$env:ANDROID_HOME,[string]$JavaSdkDirectory=$env:JAVA_HOME,[string]$KeyStore,[string]$PasswordFile)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
Set-Location $projectRoot
$destination=Join-Path $projectRoot "releases/build-$Platform-$Version"
New-Item -ItemType Directory -Force $destination | Out-Null
function Invoke-Dotnet([string[]]$Arguments){ & $Dotnet @Arguments; if($LASTEXITCODE -ne 0){throw "dotnet failed: $LASTEXITCODE"} }
if($Platform -eq 'Windows'){
    $package=Join-Path $destination 'PersonalOrderDesk'
    Invoke-Dotnet -Arguments @('publish','src/PersonalOrderDesk.Desktop','-c','Release','-r','win-x64','--self-contained','true',"-p:Version=$Version",'-o',$package)
    Invoke-Dotnet -Arguments @('publish','src/PersonalOrderDesk','-c','Release','-r','win-x64','--self-contained','true',"-p:Version=$Version",'-o',(Join-Path $package 'server'))
    Invoke-Dotnet -Arguments @('publish','src/PersonalOrderDesk.Launcher','-c','Release','-r','win-x64','--self-contained','true',"-p:Version=$Version",'-o',(Join-Path $package 'server'))
    Copy-Item 'docs/public/安装与更新.md' (Join-Path $package '安装与更新.md')
    $private=Get-ChildItem $package -Recurse -File | Where-Object { $_.Name -match '(^app\.db|\.db$|\.keystore$|\.jks$|^data-location\.json$|^network-access\.json$)' }
    if($private){throw '发布目录混入本机数据，禁止打包。'}
    Compress-Archive -Path "$package/*" -DestinationPath (Join-Path $destination 'PersonalOrderDesk-win-x64.zip') -Force
}else{
    $arguments=@('publish','src/PersonalOrderDesk.Android','-c','Release',"-p:Version=$Version","-p:ApplicationDisplayVersion=$Version",'-p:AndroidPackageFormats=apk')
    if($AndroidSdkDirectory){$arguments+="-p:AndroidSdkDirectory=$AndroidSdkDirectory"};if($JavaSdkDirectory){$arguments+="-p:JavaSdkDirectory=$JavaSdkDirectory"}
    if($KeyStore){if(!(Test-Path $PasswordFile)){throw '缺少签名密码文件'};Copy-Item $PasswordFile ($PasswordFile+'.key') -Force;$arguments+=@('-p:AndroidKeyStore=true',"-p:AndroidSigningKeyStore=$KeyStore",'-p:AndroidSigningKeyAlias=personalorderdesk',"-p:AndroidSigningKeyPass=file:$PasswordFile.key","-p:AndroidSigningStorePass=file:$PasswordFile")}
    Invoke-Dotnet $arguments
    $apk=Get-ChildItem src/PersonalOrderDesk.Android/bin/Release/net10.0-android/publish -Filter '*-Signed.apk' | Select-Object -First 1
    if(!$apk){throw '没有找到签名APK。'}
    Copy-Item $apk.FullName (Join-Path $destination 'PersonalOrderDesk-android.apk')
}
Get-ChildItem $destination -File | Where-Object Extension -In '.zip','.apk' | ForEach-Object {"$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower())  $($_.Name)"} | Set-Content (Join-Path $destination 'SHA256SUMS.txt') -Encoding utf8NoBOM
Write-Output "构建结果：$destination"

