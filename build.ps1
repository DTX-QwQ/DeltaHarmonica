param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

if (-not $SkipTests) {
    dotnet run --project tests/DeltaHarmonica.Core.Tests/DeltaHarmonica.Core.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'MIDI 核心测试失败。' }
    dotnet run --project tests/DeltaHarmonica.Playback.Tests/DeltaHarmonica.Playback.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw '播放引擎测试失败。' }
    dotnet run --project tests/DeltaHarmonica.Audio.Tests/DeltaHarmonica.Audio.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw '内置预览音效测试失败。' }
}

$publishDirectory = Join-Path $PSScriptRoot 'artifacts/DeltaHarmonica'
dotnet publish src/DeltaHarmonica.App/DeltaHarmonica.App.csproj -c Release -r win-x64 --self-contained true -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw '发布失败。' }
$requiredFiles = @('DeltaHarmonica.App.exe', 'App.xbf', 'MainWindow.xbf', 'DeltaHarmonica.App.pri', 'Assets/logo.png', 'Assets/app.ico', 'tools/audio_to_midi/audio_to_midi.py', 'tools/audio_to_midi/requirements.txt', 'tools/audio_to_midi/vendor/music-tempo.min.js', 'tools/audio_to_midi/vendor/music-tempo.LICENSE', 'examples/小星星.mid')
foreach ($requiredFile in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $requiredFile) -PathType Leaf)) {
        throw "发布目录缺少必要文件：$requiredFile"
    }
}
$releaseReadme = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Raw).Replace('src/DeltaHarmonica.App/Assets/logo.png', 'Assets/logo.png')
Set-Content -LiteralPath (Join-Path $publishDirectory 'README.md') -Value $releaseReadme -Encoding utf8
Write-Output "可运行版本：$(Join-Path $publishDirectory 'DeltaHarmonica.App.exe')"
