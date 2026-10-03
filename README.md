# 三角洲口琴 / Delta Harmonica

基于 **WinUI 3 + C# / .NET 8** 的 Windows 桌面程序：读取 MIDI，把旋律转换成 `z x c v b n m ,` 按键，并通过 Windows `SendInput` 演奏游戏内口琴。音频转 MIDI 使用本机 Basic Pitch ONNX 模型。

## 运行

从 [GitHub Releases](https://github.com/Amor-Vooc/DeltaHarmonica/releases/latest) 下载 `DeltaHarmonica-v1.0.0-win-x64.zip`，完整解压后运行文件夹中的 `DeltaHarmonica.App.exe`。Release 同时提供 SHA-256 校验文件。

构建好的程序位于 `artifacts/DeltaHarmonica/DeltaHarmonica.App.exe`。整个文件夹需一起保留，可复制到其他 **Windows 10 2004 或更高版本 / Windows 11，x64** 电脑；它包含 .NET 和 Windows App SDK 运行时。

1. 启动程序，在“我的曲库”导入 `.mid` / `.midi`，或点击“载入小星星示例”。
2. 选择需要的音轨和旋律策略，点击“预览按键”检查指法。预览显示按键与时间轴，不发送游戏输入，也不合成试听音频。
3. 打开游戏内口琴，点击“开始演奏”，在倒计时结束前切到游戏窗口。软件锁定该窗口的 HWND 和进程 ID。
4. 使用全局快捷键暂停、切歌或停止；切到其他窗口时自动暂停并释放输入。切回原游戏窗口后按暂停快捷键继续。

默认快捷键：**F8 暂停/继续，F6 上一首，F7 下一首，F9 停止**。在“设置”中可修改为 `Ctrl+Alt+P` 等组合。重复、无效或被其他程序占用的组合会显示错误，原快捷键保持有效。停止时按暂停快捷键也能开始当前曲目的倒计时。

## 音阶与 MIDI

| 按键 | z | x | c | v | b | n | m | , |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 默认音高 | C4 | D4 | E4 | F4 | G4 | A4 | B4 | C5 |
| MIDI 编号 | 60 | 62 | 64 | 65 | 67 | 69 | 71 | 72 |

鼠标变调按**按住**处理：左键低八度（−12 半音）、右键高八度（+12 半音）、中键升半音（+1）。可以在设置中调整 Z 的基准音高及三个鼠标偏移；八度命名以 MIDI 编号为准。默认可精确覆盖 MIDI 48–85。超出可映射范围的音符默认折叠八度，也可选择跳过。

- 支持标准 MIDI 格式 0/1、PPQN 时间基准、多音轨、全局速度变化、running status，以及力度 0 的 Note On。格式 2 和 SMPTE 时间基准会明确报错。
- 默认过滤第 10 通道打击乐。曲库保留原时序，支持 **0.25–3 倍速**、实时速度调整、整体移调和进度定位。
- “最高音”适合旋律，“最低音”适合低音声部；“兼容和弦”只演奏能够共享鼠标变调状态的音符。不能同时表示的和弦音会计数并提示。
- 持续音在相邻和弦片段中保持按住；同音重复时重新触发。暂停、停止、切歌、窗口失焦、退出和异常时尝试释放所有本软件按下的按键。
- 游戏按键只支持离散音高。MIDI 的力度、音色、踏板、表情和弯音不会在游戏中复现。

实际游戏音高可能不同，建议先导入已知旋律校准 Z 音高。游戏是否接受标准模拟输入需要实测；本程序没有驱动注入或绕过游戏保护的功能。若 Windows 拒绝输入，界面会显示权限或输入失败信息。

## 音频转 MIDI

在“音频转 MIDI”页面选择 **64 位 CPython 3.10–3.12** 的 `python.exe`，点击“安装本地转录环境”。安装需要网络，只在 `%LOCALAPPDATA%/DeltaHarmonica/audio-python-onnx-v1/` 中创建独立环境，不修改原 Python 的包。随后选取音频和输出目录，点击“开始转录并加入曲库”；运行中的任务可取消。

转录在 CPU 本地运行。WAV、FLAC、OGG 和常见 MP3 可以直接使用；M4A/AAC 若无法解码，请先转成 WAV。独奏、人声或干净乐器录音通常更容易识别；复杂混音可能需要整理旋律。输出以独立名称保存，不覆盖已有 MIDI，成功后自动加入曲库。

使用 Spotify Basic Pitch 0.4.0 的随包 ONNX 模型及 ONNX Runtime 1.19.2。官方 Python 支持范围和本项目实测 Python 3.12 ONNX 配置的区别、依赖版本及独立脚本用法见 [转录工具说明](tools/audio_to_midi/README.md)。

## 构建与验证

需要 Windows x64、.NET 8 或更高版本 SDK。当前工程已在 .NET SDK 10 上通过 `dotnet` 编译；Visual Studio 可直接打开 `DeltaHarmonica.sln`。

```powershell
.\build.ps1
```

脚本先运行 MIDI 和播放引擎测试，再发布带运行时的版本到 `artifacts/DeltaHarmonica/`。也可单独执行：

```powershell
dotnet build src/DeltaHarmonica.App/DeltaHarmonica.App.csproj -c Release
dotnet run --project tests/DeltaHarmonica.Core.Tests -c Release
dotnet run --project tests/DeltaHarmonica.Playback.Tests -c Release
python -m unittest discover -s tools/audio_to_midi/tests -v
```

测试的输入服务使用记录替身，不向游戏或其他软件发送演奏按键。真实音频推理测试可在已安装的转录环境中执行 `tools/audio_to_midi/smoke_test.py`，检查 A4 / C5 测试录音生成的 MIDI 音高。

## 源码

| 路径 | 职责 |
| --- | --- |
| `src/DeltaHarmonica.App/MainWindow.xaml` | 原生 WinUI 界面 |
| `src/DeltaHarmonica.App/MainWindow.xaml.cs` | 曲库、倒计时、设置和转录交互 |
| `src/DeltaHarmonica.Core/` | MIDI 解析、音高映射、旋律和和弦规划 |
| `src/DeltaHarmonica.App/Services/PlaybackEngine.cs` | 单调时钟、倍速、暂停、进度和窗口检查 |
| `src/DeltaHarmonica.App/Services/NativeInputService.cs` | 扫描码按键、鼠标变调和输入释放 |
| `src/DeltaHarmonica.App/Services/GlobalHotkeyService.cs` | 原生全局快捷键注册与冲突处理 |
| `src/DeltaHarmonica.App/Services/AudioTranscriptionService.cs` | 本地环境安装、日志、取消和结果检查 |
| `tools/audio_to_midi/` | Basic Pitch ONNX 转录工具 |

曲库路径、音高校准、倍速和快捷键存储在 `%LOCALAPPDATA%/DeltaHarmonica/settings.json`。若程序发生未处理异常，日志记录在同目录 `crash.log`。

实现参考：[Microsoft WinUI 无 MSIX 部署](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app)、[SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)、[RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)、[Spotify Basic Pitch](https://github.com/spotify/basic-pitch)。
