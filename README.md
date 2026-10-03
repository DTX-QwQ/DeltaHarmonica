# 三角洲口琴 / Delta Harmonica

<img src="src/DeltaHarmonica.App/Assets/logo.png" alt="三角洲口琴 Logo" width="144" />

基于 **WinUI 3 + C# / .NET 8** 的 Windows 桌面程序：读取 MIDI，把旋律转换成 `z x c v b n m ,` 按键，并通过 Windows `SendInput` 演奏游戏内口琴。音频转 MIDI 使用本机 Basic Pitch ONNX 模型。

**开源免费项目，严禁倒卖盈利。** 源码地址：[DTX-QwQ/DeltaHarmonica](https://github.com/DTX-QwQ/DeltaHarmonica)。请从项目仓库和官方 Releases 获取软件，勿购买付费转售版本。

## 风险提示与免责声明

- 使用前请确认游戏及平台的相关规则。模拟键盘、鼠标输入或自动演奏可能被游戏判定为违规，存在账号限制、处罚或封禁风险；管理员权限不保证游戏接受模拟输入。
- 请仅导入、转录和分享自己拥有使用权或已获授权的 MIDI、音频与演奏内容，遵守相关版权要求。
- 使用者应自行评估并承担使用风险。作者不保证游戏兼容性、演奏效果或音频转录准确性，也不对因使用本项目产生的账号处罚、数据损失或其他损失作出赔偿承诺。
- 本项目与游戏开发商、发行商无隶属或官方合作关系。项目免费提供，严禁付费倒卖、打包转售或以本项目牟利。

## 运行

从 [GitHub Releases](https://github.com/DTX-QwQ/DeltaHarmonica/releases/latest) 下载 `DeltaHarmonica-v1.1.0-win-x64.zip`，完整解压后运行文件夹中的 `DeltaHarmonica.App.exe`。Release 同时提供 SHA-256 校验文件。

程序默认请求以管理员身份运行，启动时 Windows 会显示 UAC 权限确认；允许后进入软件。若当前账号没有管理员权限，需要提供管理员凭据。管理员权限用于减少 Windows 输入权限等级不同导致的失败，不能绕过游戏保护或保证模拟输入有效。

构建好的程序位于 `artifacts/DeltaHarmonica/DeltaHarmonica.App.exe`。整个文件夹需一起保留，可复制到其他 **Windows 10 2004 或更高版本 / Windows 11，x64** 电脑；它包含 .NET 和 Windows App SDK 运行时。

1. 启动程序，在“我的曲库”导入 `.mid` / `.midi`，或点击“载入小星星示例”。
2. 选择需要的音轨和旋律策略，点击“预览按键”检查指法并试听。预览显示按键与时间轴，同步播放软件内置的口琴合成音效，不向游戏发送输入，也无需打开游戏或下载音色。
3. 打开游戏内口琴，在游戏窗口按 **F5**，或点击软件的“开始演奏”并在倒计时结束前切到游戏窗口。软件锁定该窗口的 HWND 和进程 ID。
4. 使用全局快捷键暂停、切歌或停止；切到其他窗口时自动暂停并释放输入。切回原游戏窗口后按暂停快捷键继续。

默认快捷键：**F5 开始演奏，F8 暂停/继续，F6 上一首，F7 下一首，F9 停止**。在“设置”中可修改为 `Ctrl+Alt+P` 等组合并自动保存。重复、无效或被其他程序占用的组合会显示错误，原快捷键保持有效。旧配置会保留原有快捷键并补上 F5；若 F5 已分配给其他操作，则为开始演奏选用空闲的 F10–F24。

开始快捷键在松开普通键和修饰键后进入倒计时，从所选区间起点演奏；倒计时或实际演奏中重复按开始键不会重启曲目。预览中按开始键会停止试听并进入演奏倒计时，暂停时按开始键会从区间起点重新演奏。停止快捷键或停止按钮可取消等待和倒计时。停止时按暂停快捷键也能开始当前曲目的倒计时。

## 自定义演奏区间

选择曲目后，直接拖动主进度条上的开始、结束手柄设置要演奏的片段；高亮部分就是所选演奏区间，中间的播放指针用于进度定位。位置按 MIDI 原曲的绝对时间显示；改变倍速只改变实际演奏速度，不改变所选时间点。开始位置必须早于结束位置。

- 点击“开始演奏”或“预览按键”时，从所选开始位置播放，到结束位置自动结束。预览同样遵循所选区间。
- 演奏中、预览中或倒计时期间不能修改区间；暂停、停止或尚未播放时可调整。点击“恢复整首”可恢复从曲目开头到结尾。
- 停止后，进度回到所选开始位置。区间内的进度定位不会超出开始、结束位置。
- 切换曲目后恢复完整曲目区间；开启“播完自动下一首”时，当前区间结束后按下一首的完整曲目播放。

预览音效使用当前指法映射后的音高，遵循音高校准、移调、八度折叠和和弦策略。正常换音与休止使用平滑起音、淡出和线性混音；暂停、定位及停止会立即清空音频缓冲。倍速改变音符时长而不改变音高；暂停、停止、定位、切歌和播放结束会停止原音符，继续后从当前位置恢复。内置音色用于试听旋律，实际游戏音色可能有所不同；“开始演奏”只发送游戏按键，不播放软件音效。

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

转录在 CPU 本地运行。WAV、FLAC、OGG 和常见 MP3 可以直接使用；M4A/AAC 若无法解码，请先转成 WAV。建议使用独奏、纯伴奏或干净器乐录音；完整人声混音的主旋律提取效果有限。输出以独立名称保存，不覆盖已有 MIDI，成功后自动加入曲库。

参考 [Delta-Force-Harmonica 的音频处理流程](https://github.com/Dr-hydra/Delta-Force-Harmonica)，增加了三档识别预设与音符清理：**独奏 / 单声部**保留约 60 ms 以上的音符，**标准 / 推荐**保留约 140 ms 以上的音符，**长音优先**保留约 200 ms 以上的音符。长音优先适合密集伴奏，但会删去真正的短音。识别后过滤过低或过高的音符、弱音与碎音，合并同音碎片，限制密集和弦；任务日志显示原始候选、清理结果和输出音符数量，便于调整预设。

“输出旋律”默认选择**原版**：保留清理后的起音，和弦取最高音，并在下一次起音处截断重叠音。**精简**利用音高连续性、力度和时值跟随一条旋律，适合伴奏较多的器乐音频。**多音**保留清理后的和弦，便于继续编辑 MIDI。选择的预设、输出旋律和量化方式自动保存。

音频的 BPM 与拍点由 [music-tempo 1.0.3](https://github.com/killercrush/music-tempo) 的 Beatroot 算法估算，估算失败时使用 **120 BPM**。在“节拍量化”中可选择不量化（默认）、**1/4、1/8 或 1/16 拍**；这些分别为 0.25、0.125、0.0625 拍的步长。量化对齐音符起点和终点，短音符至少保留一个步长。MIDI 写入拍点之间的速度变化，不量化时保留原音频时序与前导静音。

music-tempo 的 JS 文件和 MIT 许可证随程序打包，通过应用专用 Python 环境中的 mini-racer / V8 执行，无需安装 Node.js。旧版转录环境在更新后需再次点击“安装本地转录环境”，以补齐这项依赖。

“任务日志”在新增输出后自动滚动到底部，便于持续查看安装和转录进度。

使用 Spotify Basic Pitch 0.4.0 的随包 ONNX 模型及 ONNX Runtime 1.19.2。官方 Python 支持范围和本项目实测 Python 3.12 ONNX 配置的区别、依赖版本及独立脚本用法见 [转录工具说明](tools/audio_to_midi/README.md)。

## 构建与验证

需要 Windows x64、.NET 8 或更高版本 SDK。当前工程已在 .NET SDK 10 上通过 `dotnet` 编译；Visual Studio 可直接打开 `DeltaHarmonica.sln`。

```powershell
.\build.ps1
```

脚本先运行 MIDI、播放引擎和内置音效测试，再发布带运行时的版本到 `artifacts/DeltaHarmonica/`。也可单独执行：

```powershell
dotnet build src/DeltaHarmonica.App/DeltaHarmonica.App.csproj -c Release
dotnet run --project tests/DeltaHarmonica.Core.Tests -c Release
dotnet run --project tests/DeltaHarmonica.Playback.Tests -c Release
dotnet run --project tests/DeltaHarmonica.Audio.Tests -c Release
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
| `src/DeltaHarmonica.App/Services/PreviewAudioService.cs` | 内置口琴合成音色与预览音频输出 |
| `src/DeltaHarmonica.App/Services/NativeInputService.cs` | 扫描码按键、鼠标变调和输入释放 |
| `src/DeltaHarmonica.App/Services/GlobalHotkeyService.cs` | 原生全局快捷键注册与冲突处理 |
| `src/DeltaHarmonica.App/Services/AudioTranscriptionService.cs` | 本地环境安装、日志、取消和结果检查 |
| `tools/audio_to_midi/` | Basic Pitch ONNX 转录工具 |

曲库路径、音高校准、倍速和快捷键存储在 `%LOCALAPPDATA%/DeltaHarmonica/settings.json`。若程序发生未处理异常，日志记录在同目录 `crash.log`。

实现参考：[Microsoft WinUI 无 MSIX 部署](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app)、[SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)、[RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)、[Spotify Basic Pitch](https://github.com/spotify/basic-pitch)。
