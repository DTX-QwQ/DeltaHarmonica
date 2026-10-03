# 本机音频转 MIDI

应用使用 Spotify Basic Pitch 0.4.0 自带的 ONNX 模型，推理仅使用 CPU。BPM 和拍点使用 music-tempo 1.0.3 的 Beatroot 算法估算。
音频和转谱结果保存在本机。第一次点击应用中的“安装转换环境”需要网络；安装成功后转换可以离线运行。

## 安装方式

1. 安装 **CPython 3.10–3.12 的 Windows x64 版本**。在应用中选择该版本的 `python.exe`。
2. 点击“安装转换环境”。程序在应用数据目录的 `audio-python-onnx-v1` 下创建独立 venv，安装固定版本的音频依赖与 mini-racer 0.14.1，并实际加载 ONNX 模型和 JS 运行时验证。更新前已安装的环境需再次点击安装补齐依赖。
3. 选择 WAV、MP3、FLAC 或 OGG 音频并开始转换。转换成功后会获得一个不覆盖已有文件的新 `.mid` 文件，可加入播放列表。

本工具不会修改所选 Python 的现有包，也不会在全局安装包。请选择真正的 `python.exe`，不使用 `py.exe` 启动器或 Microsoft Store 占位程序。
Basic Pitch 0.4.0 官方文档列出 Python 3.10/3.11；本软件额外验证了 Python 3.12 上的 ONNX 配置：实际加载 CPU 模型，将 A4/C5 测试音频转为 MIDI 并核验两枚正确音符。本软件固定了 NumPy 1.26.4、ONNX Runtime 1.19.2 等主要依赖版本，支持这三个版本的 CPython。
若提示 `onnxruntime` 的 DLL 加载失败，请安装 [Microsoft Visual C++ x64 运行库](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist)。

Basic Pitch 0.4.0 的包声明在 Python 3.11 及以上版本自动安装 TensorFlow，而其中的 TensorFlow 版本上限不支持 Python 3.12。本工具先安装 `requirements.txt` 中的 ONNX 依赖，再用 `--no-deps` 安装 Basic Pitch 本体，避免这项额外安装及其版本冲突。ONNX 模型包含在官方 wheel 内，不需要自行下载模型。

## 直接使用脚本

以下命令中的 `python` 应替换为应用专用 venv 的 `Scripts\python.exe`。这些命令不应在共享环境中执行：

```powershell
python -m pip install -r requirements.txt
python -m pip install --no-deps --only-binary=:all: basic-pitch==0.4.0
python audio_to_midi.py --check
python audio_to_midi.py "我的 音频.wav" "输出 乐谱.mid"
python audio_to_midi.py "我的 音频.wav" "独奏 乐谱.mid" --preset solo --melody highest
python audio_to_midi.py "我的 音频.wav" "旋律 乐谱.mid" --preset balanced --melody smart --quantize 1/8
python audio_to_midi.py "我的 音频.wav" "和弦 乐谱.mid" --preset ensemble --melody polyphonic
python smoke_test.py
python smoke_test.py --matrix
python smoke_test.py --matrix --quantize 1/16
```

输出文件不得已存在。脚本最后打印 `RESULT_JSON=` 行，包含完整输出路径、音符数量、时长、`bpm`、秒单位的 `beats`、`beatOrigin`、`tempoSource`、`quantization` 和 `noteEvents`。每个音符包含 `pitch`、`velocity`、`start`、`end`、`beat`、`durationBeats` 和音轨索引 `instrument`。另外返回 `preset`、`melodyMode`、原始/清理后数量 `rawNotes`/`cleanedNotes`、清理统计 `cleanupStats`、旋律统计 `melodyStats` 和未经清理、量化的 `rawNoteEvents`；模型候选保留真实 `amplitude`（0–1）和 `onsetConfidence`，便于核对过滤原因。失败返回非零退出码。应用取消时终止 Python 及其子进程，并清理本次未完成的输出。

不安装音频依赖也能运行 CLI 参数和文件保护测试：

```powershell
python -m unittest discover -s tools/audio_to_midi/tests -v
```

Windows 上可使用 .NET 8 和任意本机 Python 验证 C# 子进程取消逻辑：

```powershell
dotnet run --project tools/audio_to_midi/tests/ServiceHarness/ServiceHarness.csproj -- "C:\完整路径\python.exe"
```

这个测试不会安装任何包，会检查取消操作是否同时终止父进程和它启动的 Python 子进程。

已有应用转换环境时，增加 `--managed-environment` 可以直接调用 C# 转谱服务，验证独奏档的最高声部、智能旋律、多声部和量化输出，并检查真实模型 A4/C5、正音长及单音边界；无需创建环境或重新安装依赖：

```powershell
dotnet run --project tools/audio_to_midi/tests/ServiceHarness/ServiceHarness.csproj -- "C:\完整路径\python.exe" --managed-environment
```

加上 `--integration` 可在 `LocalAppData\DeltaHarmonica\TestRuns` 下创建一次性环境，实际调用 C# 的安装和转谱方法，并用应用的 MIDI 读取器核验 A4/C5 音符。测试完成后移除本次环境；依赖仍不会写入全局 Python：

