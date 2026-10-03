using System.Diagnostics;
using System.Reflection;
using System.Text;
using DeltaHarmonica.App.Services;
using DeltaHarmonica.Core;

// Default tests install nothing. --integration opts into a temporary local venv.
if (args.Length is < 1 or > 2 || !File.Exists(args[0]) || (args.Length == 2 && args[1] != "--integration"))
    throw new ArgumentException("Pass an absolute Python executable path and optional --integration.");
var integration = args.Length == 2;
var testRoot = Path.GetFullPath(integration
    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeltaHarmonica", "TestRuns")
    : Path.GetTempPath());
var testFolder = "DeltaHarmonica-audio-tests-" + Guid.NewGuid().ToString("N");
var testDirectory = Path.GetFullPath(Path.Combine(testRoot, testFolder));
Directory.CreateDirectory(testDirectory);
try
{
    var service = new AudioTranscriptionService(testDirectory);
    if (service.IsInstalled) throw new Exception("A fresh environment must not be installed.");
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
    if (integration)
    {
        var log = new ImmediateProgress(line =>
        {
            if (line.StartsWith("正在") || line.StartsWith("音频转") || line.StartsWith("识别到") || line.StartsWith("转换完成"))
                Console.WriteLine(line);
        });
        await service.EnsureEnvironmentAsync(args[0], log, CancellationToken.None);
        if (!service.IsInstalled) throw new Exception("Successful installation did not mark the environment ready.");
        var audioPath = Path.Combine(testDirectory, "真实 服务测试.wav");
        WriteTestAudio(audioPath);
        var output = await service.TranscribeAsync(audioPath, Path.Combine(testDirectory, "转谱 结果"), log, CancellationToken.None);
        var song = MidiReader.Read(output);
        var pitches = song.Notes.Select(note => note.Pitch).Distinct().Order().ToArray();
        if (!pitches.Contains(69) || !pitches.Contains(72) || song.Notes.Count != 2)
            throw new Exception("Expected A4/C5, received " + string.Join(",", pitches));
        Console.WriteLine($"PASS: C# EnsureEnvironmentAsync + TranscribeAsync → app MIDI parser; notes={song.Notes.Count}, pitches=[{string.Join(',', pitches)}].");
    }
}
finally
{
    // Resolve and validate the owned path before recursively deleting a venv.
    var resolved = Path.GetFullPath(testDirectory);
    var rootPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(testRoot)) + Path.DirectorySeparatorChar;
    if (!resolved.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(resolved) != testFolder)
        throw new InvalidOperationException("Unsafe test cleanup target: " + resolved);
    Directory.Delete(resolved, recursive: integration);
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
