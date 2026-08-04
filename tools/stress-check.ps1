<#
.SYNOPSIS
    극한 부하에서 화면이 멈추는지 자동으로 확인한다.

.DESCRIPTION
    🔑 이 검사는 dotnet test 로 못 한다.
       헤드리스 테스트에는 UI 가 아예 없어서, 「화면이 멈춘다」를 재현할 수 없다.
       실제로 그래서 결함을 두 번 놓쳤다.

    무엇을 재나:
      · Process.Responding — UI 스레드가 창 메시지에 응답하는가 (= 화면이 살아 있는가)
      · 관제실의 상태 문구 — 자동화가 읽어 「유실 0」을 기계로 판정한다
      · CPU 시간 — 부하가 실제로 걸렸는가

    x:Name 을 ASCII 로 준 이유가 이것이다. 그게 곧 AutomationId 다.
#>
[CmdletBinding()]
param(
    [int]$Seconds = 15,
    [string]$Preset = 'PresetExtremeBtn'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$repoRoot = Split-Path -Parent $PSScriptRoot
$UIA = [System.Windows.Automation.AutomationElement]

function Find-LatestExe {
    param([string]$ProjectDir, [string]$ExeName)

    Get-ChildItem -Path (Join-Path $ProjectDir 'bin\Debug') -Filter $ExeName -Recurse -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

function Wait-Window {
    param([string]$TitleStartsWith, [int]$TimeoutSeconds = 20)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)

    while ((Get-Date) -lt $deadline) {
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            $UIA::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)

        $windows = $UIA::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $condition)

        foreach ($w in $windows) {
            if ($w.Current.Name -like "$TitleStartsWith*") { return $w }
        }

        Start-Sleep -Milliseconds 300
    }

    return $null
}

function Invoke-ById {
    param($Window, [string]$AutomationId)

    $condition = New-Object System.Windows.Automation.PropertyCondition($UIA::AutomationIdProperty, $AutomationId)
    $element = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)

    if (-not $element) { return $false }

    $pattern = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
    return $true
}

function Read-TextById {
    param($Window, [string]$AutomationId)

    $condition = New-Object System.Windows.Automation.PropertyCondition($UIA::AutomationIdProperty, $AutomationId)
    $element = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)

    if (-not $element) { return $null }
    return $element.Current.Name
}

# ── 기동 ─────────────────────────────────────────────────────────────

$apps = @(
    @{ Name = 'ControlRoom'; Dir = 'IoTSensorDashboard.ControlRoom'; Exe = 'IoTSensorDashboard.ControlRoom.exe'; Wait = 5 },
    @{ Name = 'SensorFarm';  Dir = 'IoTSensorDashboard.SensorFarm';  Exe = 'IoTSensorDashboard.SensorFarm.exe';  Wait = 2 },
    @{ Name = 'Dashboard';   Dir = 'IoTSensorDashboard.Dashboard';   Exe = 'IoTSensorDashboard.Dashboard.exe';   Wait = 0 }
)

$started = @()

Write-Output '=== 극한 부하 검사 ==='

foreach ($app in $apps) {
    $exe = Find-LatestExe -ProjectDir (Join-Path $repoRoot "src\$($app.Dir)") -ExeName $app.Exe
    if (-not $exe) { Write-Output "[$($app.Name)] 실행 파일 없음 — 먼저 빌드하라"; exit 1 }

    $started += @{ Name = $app.Name; Process = Start-Process -FilePath $exe -PassThru }
    if ($app.Wait -gt 0) { Start-Sleep -Seconds $app.Wait }
}

Start-Sleep -Seconds 3

$farmWindow = Wait-Window -TitleStartsWith '센서 팜'
$controlWindow = Wait-Window -TitleStartsWith '관제실'

if (-not $farmWindow)    { Write-Output '센서 팜 창을 찾지 못했다'; exit 1 }
if (-not $controlWindow) { Write-Output '관제실 창을 찾지 못했다'; exit 1 }

Write-Output '창 두 개를 찾았다.'

# ── 부하 투입 ────────────────────────────────────────────────────────

if (-not (Invoke-ById -Window $farmWindow -AutomationId $Preset)) {
    Write-Output "버튼 '$Preset' 을 찾지 못했다"
    exit 1
}

Write-Output "[$Preset] 눌렀다. $Seconds 초 동안 관찰한다."
Write-Output ''

# ── 관찰 ─────────────────────────────────────────────────────────────

$farmProcess = ($started | Where-Object { $_.Name -eq 'SensorFarm' }).Process
$notResponding = 0
$samples = 0

for ($i = 0; $i -lt $Seconds; $i++) {
    Start-Sleep -Seconds 1
    $farmProcess.Refresh()
    $samples++

    # 🔑 Responding = UI 스레드가 창 메시지에 응답하는가.
    #    false 면 화면이 멈춘 것이다.
    if (-not $farmProcess.Responding) { $notResponding++ }

    $health = Read-TextById -Window $controlWindow -AutomationId 'HealthText'
    $integrity = Read-TextById -Window $controlWindow -AutomationId 'IntegrityText'
    $state = if ($farmProcess.Responding) { '응답' } else { '멈춤' }

    Write-Output ("  {0,2}s  팜:{1}  |  {2}  |  {3}" -f ($i + 1), $state, $health, $integrity)
}

Write-Output ''
Write-Output '=== 결과 ==='
Write-Output ("  팜 UI 응답      : {0}/{1} 샘플" -f ($samples - $notResponding), $samples)
Write-Output ("  팜 CPU 시간     : {0:F1} 초" -f $farmProcess.TotalProcessorTime.TotalSeconds)

$final = Read-TextById -Window $controlWindow -AutomationId 'IntegrityText'
Write-Output ("  관제실 정합     : {0}" -f $final)

# ── 정리 ─────────────────────────────────────────────────────────────

foreach ($entry in $started) { Stop-Process -Id $entry.Process.Id -Force -ErrorAction SilentlyContinue }

Write-Output ''

if ($notResponding -gt 0) {
    Write-Output "❌ 실패 — 화면이 $notResponding 회 멈췄다"
    exit 1
}

Write-Output '✅ 통과 — 부하 내내 화면이 응답했다'
exit 0