```powershell
dotnet run --project tools/audio_to_midi/tests/ServiceHarness/ServiceHarness.csproj -- "C:\完整路径\python.exe" --integration
```

## 识别效果和音频格式

Basic Pitch 适合清晰的单一乐器音频，也能输出多音符；完整歌曲中的人声、鼓和伴奏可能带来误识别。参考 [Delta-Force-Harmonica 音频预设设计](https://github.com/Dr-hydra/Delta-Force-Harmonica/blob/main/docs/AUDIO_PRESETS.md)，新增三档参数和清理流程：

| 预设 | 解码最短音长 | 音域 | 后续清理 | 适用 |
| --- | --- | --- | --- | --- |
| `solo` 独奏 / 单声部 | 约 58 ms，5 帧 | MIDI 36–96 | 48 ms 以下短音、弱音；同起音最多 8 个候选 | 干净独奏与快速装饰音 |
| `balanced` 标准 / 推荐（默认） | 约 139 ms，12 帧 | MIDI 45–88 | 110 ms 以下短音、弱音；同起音最多 4 个候选 | 多数录音 |
| `ensemble` 长音优先 | 约 197 ms，17 帧 | MIDI 45–88 | 190 ms 以下短音、弱音；同起音最多 4 个候选 | 长音主导、密集短伴奏的素材 |

标准与长音档关闭模型推断额外起音，独奏档保留；后续清理按真实模型振幅过滤弱音，并合并重复起点与缺少新攻击证据的同音片段。有明确新起音的同音反复保留，重叠同音在下一次起音处释放；清理与旋律选择均保留所选音符原始起点。长音档会删掉真正的快速短音，独奏档也可能保留模型识别的释放阶段短片段，应根据素材选择。`--onset-threshold`、`--frame-threshold`、`--minimum-note-length` 仍可覆盖预设；明确缩短解码最短音长时，第二道长度过滤同步下调。

`--melody` 提供三种输出方式：`highest`（默认）在 45 ms 内的和弦起音组中选最高音，并在下一次起音时截断前音；`smart` 根据时长、振幅、音程连续性与持续覆盖选择连贯声部，可跳过密集伴奏；`polyphonic` 保留清理后的多声部和弦。前两种输出适用于游戏单音口琴，量化后还会再次合并同格和弦并释放重叠，避免生成无法演奏的复音。智能选择是启发式简化，不是人声/伴奏分离，可能选错旋律。

音频先解码为 **44100 Hz、单声道 float32 PCM**，匹配 music-tempo 的默认分析步长。程序运行随包的纯 JS 库，使用 mini-racer 内嵌 V8，不调用 Node.js 或在线服务；JS 文件、版本来源和 MIT 许可证保存在 `vendor/`。

`beat=0` 对应第一个检测到的拍点（`beatOrigin` 秒）；之前的弱起音符可以具有负 `beat`。相邻拍点之间线性插值，首拍前和末拍后按估算 BPM 外推；`durationBeats` 是音符终点拍数减起点拍数，可跨越多个不等长拍间隔。估算异常、拍点不足或估算结果无效时，统一回退到 **120 BPM**、从音频 0 秒起计拍，`tempoSource="fallback"`，`beats=[]`。成功时 `tempoSource="music-tempo"`。

`--quantize` 支持 `none`（默认保留原时序）、`1/4`、`1/8`、`1/16`。分数是**拍长步长**，分别为 0.25、0.125、0.0625 拍。起点、终点就近对齐拍格，中点向后取整；终点至少晚于起点一个步长。音频 0 秒之前的格点会移到第一个可用格点，同音同格的重复音符合并，同音重叠在下一次起音处截断，保证有效的 MIDI 音符开关。

MIDI 使用 960 PPQN，初始速度为估算 BPM，并写入拍点间隔对应的速度变化；首拍前导静音保留，不量化时秒级时序在 MIDI tick 精度内保持一致。拍数是相对首拍的坐标，MIDI tick 含首拍偏移，二者通过 `tick = round(beatOrigin * bpm / 60 * 960) + round(beat * 960)` 对应。`--tempo` 已由自动估算取代。

WAV/FLAC/OGG 和常见 MP3 由 SoundFile 解码。M4A/AAC 等格式依赖额外解码器，若失败请先在外部转换为 WAV。输出中的连续弯音会移除，因为游戏按键只能发出固定半音。

来源：[Basic Pitch 官方说明](https://github.com/spotify/basic-pitch/blob/v0.4.0/README.md)、[官方依赖声明](https://github.com/spotify/basic-pitch/blob/v0.4.0/pyproject.toml)、[music-tempo](https://github.com/killercrush/music-tempo)、[mini-racer](https://pypi.org/project/mini-racer/0.14.1/)、[ONNX Runtime 1.19.2 的 Windows wheel](https://pypi.org/project/onnxruntime/1.19.2/)、[Delta-Force-Harmonica](https://github.com/Dr-hydra/Delta-Force-Harmonica) 的预设参数与主旋律选择思路。清理与旋律算法为本项目独立实现；没有复制参考仓库代码或模型资产。Basic Pitch 使用 Apache 2.0 许可证，music-tempo 使用 MIT；依赖保留各自的许可证。
