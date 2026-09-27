[CmdletBinding()]
param(
    [switch]$FrequencyDomainTest,
    [ValidateRange(400, 3000)]
    [uint32]$TestLimitMhz = 1200,
    [string]$ProbePath,
    [string]$OutputRoot,
    [switch]$StopHelper,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Restore-HelperInfrastructure {
    param($State)

    if ($null -eq $State) {
        return
    }

    foreach ($taskState in @($State.Tasks)) {
        if (-not $taskState.WasEnabled) {
            continue
        }

        $task = Get-ScheduledTask -TaskName $taskState.TaskName -TaskPath $taskState.TaskPath -ErrorAction SilentlyContinue
        if ($null -ne $task) {
            Enable-ScheduledTask -InputObject $task | Out-Null
        }
    }

    if ($null -ne $State.Service -and $State.Service.StartMode -ne 'Disabled') {
        $startupType = $(if ($State.Service.StartMode -eq 'Auto') { 'Automatic' } else { 'Manual' })
        Set-Service -Name $State.Service.Name -StartupType $startupType
    }
}

function Suspend-HelperInfrastructure {
    $state = [pscustomobject]@{
        Tasks = [System.Collections.Generic.List[object]]::new()
        Service = $null
    }

    try {
        $taskLocations = @(
            [pscustomobject]@{ TaskName = 'ClawTweaksHelper'; TaskPath = '\ClawTweaks\' },
            [pscustomobject]@{ TaskName = 'GoTweaksHelper'; TaskPath = '\GoTweaks\' },
            [pscustomobject]@{ TaskName = 'GoTweaksHelper'; TaskPath = '\' }
        )
        foreach ($location in $taskLocations) {
            $task = Get-ScheduledTask -TaskName $location.TaskName -TaskPath $location.TaskPath -ErrorAction SilentlyContinue
            if ($null -eq $task) {
                continue
            }

            $wasEnabled = [string]$task.State -ne 'Disabled'
            $state.Tasks.Add([pscustomobject]@{
                TaskName = $location.TaskName
                TaskPath = $location.TaskPath
                WasEnabled = $wasEnabled
            })
            if ($wasEnabled) {
                Disable-ScheduledTask -InputObject $task | Out-Null
            }

            Stop-ScheduledTask -InputObject $task -ErrorAction SilentlyContinue | Out-Null
        }

        $service = Get-CimInstance Win32_Service -Filter "Name='GoTweaksHelper'" -ErrorAction SilentlyContinue
        if ($null -ne $service) {
            $state.Service = [pscustomobject]@{
                Name = $service.Name
                StartMode = $service.StartMode
            }
            if ($service.StartMode -ne 'Disabled') {
                Set-Service -Name $service.Name -StartupType Disabled
            }
            Stop-Service -Name $service.Name -Force -ErrorAction SilentlyContinue
        }

        $processNames = @('XboxGamingBar', 'GameBar', 'GameBarFTServer', 'XboxGamingBarHelper')
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            $running = @(Get-Process -Name $processNames -ErrorAction SilentlyContinue)
            if ($running.Count -eq 0) {
                break
            }

            $running | Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 250
        }

        if (@(Get-Process -Name XboxGamingBarHelper -ErrorAction SilentlyContinue).Count -gt 0) {
            throw '예약 작업과 Game Bar를 중지했지만 XboxGamingBarHelper가 계속 재시작됩니다.'
        }

        return $state
    }
    catch {
        Restore-HelperInfrastructure -State $state
        throw
    }
}

function Resolve-ProbePath {
    param([string]$RequestedPath)

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $candidates += $RequestedPath
    }

    $candidates += @(
        (Join-Path $PSScriptRoot 'CpuTopologyProbe.exe'),
        (Join-Path $PSScriptRoot 'bin\Release\net8.0-windows\win-x64\publish\CpuTopologyProbe.exe'),
        (Join-Path $PSScriptRoot 'bin\Release\net8.0-windows\CpuTopologyProbe.exe')
    )

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw 'CpuTopologyProbe.exe를 찾을 수 없습니다. 스크립트와 EXE를 같은 폴더에 두거나 -ProbePath를 지정하세요.'
}

function Invoke-TopologyProbe {
    param(
        [string]$Executable,
        [string]$Destination,
        [switch]$RunFrequencyDomainTest,
        [uint32]$LimitMhz
    )

    $arguments = @('--output', $Destination)
    if ($RunFrequencyDomainTest) {
        $arguments += @('--test-frequency-domains', '--test-limit-mhz', [string]$LimitMhz)
    }

    & $Executable @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "CpuTopologyProbe가 종료 코드 $LASTEXITCODE 로 실패했습니다."
    }

    if (-not (Test-Path -LiteralPath $Destination -PathType Leaf)) {
        throw "CpuTopologyProbe가 결과 파일을 만들지 않았습니다: $Destination"
    }

    return Get-Content -Raw -LiteralPath $Destination | ConvertFrom-Json
}

function Get-CoreCountSummary {
    param($Report)

    $counts = @{}
    foreach ($group in @($Report.physicalCores | Group-Object classification)) {
        $counts[[string]$group.Name] = $group.Count
    }

    return ('P={0}, E={1}, LP-E={2}, EfficientUnknown={3}, Unknown={4}' -f
        $(if ($counts.ContainsKey('Performance')) { $counts['Performance'] } else { 0 }),
        $(if ($counts.ContainsKey('Efficiency')) { $counts['Efficiency'] } else { 0 }),
        $(if ($counts.ContainsKey('LowPowerEfficiency')) { $counts['LowPowerEfficiency'] } else { 0 }),
        $(if ($counts.ContainsKey('EfficientUnknown')) { $counts['EfficientUnknown'] } else { 0 }),
        $(if ($counts.ContainsKey('Unknown')) { $counts['Unknown'] } else { 0 }))
}

function Get-NullableBooleanText {
    param($Value)

    if ($null -eq $Value) {
        return 'INCONCLUSIVE'
    }

    return $(if ([bool]$Value) { 'TRUE' } else { 'FALSE' })
}

function Compare-PowerPlanSettings {
    param($Before, $After)

    if ([string]$Before.activeScheme -ne [string]$After.activeScheme) {
        return $false
    }

    foreach ($beforeSetting in @($Before.processorFrequencyLimits)) {
        $afterSetting = @($After.processorFrequencyLimits | Where-Object {
            $_.powerEfficiencyClass -eq $beforeSetting.powerEfficiencyClass
        }) | Select-Object -First 1
        if ($null -eq $afterSetting) {
            return $false
        }

        if ($beforeSetting.acReadStatus -ne $afterSetting.acReadStatus -or
            $beforeSetting.dcReadStatus -ne $afterSetting.dcReadStatus -or
            $beforeSetting.acValueMhz -ne $afterSetting.acValueMhz -or
            $beforeSetting.dcValueMhz -ne $afterSetting.dcValueMhz) {
            return $false
        }
    }

    return $true
}

function Add-ReportSummary {
    param(
        [System.Collections.Generic.List[string]]$Lines,
        [string]$Label,
        $Report
    )

    $Lines.Add("[$Label]")
    $Lines.Add("Collected UTC: $($Report.collectedAtUtc)")
    $Lines.Add("System: $($Report.systemManufacturer) / $($Report.systemProductName) / board $($Report.baseBoardProduct)")
    $Lines.Add("BIOS: $($Report.biosVersion)")
    $Lines.Add("CPU: $($Report.processorName)")
    $Lines.Add("Cores: $(Get-CoreCountSummary -Report $Report)")
    $Lines.Add("Independent LP-E frequency: $(Get-NullableBooleanText -Value $Report.capabilities.supportsIndependentLpeFrequency)")
    $Lines.Add("Evidence: $($Report.capabilities.independentLpeFrequencyEvidence)")
    $Lines.Add('Power frequency registers:')
    foreach ($setting in @($Report.powerPlan.processorFrequencyLimits)) {
        $Lines.Add(('  class {0}: present={1}, AC={2}, DC={3}, status={4}/{5}, GUID={6}' -f
            $setting.powerEfficiencyClass,
            $setting.present,
            $setting.acValueMhz,
            $setting.dcValueMhz,
            $setting.acReadStatus,
            $setting.dcReadStatus,
            $setting.settingGuid))
    }

    $Lines.Add('Helper raw-to-power class mapping:')
    foreach ($mapping in @($Report.helperPowerClassMappings)) {
        $Lines.Add(('  raw {0} -> power {1}: {2}; logical={3}, physical={4}, register={5}' -f
            $mapping.rawEfficiencyClass,
            $mapping.helperPowerEfficiencyClass,
            (@($mapping.classifications) -join '/'),
            $mapping.logicalProcessorCount,
            $mapping.physicalCoreCount,
            $mapping.frequencyLimitSettingPresent))
    }

    if ($Report.PSObject.Properties.Name -contains 'frequencyDomainTest') {
        $Lines.Add("Frequency-domain test: $($Report.frequencyDomainTest.status), limit=$($Report.frequencyDomainTest.requestedLimitMhz) MHz")
        foreach ($step in @($Report.frequencyDomainTest.steps)) {
            $changedGroups = @($step.observations | Where-Object { $_.changed } | Group-Object classification | ForEach-Object {
                "$($_.Name)=$($_.Count)"
            })
            $changedText = $(if ($changedGroups.Count -eq 0) { 'none' } else { $changedGroups -join ', ' })
            $Lines.Add(('  class {0}: write={1}, restore={2}, changed={3}' -f
                $step.powerEfficiencyClass,
                $step.writeSucceeded,
                $step.restoreSucceeded,
                $changedText))
        }
    }

    if (@($Report.issues).Count -gt 0) {
        $Lines.Add('Issues:')
        foreach ($issue in @($Report.issues)) {
            $Lines.Add("  [$($issue.severity)] $($issue.source): $($issue.message)")
        }
    }

    $Lines.Add('')
}

if ($env:OS -ne 'Windows_NT') {
    throw '이 스크립트는 Windows 전용입니다.'
}

$resolvedProbePath = Resolve-ProbePath -RequestedPath $ProbePath
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
    $OutputRoot = Join-Path $desktop "Claw8EX-CPU-Validation-$timestamp"
}

