param(
    [int]$DebounceMs = 1000
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$watchPaths = @(
    Join-Path $root "ROS-APP"
)

$script = Join-Path $PSScriptRoot "auto_build.ps1"
$timer = New-Object System.Timers.Timer
$timer.Interval = $DebounceMs
$timer.AutoReset = $false

$lastChange = New-Object System.Collections.Concurrent.ConcurrentQueue[string]

$action = {
    $items = @()
    while ($lastChange.TryDequeue([ref]$item)) { $items += $item }
    $msg = if ($items.Count -gt 0) { "自动构建: " + ($items | Select-Object -First 5 -Unique) -join ", " } else { "自动构建" }
    & $script -Message $msg -Environment "prod"
}

$timer.add_Elapsed({
    & $action
})

$handlers = @()
foreach ($path in $watchPaths) {
    $fsw = New-Object System.IO.FileSystemWatcher
    $fsw.Path = $path
    $fsw.Filter = "*.*"
    $fsw.IncludeSubdirectories = $true
    $fsw.EnableRaisingEvents = $true

    $onChange = Register-ObjectEvent $fsw Changed -Action {
        $full = $Event.SourceEventArgs.FullPath
        if ($full -match "\\bin\\|\\obj\\|\\.git\\|\\publish\\") { return }
        $lastChange.Enqueue($full) | Out-Null
        $timer.Stop()
        $timer.Start()
    }
    $onCreate = Register-ObjectEvent $fsw Created -Action {
        $full = $Event.SourceEventArgs.FullPath
        if ($full -match "\\bin\\|\\obj\\|\\.git\\|\\publish\\") { return }
        $lastChange.Enqueue($full) | Out-Null
        $timer.Stop()
        $timer.Start()
    }
    $onRename = Register-ObjectEvent $fsw Renamed -Action {
        $full = $Event.SourceEventArgs.FullPath
        if ($full -match "\\bin\\|\\obj\\|\\.git\\|\\publish\\") { return }
        $lastChange.Enqueue($full) | Out-Null
        $timer.Stop()
        $timer.Start()
    }

    $handlers += $onChange, $onCreate, $onRename
}

Write-Host "Watching for changes. Press Ctrl+C to stop."
while ($true) { Start-Sleep -Seconds 1 }
