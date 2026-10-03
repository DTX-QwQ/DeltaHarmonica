param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

if (-not $SkipTests) {
    dotnet run --project tests/DeltaHarmonica.Core.Tests/DeltaHarmonica.Core.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'MIDI 核心测试失败。' }
    dotnet run --project tests/DeltaHarmonica.Playback.Tests/DeltaHarmonica.Playback.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw '播放引擎测试失败。' }
}

$publishDirectory = Join-Path $PSScriptRoot 'artifacts/DeltaHarmonica'
dotnet publish src/DeltaHarmonica.App/DeltaHarmonica.App.csproj -c Release -r win-x64 --self-contained true -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw '发布失败。' }
$requiredFiles = @('DeltaHarmonica.App.exe', 'App.xbf', 'MainWindow.xbf', 'DeltaHarmonica.App.pri', 'tools/audio_to_midi/audio_to_midi.py', 'tools/audio_to_midi/requirements.txt', 'examples/小星星.mid')
foreach ($requiredFile in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $requiredFile) -PathType Leaf)) {
        throw "发布目录缺少必要文件：$requiredFile"
    }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $publishDirectory -Force
Write-Output "可运行版本：$(Join-Path $publishDirectory 'DeltaHarmonica.App.exe')"
