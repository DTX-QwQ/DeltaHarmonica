param(
    [string]$OutputDirectory,
    [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'
$repoDirectory = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoDirectory ('.artifacts/range-ui-smoke-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$appDirectory = Join-Path $OutputDirectory 'app'
$publishDirectory = Join-Path $OutputDirectory 'publish'
$dataDirectory = Join-Path $OutputDirectory 'data'
$coreDirectory = Join-Path $OutputDirectory 'core'
New-Item -ItemType Directory -Force -Path $OutputDirectory, $appDirectory, $dataDirectory | Out-Null

function ConvertTo-CSharpLiteral([string]$Value) { return '@"' + $Value.Replace('"', '""') + '"' }
function Replace-Required([string]$Text, [string]$Old, [string]$New, [string]$Description) {
    if (-not $Text.Contains($Old)) { throw "UI smoke injection point missing: $Description" }
    return $Text.Replace($Old, $New)
}

if (-not $SkipPublish) {
    $sourceDirectory = Join-Path $repoDirectory 'src/DeltaHarmonica.App'
    $hashes = [ordered]@{}
    foreach ($sourceFile in Get-ChildItem -LiteralPath $sourceDirectory -File -Recurse) {
        $relativePath = [IO.Path]::GetRelativePath($sourceDirectory, $sourceFile.FullName)
        if ($relativePath -match '(^|[\\/])(bin|obj)([\\/]|$)') { continue }
        $destination = Join-Path $appDirectory $relativePath
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $sourceFile.FullName -Destination $destination -Force
        $hashes[$relativePath] = (Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash
    }
    $hashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'source-hashes.json') -Encoding utf8

    # Keep isolated builds from contending for the primary Core project's obj directory.
    $coreSourceDirectory = Join-Path $repoDirectory 'src/DeltaHarmonica.Core'
    foreach ($sourceFile in Get-ChildItem -LiteralPath $coreSourceDirectory -File -Recurse) {
        $relativePath = [IO.Path]::GetRelativePath($coreSourceDirectory, $sourceFile.FullName)
        if ($relativePath -match '(^|[\\/])(bin|obj)([\\/]|$)') { continue }
        $destination = Join-Path $coreDirectory $relativePath
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $sourceFile.FullName -Destination $destination -Force
    }

    $mainPath = Join-Path $appDirectory 'MainWindow.xaml.cs'
    $main = Get-Content -LiteralPath $mainPath -Raw
    $main = Replace-Required $main '_store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeltaHarmonica"));' ('_store = new(' + (ConvertTo-CSharpLiteral $dataDirectory) + ');') 'isolated settings'
    $main = Replace-Required $main '_player = new(_input, _previewAudio);' '_player = new(UiSmokeInputSink.Instance, UiSmokeAudioSink.Instance);' 'recording sinks'
    if (-not $main.Contains('_hotkeys.Apply(')) { throw 'UI smoke global-hotkey injection point missing.' }
    $main = [regex]::Replace($main, 'var error = _hotkeys\.Apply\([^\r\n]+\);', 'string? error = null;')
    Set-Content -LiteralPath $mainPath -Value $main -Encoding utf8

    $appPath = Join-Path $appDirectory 'App.xaml.cs'
    $app = Get-Content -LiteralPath $appPath -Raw
    $app = Replace-Required $app 'Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeltaHarmonica")' (ConvertTo-CSharpLiteral $dataDirectory) 'isolated crash log'
    $app = Replace-Required $app '_window.Activate();' '_window.Activate(); _window.DispatcherQueue.TryEnqueue(async () => await RangeUiSmoke.RunAsync((MainWindow)_window));' 'UI smoke entrypoint'
    Set-Content -LiteralPath $appPath -Value $app -Encoding utf8

    $manifestPath = Join-Path $appDirectory 'app.manifest'
    $manifest = (Get-Content -LiteralPath $manifestPath -Raw).Replace('level="requireAdministrator"', 'level="asInvoker"')
    Set-Content -LiteralPath $manifestPath -Value $manifest -Encoding utf8

    $projectPath = Join-Path $appDirectory 'DeltaHarmonica.App.csproj'
    $project = Get-Content -LiteralPath $projectPath -Raw
    $repoForward = $repoDirectory.Replace('\', '/')
    $project = $project.Replace('../DeltaHarmonica.Core/DeltaHarmonica.Core.csproj', '../core/DeltaHarmonica.Core.csproj')
    $project = $project.Replace('../../examples/', "$repoForward/examples/").Replace('../../tools/', "$repoForward/tools/")
    Set-Content -LiteralPath $projectPath -Value $project -Encoding utf8

    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'RangeUiSmoke.cs') -Destination (Join-Path $appDirectory 'RangeUiSmoke.cs') -Force
    dotnet publish $projectPath -c Release -r win-x64 --self-contained true -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Isolated WinUI smoke publish failed.' }
}

$executable = Join-Path $publishDirectory 'DeltaHarmonica.App.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Missing smoke executable: $executable" }
$savedOutput = $env:DELTA_RANGE_SMOKE_OUTPUT
$savedPhase = $env:DELTA_RANGE_SMOKE_PHASE
# Every run starts fresh, including -SkipPublish reruns of a prepared isolated build.
foreach ($settingsFile in @('settings.json', 'settings.json.tmp')) {
    $settingsPath = Join-Path $dataDirectory $settingsFile
    if (Test-Path -LiteralPath $settingsPath -PathType Leaf) { Remove-Item -LiteralPath $settingsPath }
}
try {
    $env:DELTA_RANGE_SMOKE_OUTPUT = $OutputDirectory
    foreach ($phase in @('edit', 'restart')) {
        $env:DELTA_RANGE_SMOKE_PHASE = $phase
        $resultPath = Join-Path $OutputDirectory "result-$phase.json"
        if (Test-Path -LiteralPath $resultPath) { Remove-Item -LiteralPath $resultPath }
        $process = Start-Process -FilePath $executable -WorkingDirectory $publishDirectory -WindowStyle Hidden -PassThru
        $deadline = [DateTime]::UtcNow.AddSeconds(55)
        while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 200
            $process.Refresh()
        }
        if (-not $process.HasExited) {
            $process.Kill()
            throw "UI smoke timed out in phase $phase. See progress-$phase.json and data/crash.log."
        }
        if (-not (Test-Path -LiteralPath $resultPath)) { throw "UI smoke phase $phase exited without results (exit $($process.ExitCode))." }
        $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        Write-Output "$phase phase: $($result.Checks.Count) checks; passed=$($result.Passed); process=$($result.ProcessId)"
        if (-not $result.Passed) {
            $result.Checks | Where-Object { -not $_.Passed } | Format-Table Name, Detail -Wrap | Out-String | Write-Output
            throw "UI smoke failed in phase $phase. Results: $resultPath"
        }
    }
}
finally {
    $env:DELTA_RANGE_SMOKE_OUTPUT = $savedOutput
    $env:DELTA_RANGE_SMOKE_PHASE = $savedPhase
}
Write-Output "UI smoke evidence: $OutputDirectory"
