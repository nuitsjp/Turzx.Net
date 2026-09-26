[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateNotNullOrEmpty()]
    [string]$ProgramPath,

    [Parameter(Mandatory = $false, Position = 1)]
    [AllowEmptyCollection()]
    [string[]]$ProgramArguments = @(),

    [Parameter(Mandatory = $true, Position = 2)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory,

    [Parameter(Mandatory = $false)]
    [ValidateRange(0.1, 60.0)]
    [double]$SampleIntervalSeconds = 1.0
)

# This is an experiment helper.  Counter failures are recorded as null values and
# text errors; they are never changed to zero merely to make a row complete.
$ErrorActionPreference = 'Stop'

$BaselineSeconds = 20.0
$PostSeconds = 10.0
$ProcessorCount = [Environment]::ProcessorCount

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$ResolvedOutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$MeasurementId = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmssfff', [Globalization.CultureInfo]::InvariantCulture)
$JsonPath = Join-Path -Path $ResolvedOutputDirectory -ChildPath ('measure-probe-{0}.json' -f $MeasurementId)
$CsvPath = Join-Path -Path $ResolvedOutputDirectory -ChildPath ('measure-probe-{0}.csv' -f $MeasurementId)

$script:Measurements = New-Object 'System.Collections.Generic.List[object]'
$script:SampleIntervalSeconds = $SampleIntervalSeconds
$script:ProcessorCount = $ProcessorCount
$script:PreviousSampleStartUtc = $null
$script:PreviousProcessCpuSeconds = $null
$script:PreviousProcessSampleUtc = $null
$script:PreviousSystemCpuSeconds = $null
$script:PreviousSystemSampleUtc = $null
$script:CounterCache = @{}

