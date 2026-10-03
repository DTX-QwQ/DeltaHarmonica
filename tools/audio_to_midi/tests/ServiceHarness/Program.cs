using System.Diagnostics;
using System.Reflection;
using System.Text;
using DeltaHarmonica.App.Services;
using DeltaHarmonica.Core;

// Default tests install nothing. Managed mode reuses the application's environment read-only;
// --integration is the explicit opt-in that creates and installs a disposable local venv.
if (args.Length is < 1 or > 2 || !Path.IsPathFullyQualified(args[0]) || !File.Exists(args[0]) ||
    (args.Length == 2 && args[1] is not ("--integration" or "--managed-environment")))
    throw new ArgumentException("Pass an absolute Python executable path and optional --integration or --managed-environment.");
var integration = args.Length == 2 && args[1] == "--integration";
var managedEnvironment = args.Length == 2 && args[1] == "--managed-environment";
var applicationData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeltaHarmonica");
var testRoot = Path.GetFullPath(integration || managedEnvironment
    ? Path.Combine(applicationData, "TestRuns")
    : Path.GetTempPath());
var testFolder = "DeltaHarmonica-audio-tests-" + Guid.NewGuid().ToString("N");
var testDirectory = Path.GetFullPath(Path.Combine(testRoot, testFolder));
Directory.CreateDirectory(testDirectory);
Exception? testFailure = null;
try
{
    var service = new AudioTranscriptionService(testDirectory);
    if (service.IsInstalled) throw new Exception("A fresh environment must not be installed.");
    foreach (var quantization in new[] { "quarter", "1/2", "1/32", "1/4 --inject", "" })
    {
        try
        {
            await service.TranscribeAsync("unused.wav", "unused-output", null, CancellationToken.None, quantization);
            throw new Exception("Invalid quantization unexpectedly succeeded: " + quantization);
        }
        catch (ArgumentException error) when (error.ParamName == "quantization") { }
    }
    Console.WriteLine("PASS: invalid quantization is rejected before environment access or process launch.");
    foreach (var preset in new[] { "", "standard", "solo --inject" })
    {
        try
        {
            await service.TranscribeAsync("unused.wav", "unused-output", null, CancellationToken.None, preset: preset);
            throw new Exception("Invalid preset unexpectedly succeeded: " + preset);
        }
        catch (ArgumentException error) when (error.ParamName == "preset") { }
    }
    foreach (var melody in new[] { "", "raw", "smart --inject" })
    {
        try
        {
            await service.TranscribeAsync("unused.wav", "unused-output", null, CancellationToken.None, melodyMode: melody);
            throw new Exception("Invalid melody unexpectedly succeeded: " + melody);
        }
        catch (ArgumentException error) when (error.ParamName == "melodyMode") { }
    }
    Console.WriteLine("PASS: invalid preset and melody mode are rejected before process launch.");
    var method = typeof(AudioTranscriptionService).GetMethod("RunProcessAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var childStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    var progress = new ImmediateProgress(line =>
    {
        if (line.StartsWith("CHILD=")) childStarted.TrySetResult(int.Parse(line[6..]));
    });
    using var cancellation = new CancellationTokenSource();
    var pythonArguments = new[]
    {
        "-I", "-u", "-c",
        "import subprocess,sys,time; p=subprocess.Popen([sys.executable,'-I','-c','import time;time.sleep(120)']); print('CHILD='+str(p.pid),flush=True);time.sleep(120)"
    };
    var operation = (Task)method.Invoke(service, new object?[] { args[0], pythonArguments, progress, cancellation.Token })!;
    var childPid = await childStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
    cancellation.Cancel();
    try
    {
        await operation.WaitAsync(TimeSpan.FromSeconds(15));
        throw new Exception("Canceled process operation unexpectedly succeeded.");
    }
    catch (OperationCanceledException) { }
    try
    {
        using var child = Process.GetProcessById(childPid);
        if (!child.HasExited) throw new Exception("Cancellation left the Python child process running.");
    }
    catch (ArgumentException) { /* Process was removed from OS process table. */ }
    Console.WriteLine("PASS: canceled audio process and its Python child terminated.");
    if (integration || managedEnvironment)
    {
        var conversionLog = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var log = new ImmediateProgress(line =>
        {
            conversionLog.Enqueue(line);
            if (line.StartsWith("正在") || line.StartsWith("音频转") || line.StartsWith("识别到") || line.StartsWith("转换完成"))
                Console.WriteLine(line);
        });
        if (integration)
        {
            await service.EnsureEnvironmentAsync(args[0], log, CancellationToken.None);
            if (!service.IsInstalled) throw new Exception("Successful installation did not mark the environment ready.");
        }
        else
        {
            service = new AudioTranscriptionService(applicationData);
            if (!service.IsInstalled)
                throw new Exception("Managed conversion environment is not ready; managed tests never install or repair it.");
        }
        var audioPath = Path.Combine(testDirectory, "真实 服务测试.wav");
        WriteTestAudio(audioPath);
        var outputDirectory = Path.GetFullPath(Path.Combine(testDirectory, "转谱 结果"));
        Directory.CreateDirectory(outputDirectory);
        var existingPath = Path.Combine(outputDirectory, "已有 乐谱.mid");
        var original = Encoding.UTF8.GetBytes("existing MIDI sentinel");
        File.WriteAllBytes(existingPath, original);
        var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (melody, quantization) in new[]
        {
            ("highest", "none"), ("smart", "none"), ("polyphonic", "none"), ("smart", "1/16")
        })
        {
            conversionLog.Clear();
            var output = await service.TranscribeAsync(audioPath, outputDirectory, log, CancellationToken.None,
                quantization: quantization, preset: "solo", melodyMode: melody);
            if (!conversionLog.Any(line => line.Contains("独奏 / 单声部", StringComparison.Ordinal)) ||
                !conversionLog.Any(line => line.Contains($"旋律：{melody}，量化：{quantization}", StringComparison.Ordinal)))
                throw new Exception("Python did not confirm the selected preset, melody, and quantization options.");
            if (!Path.IsPathFullyQualified(output) || !File.Exists(output) ||
                !string.Equals(Path.GetDirectoryName(output), outputDirectory, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(output).StartsWith("真实 服务测试_转谱_", StringComparison.Ordinal) ||
                Path.GetExtension(output) != ".mid" || !outputs.Add(output))
                throw new Exception("Conversion did not return a new MIDI in its selected output directory.");
            var song = MidiReader.Read(output);
            var notes = song.Notes.OrderBy(note => note.Start).ThenBy(note => note.Pitch).ToArray();
            var pitches = notes.Select(note => note.Pitch).Distinct().Order().ToArray();
            // Solo preserves confident repeats, including a possible split near
            // the synthesized release. Validate pitch/timing coverage instead of
            // requiring a decoder-specific count of exactly two segments.
            if (!pitches.SequenceEqual(new[] { 69, 72 }) || notes.Length < 2 ||
                notes.Any(note => note.Start < TimeSpan.Zero || note.Duration <= TimeSpan.Zero ||
                    (note.Pitch == 69 && (note.Start.TotalSeconds > 1.1 || note.End.TotalSeconds > 1.2)) ||
                    (note.Pitch == 72 && (note.Start.TotalSeconds < 1.2 || note.End.TotalSeconds > 2.5))) ||
                notes.Where(note => note.Pitch == 69).Sum(note => note.Duration.TotalSeconds) < .7 ||
                notes.Where(note => note.Pitch == 72).Sum(note => note.Duration.TotalSeconds) < .7)
                throw new Exception($"Expected valid A4/C5 coverage for {melody}/{quantization}, received [{string.Join(',', pitches)}], count={notes.Length}, timings=[{string.Join(';', notes.Select(note => $"{note.Pitch}:{note.Start.TotalSeconds:F4}+{note.Duration.TotalSeconds:F4}"))}].");
            if (melody != "polyphonic" && notes.Zip(notes.Skip(1)).Any(pair => pair.First.End > pair.Second.Start))
                throw new Exception($"Monophonic {melody} output contains overlapping notes.");
            if (quantization != "none" && notes.Any(note => note.DurationTicks < song.TicksPerQuarterNote / 16))
                throw new Exception("Quantized output contains a note shorter than one grid step.");
            Console.WriteLine($"PASS: C# TranscribeAsync → app MIDI parser; preset=solo, melody={melody}, quantization={quantization}, notes={notes.Length}, pitches=[{string.Join(',', pitches)}].");
        }
        if (!File.ReadAllBytes(existingPath).SequenceEqual(original) || !File.Exists(audioPath))
            throw new Exception("Conversion modified an existing file or its source audio.");
        if (Directory.EnumerateFiles(outputDirectory, "*.tmp.mid").Any())
            throw new Exception("Successful conversion left a temporary MIDI file.");
        Console.WriteLine("PASS: repeated conversions use distinct selected-directory outputs and preserve existing files.");
    }
}
catch (Exception error)
{
    testFailure = error;
    throw;
}
finally
{
    // Resolve and validate the owned path before recursively deleting a venv.
    var resolved = Path.GetFullPath(testDirectory);
    var rootPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(testRoot)) + Path.DirectorySeparatorChar;
    if (!resolved.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(resolved) != testFolder)
        throw new InvalidOperationException("Unsafe test cleanup target: " + resolved);
    try { Directory.Delete(resolved, recursive: integration || managedEnvironment); }
    catch (IOException cleanupError) when (testFailure is not null)
    {
        // Preserve the actual test failure if a failed process test still owns
        // the working directory; cleanup must not replace its diagnostic.
        Console.Error.WriteLine($"Test cleanup also failed: {cleanupError.Message}");
    }
}

