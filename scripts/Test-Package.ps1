param([string]$Package='releases/build-Windows-2.0.0-preview.3/PersonalOrderDesk')
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
Set-Location $projectRoot
$directory=(Resolve-Path $Package).Path
$data=Join-Path $projectRoot ('tmp/package-smoke-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $data | Out-Null
if((Get-Content (Join-Path $directory 'wwwroot/index.html') -Raw) -match 'autostart="false"'){throw 'Windows主机不能禁用Blazor自动启动。'}
if(!(Test-Path (Join-Path $directory 'wwwroot/_content/PersonalOrderDesk.SharedUI/desk.css'))){throw '缺少共用界面资源。'}
$start=[Diagnostics.ProcessStartInfo]::new((Join-Path $directory 'server/PersonalOrderDesk.exe'))
$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WorkingDirectory=Join-Path $directory 'server'
$start.Environment['PERSONAL_ORDER_DESK_DATA_DIR']=$data
$process=[Diagnostics.Process]::Start($start)
try{
    for($i=0;$i -lt 80;$i++){
        try{$info=Invoke-RestMethod http://127.0.0.1:5059/api/v1/info -TimeoutSec 2;if($info.productId -eq 'PersonalOrderDesk'){break}}catch{Start-Sleep -Milliseconds 250}
        if($process.HasExited){throw '发布包后台提前退出。'}
    }
    if(!$info -or $process.HasExited){throw '发布包后台未就绪。'}
    $unauthorized=Invoke-WebRequest http://127.0.0.1:5059/api/v1/state -SkipHttpErrorCheck
    if($unauthorized.StatusCode -ne 401){throw '未认证API没有返回401。'}
    $session=Invoke-RestMethod http://127.0.0.1:5059/api/v1/session -Method Post -ContentType application/json -Body '{"password":""}'
    $headers=@{Authorization='Bearer '+$session.token}
    $state=Invoke-RestMethod http://127.0.0.1:5059/api/v1/state -Headers $headers
    if($state.orders.Count -ne 0){throw '发布包包含订单或连接了错误数据源。'}
    foreach($path in @('/Workbench','/_framework/blazor.server.js','/_content/PersonalOrderDesk.SharedUI/desk.css')){
        $response=Invoke-WebRequest ('http://127.0.0.1:5059'+$path)
        if($response.StatusCode -ne 200 -or $response.Content.Length -eq 0){throw "静态资源不可用：$path"}
    }
    $snapshot=Invoke-WebRequest http://127.0.0.1:5059/api/v1/snapshot -Headers $headers
    if($snapshot.RawContentLength -lt 100){throw 'PC副本接口未生成有效数据。'}
    '发布包启动、未认证访问、空账本、Blazor脚本、共用资源与PC副本接口检查通过。'
}finally{
    if(!$process.HasExited){$process.Kill();$process.WaitForExit()};$process.Dispose()
}
