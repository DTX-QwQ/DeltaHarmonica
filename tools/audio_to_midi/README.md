# 本机音频转 MIDI

应用使用 Spotify Basic Pitch 0.4.0 自带的 ONNX 模型，推理仅使用 CPU。
音频和转谱结果保存在本机。第一次点击应用中的“安装转换环境”需要网络；安装成功后转换可以离线运行。

## 安装方式

1. 安装 **CPython 3.10–3.12 的 Windows x64 版本**。在应用中选择该版本的 `python.exe`。
2. 点击“安装转换环境”。程序在应用数据目录的 `audio-python-onnx-v1` 下创建独立 venv，安装固定版本的音频依赖，并实际加载 ONNX 模型验证。
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
python smoke_test.py
```

输出文件不得已存在。脚本最后打印 `RESULT_JSON=` 行，包含完整输出路径、音符数量和时长；失败返回非零退出码。应用取消时终止 Python 及其子进程，并清理本次未完成的输出。

不安装音频依赖也能运行 CLI 参数和文件保护测试：

```powershell
python -m unittest discover -s tools/audio_to_midi/tests -v
```

Windows 上可使用 .NET 8 和任意本机 Python 验证 C# 子进程取消逻辑：

```powershell
dotnet run --project tools/audio_to_midi/tests/ServiceHarness/ServiceHarness.csproj -- "C:\完整路径\python.exe"
```

这个测试不会安装任何包，会检查取消操作是否同时终止父进程和它启动的 Python 子进程。

加上 `--integration` 可在 `LocalAppData\DeltaHarmonica\TestRuns` 下创建一次性环境，实际调用 C# 的安装和转谱方法，并用应用的 MIDI 读取器核验 A4/C5 音符。测试完成后移除本次环境；依赖仍不会写入全局 Python：

```powershell
dotnet run --project tools/audio_to_midi/tests/ServiceHarness/ServiceHarness.csproj -- "C:\完整路径\python.exe" --integration
```

## 识别效果和音频格式

Basic Pitch 适合清晰的单一乐器音频，也能输出多音符；完整歌曲中的人声、鼓和伴奏可能带来误识别。它不会自动分离主旋律，游戏播放仍需使用应用中的声部选择/单音处理。转谱保留音符的原始秒级时间，因此 `--tempo` 仅控制 MIDI 时间基准，不改变实际演奏节奏。

WAV/FLAC/OGG 和常见 MP3 由 SoundFile 解码。M4A/AAC 等格式依赖额外解码器，若失败请先在外部转换为 WAV。输出中的连续弯音会移除，因为游戏按键只能发出固定半音。

来源：[Basic Pitch 官方说明](https://github.com/spotify/basic-pitch/blob/v0.4.0/README.md)、[官方依赖声明](https://github.com/spotify/basic-pitch/blob/v0.4.0/pyproject.toml)、[ONNX Runtime 1.19.2 的 Windows wheel](https://pypi.org/project/onnxruntime/1.19.2/)。Basic Pitch 使用 Apache 2.0 许可证；依赖保留各自的许可证。
