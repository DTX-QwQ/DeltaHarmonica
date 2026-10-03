using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeltaHarmonica.App.Services;

/// <summary>Runs local audio transcription in an application-owned Python venv.</summary>
public sealed class AudioTranscriptionService
{
    private const string EnvironmentRevision = "basic-pitch-0.4.0-onnx-1.19.2-tempo-v2";
    private readonly string _dataDirectory;
    private readonly string _toolDirectory;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    public AudioTranscriptionService(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _toolDirectory = Path.Combine(AppContext.BaseDirectory, "tools", "audio_to_midi");
    }

    public string EnvironmentDirectory => Path.Combine(_dataDirectory, "audio-python-onnx-v1");
    private string EnvironmentPython => Path.Combine(EnvironmentDirectory, "Scripts", "python.exe");
    private string ReadyMarker => Path.Combine(EnvironmentDirectory, "delta-harmonica-ready.txt");
    private string ScriptPath => Path.Combine(_toolDirectory, "audio_to_midi.py");
    private string TempoScriptPath => Path.Combine(_toolDirectory, "vendor", "music-tempo.min.js");

    /// <summary>The last setup completed and its managed files still exist.</summary>
    public bool IsInstalled
    {
        get
        {
            try
            {
                return File.Exists(EnvironmentPython) && File.Exists(ScriptPath) && File.Exists(TempoScriptPath) &&
                       File.Exists(Path.Combine(_toolDirectory, "requirements.txt")) &&
                       File.Exists(ReadyMarker) && File.ReadAllText(ReadyMarker).Trim() == EnvironmentRevision;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    /// <summary>
    /// Invoked by the install button only. The supplied executable is inspected;
    /// all pip operations target a private venv, never the supplied environment.
    /// </summary>
    public async Task EnsureEnvironmentAsync(
        string pythonExe, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pythonExe);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            VerifyBundledTools();
            Directory.CreateDirectory(_dataDirectory);
            pythonExe = Path.GetFullPath(pythonExe.Trim().Trim('"'));
            if (!File.Exists(pythonExe))
                throw new FileNotFoundException("请选择已安装的 Python 3.10–3.12 x64 的 python.exe。", pythonExe);

            progress?.Report("正在检查 Python 版本…");
            var probe = await RunProcessAsync(pythonExe,
                ["-I", "-c", "import json,sys,struct; print('RESULT_JSON='+json.dumps({'major':sys.version_info.major,'minor':sys.version_info.minor,'bits':struct.calcsize('P')*8,'implementation':sys.implementation.name}))"],
                progress, cancellationToken).ConfigureAwait(false);
            using (var version = JsonDocument.Parse(probe.ResultJson ?? "{}"))
            {
                var value = version.RootElement;
                if (!value.TryGetProperty("major", out var major) || major.GetInt32() != 3 ||
                    !value.TryGetProperty("minor", out var minor) || minor.GetInt32() is not (10 or 11 or 12) ||
                    !value.TryGetProperty("bits", out var bits) || bits.GetInt32() != 64 ||
                    !value.TryGetProperty("implementation", out var implementation) || implementation.GetString() != "cpython")
                    throw new InvalidOperationException("音频转 MIDI 需要 CPython 3.10–3.12 x64。请选择对应的 python.exe；本工具使用 ONNX，不需要 TensorFlow。");
            }

            if (IsInstalled)
            {
                progress?.Report("正在验证已安装的 ONNX 环境…");
                try
                {
                    await VerifyEnvironmentAsync(progress, cancellationToken).ConfigureAwait(false);
                    progress?.Report("音频转 MIDI 环境已就绪。");
                    return;
                }
                catch (InvalidOperationException)
                {
                    File.Delete(ReadyMarker);
                    progress?.Report("现有转换环境未通过检查，正在修复应用专用环境…");
                }
            }

            if (Directory.Exists(EnvironmentDirectory) &&
                !File.Exists(Path.Combine(EnvironmentDirectory, "pyvenv.cfg")))
                throw new InvalidOperationException($"转换环境目录已被其他文件占用，请先移动该目录：{EnvironmentDirectory}");

            progress?.Report("正在创建应用专用 Python 环境…");
            await RunProcessAsync(pythonExe, ["-I", "-m", "venv", EnvironmentDirectory],
                progress, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(EnvironmentPython))
                throw new InvalidOperationException("Python 虚拟环境创建失败，未找到 Scripts\\python.exe。");

            // Deliberately split the installation. The upstream Python 3.11
            // dependency marker would otherwise install a large TensorFlow build.
            progress?.Report("正在下载安装工具（仅写入应用环境）…");
            await RunPipAsync(["install", "--upgrade", "pip==25.0.1", "setuptools==75.8.0", "wheel==0.45.1"],
                progress, cancellationToken).ConfigureAwait(false);
            progress?.Report("正在安装 ONNX CPU 和音频依赖，首次安装需要联网…");
            await RunPipAsync(["install", "--prefer-binary", "-r", Path.Combine(_toolDirectory, "requirements.txt")],
                progress, cancellationToken).ConfigureAwait(false);
            progress?.Report("正在安装 Basic Pitch 及其内置 ONNX 模型…");
            await RunPipAsync(["install", "--no-deps", "--only-binary=:all:", "basic-pitch==0.4.0"],
                progress, cancellationToken).ConfigureAwait(false);
            await VerifyEnvironmentAsync(progress, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(ReadyMarker, EnvironmentRevision, cancellationToken).ConfigureAwait(false);
            progress?.Report("音频转 MIDI 环境已就绪，之后的转换可离线运行。");
        }
        finally { _operationLock.Release(); }
    }

    /// <summary>Returns a verified .mid path; never overwrites an existing MIDI.</summary>
    public async Task<string> TranscribeAsync(
        string audioPath, string outputDirectory, IProgress<string>? progress, CancellationToken cancellationToken,
        string quantization = "none", string preset = "balanced", string melodyMode = "highest")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (quantization is not ("none" or "1/4" or "1/8" or "1/16"))
            throw new ArgumentException("节拍量化必须为 none、1/4、1/8 或 1/16。", nameof(quantization));
        if (preset is not ("solo" or "balanced" or "ensemble"))
            throw new ArgumentException("识别预设必须为 solo、balanced 或 ensemble。", nameof(preset));
        if (melodyMode is not ("highest" or "smart" or "polyphonic"))
            throw new ArgumentException("输出旋律必须为 highest、smart 或 polyphonic。", nameof(melodyMode));
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? outputPath = null;
        try
        {
            VerifyBundledTools();
            if (!IsInstalled)
                throw new InvalidOperationException("请先选择 Python 3.10–3.12 x64 并安装音频转 MIDI 环境。");
            audioPath = Path.GetFullPath(audioPath);
            if (!File.Exists(audioPath))
                throw new FileNotFoundException("找不到所选音频文件。", audioPath);
            if (new FileInfo(audioPath).Length == 0)
                throw new InvalidOperationException("所选音频文件为空。");
            outputDirectory = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(outputDirectory);
            var sourceStem = Path.GetFileNameWithoutExtension(audioPath);
            if (sourceStem.Length > 80) sourceStem = sourceStem[..80];
            // An unpredictable suffix also prevents another conversion from
            // overwriting this job's output when two app instances are open.
            outputPath = Path.Combine(outputDirectory,
                $"{sourceStem}_转谱_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.mid");
            progress?.Report("转换在本机运行；建议使用独奏、纯伴奏或器乐录音。");
            var result = await RunProcessAsync(EnvironmentPython,
                ["-I", "-u", ScriptPath, audioPath, outputPath, "--quantize", quantization,
                    "--preset", preset, "--melody", melodyMode],
                progress, cancellationToken).ConfigureAwait(false);
            using (var document = JsonDocument.Parse(result.ResultJson ?? "{}"))
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("output", out var output))
                    throw new InvalidOperationException("转换程序未返回有效的 MIDI 输出。");
                var reportedPath = Path.GetFullPath(output.GetString() ?? "");
                if (!string.Equals(reportedPath, outputPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"转换程序返回的路径不匹配。预期：{outputPath}；实际：{reportedPath}");
                if (!root.TryGetProperty("notes", out var notes) || notes.GetInt32() <= 0)
                    throw new InvalidOperationException("转换程序未返回有效的 MIDI 音符。");
            }
            await VerifyMidiHeaderAsync(outputPath, cancellationToken).ConfigureAwait(false);
            progress?.Report($"转换完成：{Path.GetFileName(outputPath)}");
            return outputPath;
        }
        catch
        {
            // This path belongs to this invocation only, never an existing file.
            if (outputPath is not null && File.Exists(outputPath))
            {
                try { File.Delete(outputPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (outputPath is not null)
            {
                // The Python writer uses this invocation's unique stem for its
                // temporary file. A hard cancellation may leave it unfinished.
                try
                {
                    var directory = Path.GetDirectoryName(outputPath)!;
                    var pattern = $".{Path.GetFileNameWithoutExtension(outputPath)}-*.tmp.mid";
                    foreach (var temporary in Directory.EnumerateFiles(directory, pattern))
                        File.Delete(temporary);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
        finally { _operationLock.Release(); }
    }

    private void VerifyBundledTools()
    {
        if (!File.Exists(ScriptPath) || !File.Exists(TempoScriptPath) ||
            !File.Exists(Path.Combine(_toolDirectory, "requirements.txt")))
            throw new FileNotFoundException("程序未包含音频转谱脚本，请重新完整解压或构建应用。", ScriptPath);
    }

    private async Task VerifyEnvironmentAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync(EnvironmentPython, ["-I", "-u", ScriptPath, "--check"],
            progress, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(result.ResultJson ?? "{}");
        var value = document.RootElement;
        if (!value.TryGetProperty("ready", out var ready) || !ready.GetBoolean() ||
            !value.TryGetProperty("basic_pitch", out var pitch) || pitch.GetString() != "0.4.0" ||
            !value.TryGetProperty("onnxruntime", out var runtime) || runtime.GetString() != "1.19.2" ||
            !value.TryGetProperty("music_tempo", out var tempo) || tempo.GetString() != "1.0.3" ||
            !value.TryGetProperty("mini_racer", out var jsRuntime) || jsRuntime.GetString() != "0.14.1")
            throw new InvalidOperationException("音频转谱环境验证失败，请重新安装。");
    }

    private Task<ProcessResult> RunPipAsync(
        IEnumerable<string> arguments, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var command = new List<string>
        {
            "-I", "-m", "pip", "--isolated", "--disable-pip-version-check", "--no-input",
            "--timeout", "60", "--retries", "2"
        };
        command.AddRange(arguments);
        return RunProcessAsync(EnvironmentPython, command, progress, cancellationToken);
    }

    private async Task<ProcessResult> RunProcessAsync(
        string executable, IEnumerable<string> arguments, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = _dataDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONNOUSERSITE"] = "1";
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动 Python 转谱进程。");
        }
        catch (Win32Exception error)
        {
            throw new InvalidOperationException("无法启动 Python，请检查所选程序和应用专用环境是否仍存在。", error);
        }

        // Cancel may come from Window.Closed. Kill synchronously in its callback,
        // before the UI message loop and the .NET process can terminate.
        var terminationLock = new object();
        void TerminateProcessTree()
        {
            // Token callbacks and the canceled await can run concurrently. A
            // second traversal must not race the first through Python's venv launcher.
            lock (terminationLock)
            {
                if (OperatingSystem.IsWindows())
                {
                    // Windows venv launchers can forward to another interpreter;
                    // taskkill snapshots the full tree before terminating its parents.
                    try
                    {
                        if (process.HasExited) return;
                        var terminate = new ProcessStartInfo
                        {
                            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe"),
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        };
                        terminate.ArgumentList.Add("/PID");
                        terminate.ArgumentList.Add(process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        terminate.ArgumentList.Add("/T");
                        terminate.ArgumentList.Add("/F");
                        using var terminator = Process.Start(terminate);
                        if (terminator is not null)
                        {
                            if (terminator.WaitForExit(5000) && terminator.ExitCode == 0) return;
                            if (!terminator.HasExited) terminator.Kill();
                        }
                    }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception) { }
                }
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
            }
        }
        using var cancellationRegistration = cancellationToken.Register(TerminateProcessTree);

        var lines = new Queue<string>();
        var outputLock = new object();
        string? resultJson = null;
        async Task PumpAsync(StreamReader reader, bool isError)
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (!isError && line.StartsWith("RESULT_JSON=", StringComparison.Ordinal))
                {
                    resultJson = line["RESULT_JSON=".Length..];
                    continue;
                }
                if (string.IsNullOrWhiteSpace(line)) continue;
                lock (outputLock)
                {
                    lines.Enqueue(line);
                    while (lines.Count > 32) lines.Dequeue();
                }
                progress?.Report(line);
            }
        }
        var stdout = PumpAsync(process.StandardOutput, false);
        var stderr = PumpAsync(process.StandardError, true);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TerminateProcessTree();
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            progress?.Report("操作已取消。");
            throw;
        }
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var outputText = string.Join(Environment.NewLine, lines);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"音频转换环境运行失败（退出码 {process.ExitCode}）。{Environment.NewLine}{outputText}");
        return new ProcessResult(resultJson, outputText);
    }

    private static async Task VerifyMidiHeaderAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, useAsync: true);
        var header = new byte[14];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (header[0] != 'M' || header[1] != 'T' || header[2] != 'h' || header[3] != 'd' ||
            header[4] != 0 || header[5] != 0 || header[6] != 0 || header[7] != 6 ||
            (header[10] == 0 && header[11] == 0) || stream.Length <= 22)
            throw new InvalidOperationException("生成的文件不是有效的 MIDI 文件。");
    }

    private sealed record ProcessResult(string? ResultJson, string Output);
}