$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$transcriptPath = Join-Path $OutputRoot 'collector.log'
$summaryPath = Join-Path $OutputRoot 'summary.txt'
$prePath = Join-Path $OutputRoot '01-read-only-before.json'
$testPath = Join-Path $OutputRoot '02-frequency-domain-test.json'
$postPath = Join-Path $OutputRoot '03-read-only-after.json'
$powerCfgBeforePath = Join-Path $OutputRoot 'powercfg-before.txt'
$powerCfgAfterPath = Join-Path $OutputRoot 'powercfg-after.txt'
$archivePath = "$OutputRoot.zip"
$summary = [System.Collections.Generic.List[string]]::new()
$exitCode = 0
$transcriptStarted = $false
$helperInfrastructureState = $null

try {
    Start-Transcript -Path $transcriptPath -Force | Out-Null
    $transcriptStarted = $true
    Write-Host "Probe: $resolvedProbePath"
    Write-Host "Output: $OutputRoot"
    Write-Host '1/3 읽기 전용 토폴로지와 전원 설정을 수집합니다.'

    (& powercfg.exe /getactivescheme 2>&1 | Out-String).Trim() |
        Set-Content -LiteralPath $powerCfgBeforePath -Encoding utf8
    $before = Invoke-TopologyProbe -Executable $resolvedProbePath -Destination $prePath
    Add-ReportSummary -Lines $summary -Label 'READ-ONLY BEFORE' -Report $before

    if (-not $FrequencyDomainTest) {
        $summary.Insert(0, 'VERDICT: READ_ONLY_COMPLETE')
        $summary.Insert(1, '주파수 설정은 변경하지 않았습니다. 이 JSON을 먼저 검토한 뒤 도메인 시험 여부를 결정하세요.')
        Write-Host '읽기 전용 수집 완료. 주파수 설정은 변경되지 않았습니다.' -ForegroundColor Green
    }
    elseif ($before.capabilities.supportsIndependentLpeFrequency -eq $false) {
        $summary.Insert(0, 'VERDICT: NOT_SUPPORTED_BY_READ_ONLY_EVIDENCE')
        $summary.Insert(1, '토폴로지 또는 전원 레지스터 전제조건이 실패하여 주파수 쓰기 시험을 실행하지 않았습니다.')
        Write-Warning '전제조건이 실패해 주파수 쓰기 시험을 실행하지 않았습니다.'
        $exitCode = 2
    }
    else {
        if (-not (Get-IsAdministrator)) {
            throw '주파수 도메인 시험은 관리자 PowerShell에서 실행해야 합니다.'
        }

        if ($before.powerPlan.effectiveOverlayReadStatus -eq 0 -and
            [Guid]$before.powerPlan.effectiveOverlay -ne [Guid]::Empty) {
            throw "Windows 전원 모드를 '균형 조정(Balanced)'으로 바꾼 뒤 다시 실행하세요. 현재 overlay=$($before.powerPlan.effectiveOverlay)"
        }

        if (-not $Force) {
            Write-Host ''
            Write-Host "각 e100/e101/e102 레지스터를 잠시 ${TestLimitMhz}MHz로 설정하고 MhzLimit을 측정합니다." -ForegroundColor Yellow
            Write-Host '각 단계 직후 원래 AC/DC 값을 복원하지만, 시험 중 전원을 끄거나 프로세스를 강제 종료하면 안 됩니다.' -ForegroundColor Yellow
            if ($StopHelper) {
                Write-Host '실측 중 Helper 재시작을 막기 위해 Game Bar를 닫고 관련 예약 작업을 임시 비활성화합니다.' -ForegroundColor Yellow
            }
            $confirmation = Read-Host '계속하려면 EX-TEST를 입력하세요'
            if ($confirmation -cne 'EX-TEST') {
                throw '사용자가 주파수 도메인 시험을 취소했습니다.'
            }
        }

        if ($StopHelper) {
            Write-Host 'Helper와 자동 재시작 경로를 일시 중지합니다.'
            $helperInfrastructureState = Suspend-HelperInfrastructure
            $summary.Add('Helper suppression: Game Bar closed; helper tasks temporarily disabled.')
        }
        else {
            $helper = @(Get-Process -Name XboxGamingBarHelper -ErrorAction SilentlyContinue)
            if ($helper.Count -gt 0) {
                throw 'XboxGamingBarHelper가 실행 중입니다. -StopHelper를 추가하거나 Widget과 Helper를 직접 종료하세요.'
            }
        }

        Write-Host '2/3 주파수 도메인 격리 시험을 실행합니다.'
        $testArguments = @{
            Executable = $resolvedProbePath
            Destination = $testPath
            RunFrequencyDomainTest = $true
            LimitMhz = $TestLimitMhz
        }
        $test = Invoke-TopologyProbe @testArguments
        Add-ReportSummary -Lines $summary -Label 'FREQUENCY DOMAIN TEST' -Report $test

        Write-Host '3/3 복원 후 전원 설정을 다시 읽어 원상복구를 검증합니다.'
        $after = Invoke-TopologyProbe -Executable $resolvedProbePath -Destination $postPath
        (& powercfg.exe /getactivescheme 2>&1 | Out-String).Trim() |
            Set-Content -LiteralPath $powerCfgAfterPath -Encoding utf8
        Add-ReportSummary -Lines $summary -Label 'READ-ONLY AFTER' -Report $after

        $allStepsRestored = @($test.frequencyDomainTest.steps).Count -gt 0 -and
            @($test.frequencyDomainTest.steps | Where-Object { -not $_.restoreSucceeded }).Count -eq 0
        $powerPlanRestored = Compare-PowerPlanSettings -Before $before.powerPlan -After $after.powerPlan
        $independent = $test.capabilities.supportsIndependentLpeFrequency
        $summary.Insert(0, "RESTORE: steps=$allStepsRestored, readback=$powerPlanRestored")

        if (-not $allStepsRestored -or -not $powerPlanRestored) {
            $summary.Insert(0, 'VERDICT: FAIL_RESTORE')
            Write-Host '원래 전원 설정 복원 검증에 실패했습니다. summary.txt와 JSON을 확인하세요.' -ForegroundColor Red
            $exitCode = 3
        }
        elseif ($independent -eq $true) {
            $summary.Insert(0, 'VERDICT: PASS_INDEPENDENT_LP_E_FREQUENCY')
            Write-Host 'PASS: E와 LP-E 주파수 도메인이 분리되어 있고 원래 설정도 복원됐습니다.' -ForegroundColor Green
        }
        elseif ($independent -eq $false) {
            $summary.Insert(0, 'VERDICT: FAIL_NO_LP_E_ONLY_REGISTER')
            Write-Warning 'FAIL: LP-E 전체만 변경하는 전용 레지스터가 관측되지 않았습니다.'
            $exitCode = 4
        }
        else {
            $summary.Insert(0, 'VERDICT: INCONCLUSIVE')
            Write-Warning '판정 불가: 모든 대상 코어에서 MhzLimit 변화가 관측되지 않았습니다.'
            $exitCode = 2
        }
    }
}
catch {
    $summary.Insert(0, 'VERDICT: SCRIPT_ERROR')
    $summary.Insert(1, $_.Exception.Message)
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 1
}
finally {
    if ($null -ne $helperInfrastructureState) {
        try {
            Restore-HelperInfrastructure -State $helperInfrastructureState
            $summary.Add('Helper suppression restore: scheduled tasks and service startup restored; open the Widget to restart the Helper.')
            Write-Host 'Helper 자동 시작 설정을 복구했습니다. 측정 후 Widget을 열면 Helper가 다시 시작됩니다.'
        }
        catch {
            $summary.Insert(0, "HELPER_RESTORE_FAILED: $($_.Exception.Message)")
            Write-Warning "Helper 자동 시작 설정 복구 실패: $($_.Exception.Message)"
            if ($exitCode -eq 0) {
                $exitCode = 5
            }
        }
    }

    $summary | Set-Content -LiteralPath $summaryPath -Encoding utf8
    if ($transcriptStarted) {
        Stop-Transcript | Out-Null
    }

    Compress-Archive -Path (Join-Path $OutputRoot '*') -DestinationPath $archivePath -CompressionLevel Optimal -Force
    Write-Host "요약: $summaryPath"
    Write-Host "공유용 압축 파일: $archivePath"
}

exit $exitCode
