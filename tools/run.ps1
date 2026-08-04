<#
.SYNOPSIS
    세 앱을 순서대로 띄운다.

.DESCRIPTION
    기동 순서가 중요하다:

        ControlRoom  →  (5초)  →  SensorFarm  →  (2초)  →  Dashboard

    🔑 ControlRoom 이 브로커를 소유하므로 먼저 떠 있어야 나머지가 붙는다.
       순서를 어겨도 재연결 루프가 결국 붙지만, 백오프(2초)만큼 늦는다.

    띄운 뒤 센서 팜에서 발행 속도를 고르면 데이터가 흐르기 시작한다
    (기본값은 「정지」다 — 켜자마자 디스크를 쓰기 시작하지 않게).
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

# 🔴 TFM 폴더를 하드코딩하지 않는다. 옛 빌드 잔재를 집으면
#    「수정이 안 먹었다」로 오해하게 된다(실제로 3시간을 태운 함정).
function Find-LatestExe {
    param([string]$ProjectDir, [string]$ExeName)

    Get-ChildItem -Path (Join-Path $ProjectDir 'bin\Debug') -Filter $ExeName `
                  -Recurse -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

$apps = @(
    @{ Name = '관제실';  Dir = 'IoTSensorDashboard.ControlRoom'; Exe = 'IoTSensorDashboard.ControlRoom.exe'; Wait = 5 },
    @{ Name = '센서 팜'; Dir = 'IoTSensorDashboard.SensorFarm';  Exe = 'IoTSensorDashboard.SensorFarm.exe';  Wait = 2 },
    @{ Name = '상황판';  Dir = 'IoTSensorDashboard.Dashboard';   Exe = 'IoTSensorDashboard.Dashboard.exe';   Wait = 0 }
)

foreach ($app in $apps) {
    $exe = Find-LatestExe -ProjectDir (Join-Path $repoRoot "src\$($app.Dir)") -ExeName $app.Exe

    if (-not $exe) {
        Write-Output "[$($app.Name)] 실행 파일이 없다. 먼저 빌드하라: dotnet build"
        exit 1
    }

    Write-Output "[$($app.Name)] $exe"
    Start-Process -FilePath $exe | Out-Null

    if ($app.Wait -gt 0) { Start-Sleep -Seconds $app.Wait }
}

Write-Output ''
Write-Output '세 앱을 띄웠다.'
Write-Output '센서 팜에서 발행 속도를 고르면 데이터가 흐른다(기본값은 정지).'