function ConvertTo-WindowsCommandLineArgument {
    param(
        [AllowNull()]
        [string]$Argument
    )

    # Start-Process joins ArgumentList into one command line.  Quote every
    # argument with the CommandLineToArgvW escaping rules so spaces, #, &, and
    # trailing backslashes survive that join unchanged.
    if ($null -eq $Argument -or $Argument.Length -eq 0) {
        return '""'
    }

    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('"')
    $backslashCount = 0
    foreach ($character in $Argument.ToCharArray()) {
        if ($character -eq '\') {
            $backslashCount++
            continue
        }

        if ($character -eq '"') {
            if ($backslashCount -gt 0) {
                [void]$builder.Append((('\' * ($backslashCount * 2 + 1)) -join ''))
            }
            else {
                [void]$builder.Append('\')
            }
            [void]$builder.Append('"')
            $backslashCount = 0
            continue
        }

        if ($backslashCount -gt 0) {
            [void]$builder.Append((('\' * $backslashCount) -join ''))
            $backslashCount = 0
        }
        [void]$builder.Append($character)
    }

    if ($backslashCount -gt 0) {
        [void]$builder.Append((('\' * ($backslashCount * 2)) -join ''))
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Get-NumericPropertyResult {
    param(
        [AllowNull()]
        [object]$Object,
        [Parameter(Mandatory = $true)]
        [string]$PropertyName,
        [Parameter(Mandatory = $true)]
        [string]$SourceName
    )

    if ($null -eq $Object) {
        return [pscustomobject]@{
            Value = $null
            Error = '{0} is unavailable.' -f $SourceName
        }
    }

    $property = $Object.PSObject.Properties[$PropertyName]
    if ($null -eq $property) {
        return [pscustomobject]@{
            Value = $null
            Error = '{0}.{1} is unavailable.' -f $SourceName, $PropertyName
        }
    }
    if ($null -eq $property.Value) {
        return [pscustomobject]@{
            Value = $null
            Error = '{0}.{1} is null.' -f $SourceName, $PropertyName
        }
    }

    try {
        return [pscustomobject]@{
            Value = [double]$property.Value
            Error = $null
        }
    }
    catch {
        return [pscustomobject]@{
            Value = $null
            Error = '{0}.{1} is not numeric: {2}' -f $SourceName, $PropertyName, $_.Exception.Message
        }
    }
}

function Get-CounterRows {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CacheKey,
        [Parameter(Mandatory = $true)]
        [string]$ClassName,
        [AllowEmptyString()]
        [string]$Filter
    )

    if ($script:CounterCache.ContainsKey($CacheKey)) {
        $cached = $script:CounterCache[$CacheKey]
        if (-not $cached.Available) {
            return [pscustomobject]@{
                Rows = $null
                Error = $cached.Error
            }
        }
    }

    try {
        if ([string]::IsNullOrEmpty($Filter)) {
            $rows = @(Get-CimInstance -ClassName $ClassName -ErrorAction Stop)
        }
        else {
            $rows = @(Get-CimInstance -ClassName $ClassName -Filter $Filter -ErrorAction Stop)
        }

        if ($rows.Count -eq 0) {
            $errorText = '{0} ({1}) returned no instances.' -f $ClassName, $Filter
            $script:CounterCache[$CacheKey] = [pscustomobject]@{
                Available = $false
                Error = $errorText
            }
            return [pscustomobject]@{
                Rows = $null
                Error = $errorText
            }
        }

        $script:CounterCache[$CacheKey] = [pscustomobject]@{
            Available = $true
            Error = $null
        }
        return [pscustomobject]@{
            Rows = $rows
            Error = $null
        }
    }
    catch {
        $errorText = '{0}: {1}' -f $ClassName, $_.Exception.Message
        $script:CounterCache[$CacheKey] = [pscustomobject]@{
            Available = $false
            Error = $errorText
        }
        return [pscustomobject]@{
            Rows = $null
            Error = $errorText
        }
    }
}

function Get-ProcessSnapshot {
    param(
        [int]$ProcessId,
        [DateTime]$SampleStartUtc
    )

    $snapshot = [ordered]@{
        ProcessState = $null
        ProcessError = $null
        ProcessCpuTimeSeconds = $null
        ProcessCpuPercentOneCore = $null
        ProcessCpuPercentAllCores = $null
        ProcessWorkingSetBytes = $null
        ProcessPrivateBytes = $null
        ProcessThreadCount = $null
    }

    if ($ProcessId -le 0) {
        $snapshot.ProcessState = 'NotStarted'
        return [pscustomobject]$snapshot
    }

    try {
        $process = Get-Process -Id $ProcessId -ErrorAction Stop
        $cpuSeconds = $process.TotalProcessorTime.TotalSeconds
        $snapshot.ProcessState = 'Running'
        $snapshot.ProcessCpuTimeSeconds = [double]$cpuSeconds
        $snapshot.ProcessWorkingSetBytes = [Int64]$process.WorkingSet64
        $snapshot.ProcessPrivateBytes = [Int64]$process.PrivateMemorySize64
        $snapshot.ProcessThreadCount = [int]$process.Threads.Count

        if ($null -ne $script:PreviousProcessCpuSeconds -and $null -ne $script:PreviousProcessSampleUtc) {
            $elapsedSeconds = ($SampleStartUtc - $script:PreviousProcessSampleUtc).TotalSeconds
            if ($elapsedSeconds -le 0) {
                $snapshot.ProcessError = 'Process CPU interval was not positive.'
            }
            else {
                $cpuDeltaSeconds = $cpuSeconds - $script:PreviousProcessCpuSeconds
                if ($cpuDeltaSeconds -lt 0) {
                    $snapshot.ProcessError = 'Process CPU time moved backwards; CPU percentage is unavailable.'
                }
                else {
                    $oneCorePercent = ($cpuDeltaSeconds / $elapsedSeconds) * 100.0
                    $snapshot.ProcessCpuPercentOneCore = [double]$oneCorePercent
                    $snapshot.ProcessCpuPercentAllCores = [double]($oneCorePercent / $script:ProcessorCount)
                }
            }
        }

        $script:PreviousProcessCpuSeconds = $cpuSeconds
        $script:PreviousProcessSampleUtc = $SampleStartUtc
    }
    catch {
        $snapshot.ProcessState = 'ExitedOrUnavailable'
        $snapshot.ProcessError = 'Process {0}: {1}' -f $ProcessId, $_.Exception.Message
    }

    return [pscustomobject]$snapshot
}

function Get-SystemSnapshot {
    param(
        [DateTime]$SampleStartUtc
    )

    $snapshot = [ordered]@{
        SystemState = $null
        SystemError = $null
        SystemProcessId = $null
        SystemCpuTimeSeconds = $null
        SystemCpuPercentOneCore = $null
        SystemCpuPercentAllCores = $null
    }

    try {
        $systemProcesses = @(Get-Process -Name System -ErrorAction Stop)
        if ($systemProcesses.Count -eq 0) {
            throw 'The System process was not found.'
        }
        $systemProcess = $systemProcesses[0]
        if ($null -eq $systemProcess.TotalProcessorTime) {
            throw 'System process CPU time is not accessible with the current permissions.'
        }
        $cpuSeconds = $systemProcess.TotalProcessorTime.TotalSeconds
        $snapshot.SystemState = 'Running'
        $snapshot.SystemProcessId = [int]$systemProcess.Id
        $snapshot.SystemCpuTimeSeconds = [double]$cpuSeconds

        if ($null -ne $script:PreviousSystemCpuSeconds -and $null -ne $script:PreviousSystemSampleUtc) {
            $elapsedSeconds = ($SampleStartUtc - $script:PreviousSystemSampleUtc).TotalSeconds
            if ($elapsedSeconds -le 0) {
                $snapshot.SystemError = 'System CPU interval was not positive.'
            }
            else {
                $cpuDeltaSeconds = $cpuSeconds - $script:PreviousSystemCpuSeconds
                if ($cpuDeltaSeconds -lt 0) {
                    $snapshot.SystemError = 'System CPU time moved backwards; CPU percentage is unavailable.'
                }
                else {
                    $oneCorePercent = ($cpuDeltaSeconds / $elapsedSeconds) * 100.0
                    $snapshot.SystemCpuPercentOneCore = [double]$oneCorePercent
                    $snapshot.SystemCpuPercentAllCores = [double]($oneCorePercent / $script:ProcessorCount)
                }
            }
        }

        $script:PreviousSystemCpuSeconds = $cpuSeconds
        $script:PreviousSystemSampleUtc = $SampleStartUtc
    }
    catch {
        $snapshot.SystemState = 'Unavailable'
        $snapshot.SystemError = 'Get-Process System: {0}' -f $_.Exception.Message
    }

    return [pscustomobject]$snapshot
}

function Add-ResultError {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]]$Errors,
        [AllowNull()]
        [string]$ErrorText
    )

    if (-not [string]::IsNullOrEmpty($ErrorText)) {
        [void]$Errors.Add($ErrorText)
    }
}

function Get-SampleRecord {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Phase,
        [Parameter(Mandatory = $true)]
        [double]$PhaseElapsedSeconds,
        [int]$TargetProcessId
    )

    $sampleStartUtc = [DateTime]::UtcNow
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $errors = New-Object 'System.Collections.Generic.List[string]'

    $intervalSincePrevious = $null
    if ($null -ne $script:PreviousSampleStartUtc) {
        $intervalSincePrevious = ($sampleStartUtc - $script:PreviousSampleStartUtc).TotalSeconds
    }
    $script:PreviousSampleStartUtc = $sampleStartUtc

    $processSnapshot = Get-ProcessSnapshot -ProcessId $TargetProcessId -SampleStartUtc $sampleStartUtc
    Add-ResultError -Errors $errors -ErrorText $processSnapshot.ProcessError
    $systemSnapshot = Get-SystemSnapshot -SampleStartUtc $sampleStartUtc
    Add-ResultError -Errors $errors -ErrorText $systemSnapshot.SystemError

    $totalCpuPercent = $null
    $totalDpcPercent = $null
    $totalInterruptPercent = $null
    $processorResult = Get-CounterRows -CacheKey 'ProcessorTotal' -ClassName 'Win32_PerfFormattedData_PerfOS_Processor' -Filter "Name='_Total'"
    Add-ResultError -Errors $errors -ErrorText $processorResult.Error
    if ($null -ne $processorResult.Rows) {
        $processorCounter = @($processorResult.Rows)[0]
        $propertyResult = Get-NumericPropertyResult -Object $processorCounter -PropertyName 'PercentProcessorTime' -SourceName 'Processor._Total'
        Add-ResultError -Errors $errors -ErrorText $propertyResult.Error
        $totalCpuPercent = $propertyResult.Value
        $propertyResult = Get-NumericPropertyResult -Object $processorCounter -PropertyName 'PercentDPCTime' -SourceName 'Processor._Total'
        Add-ResultError -Errors $errors -ErrorText $propertyResult.Error
        $totalDpcPercent = $propertyResult.Value
        $propertyResult = Get-NumericPropertyResult -Object $processorCounter -PropertyName 'PercentInterruptTime' -SourceName 'Processor._Total'
        Add-ResultError -Errors $errors -ErrorText $propertyResult.Error
        $totalInterruptPercent = $propertyResult.Value
    }

    $memoryAvailableMBytes = $null
    $memoryPagesPerSec = $null
    $memoryResult = Get-CounterRows -CacheKey 'Memory' -ClassName 'Win32_PerfFormattedData_PerfOS_Memory' -Filter ''
    Add-ResultError -Errors $errors -ErrorText $memoryResult.Error
    if ($null -ne $memoryResult.Rows) {
        $memoryCounter = @($memoryResult.Rows)[0]
        $propertyResult = Get-NumericPropertyResult -Object $memoryCounter -PropertyName 'AvailableMBytes' -SourceName 'Memory'
        Add-ResultError -Errors $errors -ErrorText $propertyResult.Error
        $memoryAvailableMBytes = $propertyResult.Value
        $propertyResult = Get-NumericPropertyResult -Object $memoryCounter -PropertyName 'PagesPersec' -SourceName 'Memory'
        Add-ResultError -Errors $errors -ErrorText $propertyResult.Error
        $memoryPagesPerSec = $propertyResult.Value
    }

    $diskBytesPerSec = $null
    $diskReadBytesPerSec = $null
    $diskWriteBytesPerSec = $null
    $diskPercentTime = $null
    $diskQueueLength = $null
    $diskResult = Get-CounterRows -CacheKey 'PhysicalDiskTotal' -ClassName 'Win32_PerfFormattedData_PerfDisk_PhysicalDisk' -Filter "Name='_Total'"
    Add-ResultError -Errors $errors -ErrorText $diskResult.Error
    if ($null -ne $diskResult.Rows) {
        $diskCounter = @($diskResult.Rows)[0]
        $propertyResult = Get-NumericPropertyResult -Object $diskCounter -PropertyName 'DiskBytesPersec' -SourceName 'PhysicalDisk._Total'
        Add-ResultError -Errors $errors -ErrorText $propertyResult.Error
        $diskBytesPerSec = $propertyResult.Value
        $propertyResult = Get-NumericPropertyResult -Object $diskCounter -PropertyName 'DiskReadBytesPersec' -SourceName 'PhysicalDisk._Total'
        Add-ResultError -Errors $errors -ErrorText $propertyResult.Error
        $diskReadBytesPerSec = $propertyResult.Value
        $propertyResult = Get-NumericPropertyResult -Object $diskCounter -PropertyName 'DiskWriteBytesPersec' -SourceName 'PhysicalDisk._Total'
        Add-ResultError -Errors $errors -ErrorText $propertyResult.Error
        $diskWriteBytesPerSec = $propertyResult.Value
        $propertyResult = Get-NumericPropertyResult -Object $diskCounter -PropertyName 'PercentDiskTime' -SourceName 'PhysicalDisk._Total'
        Add-ResultError -Errors $errors -ErrorText $propertyResult.Error
        $diskPercentTime = $propertyResult.Value
        $propertyResult = Get-NumericPropertyResult -Object $diskCounter -PropertyName 'CurrentDiskQueueLength' -SourceName 'PhysicalDisk._Total'
        Add-ResultError -Errors $errors -ErrorText $propertyResult.Error
        $diskQueueLength = $propertyResult.Value
    }

    $gpuEngineCount = $null
    $gpuUtilizationSum = $null
    $gpuEngineObjects = $null
    $gpuEngineJson = $null
    if ($TargetProcessId -gt 0) {
        $gpuResult = Get-CounterRows -CacheKey 'GpuEngines' -ClassName 'Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine' -Filter ''
        Add-ResultError -Errors $errors -ErrorText $gpuResult.Error
        if ($null -ne $gpuResult.Rows) {
            $gpuRows = @($gpuResult.Rows | Where-Object {
                    $_.Name -like ('pid_{0}_*' -f $TargetProcessId)
                })
            $gpuEngineCount = $gpuRows.Count
            $gpuEngineObjectsList = New-Object 'System.Collections.Generic.List[object]'
            $allGpuValuesAvailable = $true
            $gpuSum = 0.0
            foreach ($gpuRow in $gpuRows) {
                $engineName = $null
                if ($null -ne $gpuRow.PSObject.Properties['Name']) {
                    $engineName = [string]$gpuRow.Name
                }
                else {
                    Add-ResultError -Errors $errors -ErrorText 'GPU engine Name is unavailable.'
                }
                $propertyResult = Get-NumericPropertyResult -Object $gpuRow -PropertyName 'UtilizationPercentage' -SourceName ('GPU engine {0}' -f $engineName)
                Add-ResultError -Errors $errors -ErrorText $propertyResult.Error
                if ($null -eq $propertyResult.Value) {
                    $allGpuValuesAvailable = $false
                }
                else {
                    $gpuSum += $propertyResult.Value
                }
                [void]$gpuEngineObjectsList.Add([pscustomobject][ordered]@{
                        Name = $engineName
                        UtilizationPercentage = $propertyResult.Value
                    })
            }
            $gpuEngineObjects = @($gpuEngineObjectsList.ToArray())
            if ($allGpuValuesAvailable) {
                # This is the sum of engines owned by the benchmark process only.
                # It is deliberately not labelled or interpreted as total GPU use.
                $gpuUtilizationSum = [double]$gpuSum
            }
            $gpuEngineJson = $gpuEngineObjects | ConvertTo-Json -Compress -Depth 4
            if ($null -eq $gpuEngineJson) {
                $gpuEngineJson = '[]'
            }
        }
    }
    else {
        Add-ResultError -Errors $errors -ErrorText 'GPU process engine metric is not applicable before the benchmark starts.'
    }

    $stopwatch.Stop()
    $errorText = $null
    if ($errors.Count -gt 0) {
        $errorText = $errors -join ' | '
    }

    return [pscustomobject][ordered]@{
        SampleTimestampUtc = $sampleStartUtc.ToString('o', [Globalization.CultureInfo]::InvariantCulture)
        Phase = $Phase
        PhaseElapsedSeconds = [double]$PhaseElapsedSeconds
        TargetProcessId = if ($TargetProcessId -gt 0) { $TargetProcessId } else { $null }
        ProcessState = $processSnapshot.ProcessState
        ProcessError = $processSnapshot.ProcessError
        ProcessCpuTimeSeconds = $processSnapshot.ProcessCpuTimeSeconds
        ProcessCpuPercentOneCore = $processSnapshot.ProcessCpuPercentOneCore
        ProcessCpuPercentAllCores = $processSnapshot.ProcessCpuPercentAllCores
        ProcessWorkingSetBytes = $processSnapshot.ProcessWorkingSetBytes
        ProcessPrivateBytes = $processSnapshot.ProcessPrivateBytes
        ProcessThreadCount = $processSnapshot.ProcessThreadCount
        SystemState = $systemSnapshot.SystemState
        SystemError = $systemSnapshot.SystemError
        SystemProcessId = $systemSnapshot.SystemProcessId
        SystemCpuTimeSeconds = $systemSnapshot.SystemCpuTimeSeconds
        SystemCpuPercentOneCore = $systemSnapshot.SystemCpuPercentOneCore
        SystemCpuPercentAllCores = $systemSnapshot.SystemCpuPercentAllCores
        TotalCpuPercent = $totalCpuPercent
        TotalDpcPercent = $totalDpcPercent
        TotalInterruptPercent = $totalInterruptPercent
        MemoryAvailableMBytes = $memoryAvailableMBytes
        MemoryPagesPerSec = $memoryPagesPerSec
        DiskBytesPerSec = $diskBytesPerSec
        DiskReadBytesPerSec = $diskReadBytesPerSec
        DiskWriteBytesPerSec = $diskWriteBytesPerSec
        DiskPercentTime = $diskPercentTime
        DiskQueueLength = $diskQueueLength
        GpuEngineCount = $gpuEngineCount
        GpuProcessEngineUtilizationSumPercent = $gpuUtilizationSum
        GpuEngineUtilizationJson = $gpuEngineJson
        GpuEngines = $gpuEngineObjects
        TargetIntervalSeconds = [double]$script:SampleIntervalSeconds
        IntervalSincePreviousSampleSeconds = $intervalSincePrevious
        SamplingOverheadMilliseconds = [double]$stopwatch.Elapsed.TotalMilliseconds
        MeasurementErrorCount = [int]$errors.Count
        MeasurementErrors = $errorText
    }
}

function Add-TimedSample {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Phase,
        [Parameter(Mandatory = $true)]
        [double]$PhaseElapsedSeconds,
        [int]$TargetProcessId
    )

    $sample = Get-SampleRecord -Phase $Phase -PhaseElapsedSeconds $PhaseElapsedSeconds -TargetProcessId $TargetProcessId
    [void]$script:Measurements.Add($sample)
}

function Wait-UntilUtc {
    param(
        [Parameter(Mandatory = $true)]
        [DateTime]$TargetUtc
    )

    $waitSeconds = ($TargetUtc - [DateTime]::UtcNow).TotalSeconds
    if ($waitSeconds -gt 0) {
        $waitMilliseconds = [int][Math]::Max(1, [Math]::Ceiling($waitSeconds * 1000.0))
        Start-Sleep -Milliseconds $waitMilliseconds
    }
}

function Invoke-FixedPhase {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Phase,
        [Parameter(Mandatory = $true)]
        [double]$DurationSeconds,
        [int]$TargetProcessId
    )

    $phaseStartUtc = [DateTime]::UtcNow
    $nextSampleUtc = $phaseStartUtc
    $sampleCount = 0
    while ($true) {
        $nowUtc = [DateTime]::UtcNow
        $phaseElapsedSeconds = ($nowUtc - $phaseStartUtc).TotalSeconds
        if ($sampleCount -gt 0 -and $phaseElapsedSeconds -ge $DurationSeconds) {
            break
        }

        Add-TimedSample -Phase $Phase -PhaseElapsedSeconds $phaseElapsedSeconds -TargetProcessId $TargetProcessId
        $sampleCount++
        $nextSampleUtc = $nextSampleUtc.AddSeconds($script:SampleIntervalSeconds)
        Wait-UntilUtc -TargetUtc $nextSampleUtc
    }
}

function Invoke-RunPhase {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)]
        [int]$ProcessId
    )

    $runStartUtc = [DateTime]::UtcNow
    $nextSampleUtc = $runStartUtc
    while ($true) {
        $phaseElapsedSeconds = ([DateTime]::UtcNow - $runStartUtc).TotalSeconds
        Add-TimedSample -Phase 'Run' -PhaseElapsedSeconds $phaseElapsedSeconds -TargetProcessId $ProcessId

        $hasExited = $false
        try {
            $Process.Refresh()
            $hasExited = $Process.HasExited
        }
        catch {
            # If the process handle cannot be refreshed, a subsequent sample
            # cannot be attributed reliably; leave the error in metadata and end
            # the run phase so the post phase can begin.
            $hasExited = $true
        }

        if ($hasExited) {
            return [pscustomobject]@{
                StartUtc = $runStartUtc
                EndUtc = [DateTime]::UtcNow
            }
        }

        $nextSampleUtc = $nextSampleUtc.AddSeconds($script:SampleIntervalSeconds)
        Wait-UntilUtc -TargetUtc $nextSampleUtc
    }
}

$quotedArguments = @($ProgramArguments | ForEach-Object {
        ConvertTo-WindowsCommandLineArgument -Argument $_
    })

$targetProcess = $null
$targetProcessId = 0
$targetExitCode = $null
$startError = $null
$runTiming = $null
$postWasCollected = $false
$measurementStartUtc = [DateTime]::UtcNow

Invoke-FixedPhase -Phase 'Baseline' -DurationSeconds $BaselineSeconds -TargetProcessId 0

try {
    # Keep this as Start-Process with an explicit ArgumentList.  Each element is
    # already Windows-command-line quoted above, so a USB path containing # and &
    # reaches the benchmark as one argument.
    $targetProcess = Start-Process -FilePath $ProgramPath -ArgumentList $quotedArguments -WindowStyle Hidden -PassThru -ErrorAction Stop -RedirectStandardOutput (Join-Path $ResolvedOutputDirectory 'stdout.log') -RedirectStandardError (Join-Path $ResolvedOutputDirectory 'stderr.log')
    $targetProcessId = [int]$targetProcess.Id
    $runTiming = Invoke-RunPhase -Process $targetProcess -ProcessId $targetProcessId

    try {
        $targetProcess.Refresh()
        if ($targetProcess.HasExited) {
            $targetExitCode = [int]$targetProcess.ExitCode
        }
    }
    catch {
        $targetExitCode = $null
    }

    Invoke-FixedPhase -Phase 'Post' -DurationSeconds $PostSeconds -TargetProcessId $targetProcessId
    $postWasCollected = $true
}
catch {
    $startError = $_.Exception.Message
}

$measurementEndUtc = [DateTime]::UtcNow
$metadata = [ordered]@{
    SchemaVersion = 1
    MeasurementId = $MeasurementId
    MeasurementStartedAtUtc = $measurementStartUtc.ToString('o', [Globalization.CultureInfo]::InvariantCulture)
    MeasurementCompletedAtUtc = $measurementEndUtc.ToString('o', [Globalization.CultureInfo]::InvariantCulture)
    ProgramPath = $ProgramPath
    ProgramArguments = @($ProgramArguments)
    QuotedProgramArguments = @($quotedArguments)
    OutputDirectory = $ResolvedOutputDirectory
    SampleIntervalSeconds = [double]$SampleIntervalSeconds
    BaselineSeconds = $BaselineSeconds
    PostSeconds = $PostSeconds
    LogicalProcessorCount = $ProcessorCount
    TargetProcessId = if ($targetProcessId -gt 0) { $targetProcessId } else { $null }
    TargetExitCode = $targetExitCode
    PostCollected = $postWasCollected
    StartOrRunError = $startError
    CounterNotes = @(
        'Process CPU percentages use CPU-time deltas: one logical core is 100%; all-core percentage divides by LogicalProcessorCount.'
        'TotalCpuPercent, TotalDpcPercent, and TotalInterruptPercent come from the PerfOS Processor _Total instance.'
        'GpuProcessEngineUtilizationSumPercent is only the sum of matching pid_PID_ engine instances; it is not total GPU utilization.'
        'Unavailable counters and properties remain null and are described in MeasurementErrors.'
        'SamplingOverheadMilliseconds and IntervalSincePreviousSampleSeconds expose measurement overhead and actual spacing.'
    )
}

$jsonDocument = [ordered]@{
    Metadata = [pscustomobject]$metadata
    Samples = @($script:Measurements.ToArray())
}
$jsonDocument | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $JsonPath -Encoding UTF8

$csvRows = foreach ($sample in $script:Measurements) {
    [pscustomobject][ordered]@{
        SampleTimestampUtc = $sample.SampleTimestampUtc
        Phase = $sample.Phase
        PhaseElapsedSeconds = $sample.PhaseElapsedSeconds
        TargetProcessId = $sample.TargetProcessId
        ProcessState = $sample.ProcessState
        ProcessError = $sample.ProcessError
        ProcessCpuTimeSeconds = $sample.ProcessCpuTimeSeconds
        ProcessCpuPercentOneCore = $sample.ProcessCpuPercentOneCore
        ProcessCpuPercentAllCores = $sample.ProcessCpuPercentAllCores
        ProcessWorkingSetBytes = $sample.ProcessWorkingSetBytes
        ProcessPrivateBytes = $sample.ProcessPrivateBytes
        ProcessThreadCount = $sample.ProcessThreadCount
        SystemState = $sample.SystemState
        SystemError = $sample.SystemError
        SystemProcessId = $sample.SystemProcessId
        SystemCpuTimeSeconds = $sample.SystemCpuTimeSeconds
        SystemCpuPercentOneCore = $sample.SystemCpuPercentOneCore
        SystemCpuPercentAllCores = $sample.SystemCpuPercentAllCores
        TotalCpuPercent = $sample.TotalCpuPercent
        TotalDpcPercent = $sample.TotalDpcPercent
        TotalInterruptPercent = $sample.TotalInterruptPercent
        MemoryAvailableMBytes = $sample.MemoryAvailableMBytes
        MemoryPagesPerSec = $sample.MemoryPagesPerSec
        DiskBytesPerSec = $sample.DiskBytesPerSec
        DiskReadBytesPerSec = $sample.DiskReadBytesPerSec
        DiskWriteBytesPerSec = $sample.DiskWriteBytesPerSec
        DiskPercentTime = $sample.DiskPercentTime
        DiskQueueLength = $sample.DiskQueueLength
        GpuEngineCount = $sample.GpuEngineCount
        GpuProcessEngineUtilizationSumPercent = $sample.GpuProcessEngineUtilizationSumPercent
        GpuEngineUtilizationJson = $sample.GpuEngineUtilizationJson
        TargetIntervalSeconds = $sample.TargetIntervalSeconds
        IntervalSincePreviousSampleSeconds = $sample.IntervalSincePreviousSampleSeconds
        SamplingOverheadMilliseconds = $sample.SamplingOverheadMilliseconds
        MeasurementErrorCount = $sample.MeasurementErrorCount
        MeasurementErrors = $sample.MeasurementErrors
    }
}
$csvRows | Export-Csv -LiteralPath $CsvPath -NoTypeInformation -Encoding UTF8

Write-Output ('JSON: {0}' -f $JsonPath)
Write-Output ('CSV:  {0}' -f $CsvPath)
Write-Output ('Samples: {0}; PID: {1}; processor count: {2}' -f $script:Measurements.Count, $(if ($targetProcessId -gt 0) { $targetProcessId } else { 'not started' }), $ProcessorCount)
if ($null -ne $startError) {
    Write-Error ('Benchmark start or run failed: {0}' -f $startError)
    exit 1
}
if ($null -ne $targetExitCode -and $targetExitCode -ne 0) { exit $targetExitCode }