static void WriteTestAudio(string path)
{
    const int rate = 22050;
    var samples = new List<short>();
    foreach (var frequency in new[] { 440d, 523.251d })
    {
        for (var index = 0; index < rate; index++)
        {
            var time = (double)index / rate;
            var envelope = Math.Min(1, Math.Min(time / 0.03, (1 - time) / 0.06));
            var tone = Math.Sin(2 * Math.PI * frequency * time) + 0.25 * Math.Sin(4 * Math.PI * frequency * time);
            samples.Add((short)(15000 * envelope * tone));
        }
        samples.AddRange(Enumerable.Repeat((short)0, rate / 3));
    }
    using var writer = new BinaryWriter(File.Create(path), Encoding.ASCII);
    writer.Write(Encoding.ASCII.GetBytes("RIFF"));
    writer.Write(36 + samples.Count * 2);
    writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
    writer.Write(16);
    writer.Write((short)1);
    writer.Write((short)1);
    writer.Write(rate);
    writer.Write(rate * 2);
    writer.Write((short)2);
    writer.Write((short)16);
    writer.Write(Encoding.ASCII.GetBytes("data"));
    writer.Write(samples.Count * 2);
    foreach (var sample in samples) writer.Write(sample);
}

sealed class ImmediateProgress(Action<string> action) : IProgress<string>
{
    public void Report(string value) => action(value);
}
