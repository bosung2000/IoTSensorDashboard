<#
.SYNOPSIS
    기동 스모크 — 세 앱이 뜨고, 살아 있고, 창을 띄웠는지만 본다.

.DESCRIPTION
    🔑 이 스크립트는 "죽지 않는가"만 본다.
       "정상 동작" 판정은 dotnet test 와 손 확인의 몫이다 — 할 수 있는 것만 주장한다.

    왜 따로 필요한가:
      dotnet test 는 Core · Mqtt · Sqlite 만 본다(테스트가 UI 앱을 참조하지 않는다).
      XAML 파싱 오류 · 리소스 누락 · App.xaml 결선 문제는 **앱을 띄워야만** 드러난다.

      실제로 App.xaml 에 x:Class 가 빠져 App 클래스가 통째로 죽어 있던 적이 있고,
      그때 테스트는 전부 그린이었다.

.PARAMETER Seconds
    기동 후 생존을 확인할 때까지 기다리는 시간.

.PARAMETER KeepOpen
    확인 후에도 앱을 끄지 않는다(눈으로 볼 때).
#>
[CmdletBinding()]
param(
    [int]$Seconds = 8,
    [switch]$KeepOpen
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

# 🔴 TFM 폴더를 하드코딩하지 않는다.
#
# 📌 근거 — 실제로 3시간을 태운 함정:
#    bin\Debug 아래에 폴더가 둘 있었다.
#      net8.0-windows\                 ← 8일 묵은 옛 TFM 잔재
#      net8.0-windows10.0.19041.0\     ← 현재 빌드 산출물
#    검증 스크립트가 8일 묵은 쪽을 집었고, 그 옛 바이너리가 고치기 전 동작을 띄웠다.
#    「수정이 안 먹었다」로 오해하기 딱 좋았다.
function Find-LatestExe {
    param([string]$ProjectDir, [string]$ExeName)

    $candidates = Get-ChildItem -Path (Join-Path $ProjectDir 'bin\Debug') -Filter $ExeName `
                                -Recurse -File -ErrorAction SilentlyContinue |
                  Sort-Object LastWriteTime -Descending

    if (-not $candidates) { return $null }
    return $candidates[0].FullName
}

$apps = @(
    @{ Name = '관제실';   Dir = 'IoTSensorDashboard.ControlRoom'; Exe = 'IoTSensorDashboard.ControlRoom.exe'; WaitAfter = 5 },
    @{ Name = '센서 팜';  Dir = 'IoTSensorDashboard.SensorFarm';  Exe = 'IoTSensorDashboard.SensorFarm.exe';  WaitAfter = 2 },
    @{ Name = '상황판';   Dir = 'IoTSensorDashboard.Dashboard';   Exe = 'IoTSensorDashboard.Dashboard.exe';   WaitAfter = 0 }
)

$started = @()
$failed = @()

Write-Output '=== 기동 스모크 ==='

foreach ($app in $apps) {
    $projectDir = Join-Path $repoRoot "src\$($app.Dir)"
    $exe = Find-LatestExe -ProjectDir $projectDir -ExeName $app.Exe

    if (-not $exe) {
        Write-Output "[$($app.Name)] ✗ 실행 파일을 못 찾았다 — 빌드했는가?"
        $failed += $app.Name
        continue
    }

    # 🔒 어떤 바이너리를 띄웠는지 눈으로 확인할 수 있게 남긴다.
    Write-Output "[$($app.Name)] 실행: $exe"
    Write-Output "            빌드 시각: $((Get-Item $exe).LastWriteTime)"

    $process = Start-Process -FilePath $exe -PassThru
    $started += @{ Name = $app.Name; Process = $process }

    # 기동 순서가 중요하다 — 관제실이 브로커를 소유하므로 먼저 떠 있어야 나머지가 붙는다.
    if ($app.WaitAfter -gt 0) { Start-Sleep -Seconds $app.WaitAfter }
}

Write-Output ''
Write-Output "생존 확인까지 $Seconds 초 대기…"
Start-Sleep -Seconds $Seconds
Write-Output ''

foreach ($entry in $started) {
    $alive = Get-Process -Id $entry.Process.Id -ErrorAction SilentlyContinue

    if (-not $alive) {
        Write-Output "[$($entry.Name)] ✗ 죽었다"
        $failed += $entry.Name
        continue
    }

    $alive.Refresh()

    if ($alive.MainWindowHandle -eq 0) {
        Write-Output "[$($entry.Name)] ✗ 살아 있지만 창을 띄우지 못했다 (XAML 파싱 실패 가능)"
        $failed += $entry.Name
        continue
    }

    Write-Output "[$($entry.Name)] ✓ 생존 · 창 '$($alive.MainWindowTitle)'"
}

if (-not $KeepOpen) {
    Write-Output ''
    Write-Output '정리 중…'
    foreach ($entry in $started) {
        Stop-Process -Id $entry.Process.Id -Force -ErrorAction SilentlyContinue
    }
}

Write-Output ''

if ($failed.Count -gt 0) {
    Write-Output "=== 실패: $($failed -join ', ') ==="
    exit 1
}

Write-Output '=== 통과 — 세 앱이 뜨고, 살아 있고, 창을 띄웠다 ==='
Write-Output '    (이 스크립트는 "죽지 않는가"만 본다. 동작 판정은 dotnet test 와 손 확인의 몫이다.)'
exit 0
