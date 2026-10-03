using System.Text;

namespace DeltaHarmonica.Core;

/// <summary>Reads Standard MIDI Files (format 0 and 1, PPQN time division).</summary>
public static class MidiReader
{
    private const int DefaultTempo = 500_000;

    public static MidiSong Read(string path, MidiReadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        return Read(stream, options, Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>The supplied stream remains open.</summary>
    public static MidiSong Read(Stream stream, MidiReadOptions? options = null, string name = "MIDI")
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new MidiReadOptions();
        if (options.MaximumFileBytes < 14 || options.MaximumEvents < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "MIDI read limits must be positive.");

        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (memory.Length + read > options.MaximumFileBytes)
                throw new MidiFormatException($"MIDI 文件超过 {options.MaximumFileBytes / 1024 / 1024} MB 读取限制。");
            memory.Write(buffer, 0, read);
        }

        var cursor = new Cursor(memory.ToArray());
        if (cursor.ReadTag() != "MThd")
            throw new MidiFormatException("文件不是标准 MIDI 文件：缺少 MThd 文件头。");
        var headerLength = cursor.ReadLength();
        if (headerLength < 6)
            throw new MidiFormatException("MIDI 文件头长度不足 6 字节。");
        cursor.Require(headerLength);
        var format = cursor.ReadUInt16();
        var trackCount = cursor.ReadUInt16();
        var division = cursor.ReadUInt16();
        if (format == 2)
            throw new MidiFormatException("暂不支持 MIDI 格式 2，请先转换为格式 0 或 1。");
        if (format > 2)
            throw new MidiFormatException($"未知 MIDI 格式 {format}。");
        if (trackCount == 0 || (format == 0 && trackCount != 1))
            throw new MidiFormatException("MIDI 音轨数量与文件格式不匹配。");
        if ((division & 0x8000) != 0)
            throw new MidiFormatException("暂不支持 SMPTE 时间制 MIDI，请先转换为 PPQN 时间制。");
        if (division == 0)
            throw new MidiFormatException("MIDI 每拍 tick 数不能为 0。");
        cursor.Skip(headerLength - 6);

        var rawNotes = new List<RawNote>();
        var rawTracks = new List<RawTrack>();
        var tempos = new List<RawTempo>();
        var warnings = new List<string>();
        var eventCount = 0;
        var tempoOrder = 0;
        while (rawTracks.Count < trackCount)
        {
            if (cursor.Remaining < 8)
                throw new MidiFormatException($"文件被截断：声明 {trackCount} 条音轨，仅读取 {rawTracks.Count} 条。");
            var tag = cursor.ReadTag();
            var length = cursor.ReadLength();
            var bytes = cursor.ReadBytes(length);
            if (tag != "MTrk")
            {
                warnings.Add($"跳过未知 MIDI 数据块 {tag}。");
                continue;
            }
            rawTracks.Add(ReadTrack(bytes, rawTracks.Count, options, rawNotes, tempos, warnings,
                ref eventCount, ref tempoOrder));
        }
        if (cursor.Remaining > 0)
            warnings.Add("已忽略声明音轨之后的附加数据。");

        var tempoMap = new TempoMap(division, tempos);
        var notes = rawNotes.Select(n => new MidiNote(n.Track, n.Channel, n.Pitch, n.Velocity,
                tempoMap.At(n.StartTick), tempoMap.At(n.EndTick) - tempoMap.At(n.StartTick),
                n.StartTick, n.EndTick - n.StartTick))
            .OrderBy(n => n.Start).ThenBy(n => n.TrackIndex).ThenBy(n => n.Pitch).ToArray();
        var noteCounts = notes.GroupBy(n => n.TrackIndex).ToDictionary(g => g.Key, g => g.Count());
        var tracks = rawTracks.Select(t => new MidiTrackInfo(t.Index, t.Name,
            noteCounts.GetValueOrDefault(t.Index), t.Channels.Order().ToArray(),
            t.Programs.Order().ToArray(), tempoMap.At(t.EndTick))).ToArray();
        var duration = tempoMap.At(rawTracks.Max(t => t.EndTick));
        return new MidiSong
        {
            Name = name, Format = format, TicksPerQuarterNote = division, Duration = duration,
            Notes = notes, Tracks = tracks, TempoChanges = tempoMap.Changes, Warnings = warnings
        };
    }

    private static RawTrack ReadTrack(byte[] bytes, int index, MidiReadOptions options,
        List<RawNote> notes, List<RawTempo> tempos, List<string> warnings,
        ref int eventCount, ref int tempoOrder)
    {
        var cursor = new Cursor(bytes);
        var track = new RawTrack(index);
        var active = new Dictionary<(int Channel, int Pitch), Queue<NoteStart>>();
        long tick = 0;
        byte runningStatus = 0;
        var unmatchedOffs = 0;
        var missingOffs = 0;
        var zeroLengthNotes = 0;
        var foundEnd = false;

        void Finish(int channel, int pitch)
        {
            if (!active.TryGetValue((channel, pitch), out var queue) || queue.Count == 0)
            {
                unmatchedOffs++;
                return;
            }
            var start = queue.Dequeue();
            if (tick > start.Tick)
                notes.Add(new RawNote(index, channel, pitch, start.Velocity, start.Tick, tick));
            else
                zeroLengthNotes++;
        }

        while (cursor.Remaining > 0)
        {
            if (++eventCount > options.MaximumEvents)
                throw new MidiFormatException($"MIDI 事件数量超过 {options.MaximumEvents} 读取限制。");
            tick = checked(tick + cursor.ReadVlq());
            var first = cursor.ReadByte();
            byte status;
            byte? firstData = null;
            if ((first & 0x80) != 0)
            {
                status = first;
                runningStatus = status < 0xf0 ? status : (byte)0;
            }
            else
            {
                if (runningStatus == 0)
                    throw new MidiFormatException($"音轨 {index + 1} 包含没有状态字节的 MIDI 数据。");
                status = runningStatus;
                firstData = first;
            }

            if (status == 0xff)
            {
                var type = cursor.ReadByte();
                var length = cursor.ReadVlq();
                var data = cursor.ReadBytes(length);
                switch (type)
                {
                    case 0x03 when string.IsNullOrEmpty(track.Name):
                        track.Name = Encoding.UTF8.GetString(data).TrimEnd('\0');
                        break;
                    case 0x51:
                        if (data.Length != 3)
                            throw new MidiFormatException("MIDI 速度事件必须为 3 字节。");
                        var tempo = (data[0] << 16) | (data[1] << 8) | data[2];
                        if (tempo == 0)
                            throw new MidiFormatException("MIDI 速度事件不能为 0。");
                        tempos.Add(new RawTempo(tick, tempo, tempoOrder++));
                        break;
                    case 0x2f:
                        if (data.Length != 0)
                            throw new MidiFormatException("MIDI 音轨结束事件必须为空。");
                        foundEnd = true;
                        if (cursor.Remaining != 0)
                            warnings.Add($"音轨 {index + 1} 结束事件之后的数据已忽略。");
                        break;
                }
                if (foundEnd) break;
                continue;
            }
            if (status is 0xf0 or 0xf7)
            {
                cursor.Skip(cursor.ReadVlq());
                continue;
            }
            if (status >= 0xf0)
                throw new MidiFormatException($"音轨 {index + 1} 包含标准 MIDI 文件不支持的状态 0x{status:X2}。");

            var kind = status & 0xf0;
            var channel = status & 0x0f;
            var data1 = firstData ?? cursor.ReadDataByte();
            var data2 = kind is 0xc0 or 0xd0 ? (byte)0 : cursor.ReadDataByte();
            track.Channels.Add(channel);
            if (kind == 0xc0) track.Programs.Add(data1);
            if (options.IgnorePercussion && channel == 9) continue;
            if (kind == 0x90 && data2 != 0)
            {
                if (!active.TryGetValue((channel, data1), out var queue))
                    active[(channel, data1)] = queue = new Queue<NoteStart>();
                queue.Enqueue(new NoteStart(tick, data2));
            }
            else if (kind == 0x80 || (kind == 0x90 && data2 == 0))
            {
                Finish(channel, data1);
            }
        }

        foreach (var entry in active)
        {
            while (entry.Value.Count > 0)
            {
                missingOffs++;
                Finish(entry.Key.Channel, entry.Key.Pitch);
            }
        }
        if (!foundEnd) warnings.Add($"音轨 {index + 1} 缺少结束事件，已使用数据块结束位置。");
        if (missingOffs > 0) warnings.Add($"音轨 {index + 1} 的 {missingOffs} 个音符缺少释放事件，已在音轨结束时释放。");
        if (unmatchedOffs > 0) warnings.Add($"音轨 {index + 1} 有 {unmatchedOffs} 个没有对应按下事件的释放事件。");
        if (zeroLengthNotes > 0) warnings.Add($"音轨 {index + 1} 跳过 {zeroLengthNotes} 个零时长音符。");
        if (string.IsNullOrWhiteSpace(track.Name)) track.Name = $"音轨 {index + 1}";
        track.EndTick = tick;
        return track;
    }

    private sealed class Cursor(byte[] bytes)
    {
        private int position;
        public int Remaining => bytes.Length - position;

        public void Require(int length)
        {
            if (length < 0 || length > Remaining)
                throw new MidiFormatException("MIDI 文件被截断，事件或数据块长度超过可用字节。");
        }
        public byte ReadByte() { Require(1); return bytes[position++]; }
        public byte ReadDataByte()
        {
            var result = ReadByte();
            if (result > 127) throw new MidiFormatException("MIDI 事件的数据字节必须小于 128。");
            return result;
        }
        public int ReadUInt16() => (ReadByte() << 8) | ReadByte();
        public int ReadLength()
        {
            uint length = ((uint)ReadByte() << 24) | ((uint)ReadByte() << 16) | ((uint)ReadByte() << 8) | ReadByte();
            if (length > int.MaxValue) throw new MidiFormatException("MIDI 数据块长度超过支持范围。");
            return (int)length;
        }
        public int ReadVlq()
        {
            var value = 0;
            for (var i = 0; i < 4; i++)
            {
                var next = ReadByte();
                value = (value << 7) | (next & 0x7f);
                if ((next & 0x80) == 0) return value;
            }
            throw new MidiFormatException("MIDI 可变长度整数超过 4 字节。");
        }
        public void Skip(int length) { Require(length); position += length; }
        public byte[] ReadBytes(int length)
        {
            Require(length);
            var result = bytes.AsSpan(position, length).ToArray();
            position += length;
            return result;
        }
        public string ReadTag() => Encoding.ASCII.GetString(ReadBytes(4));
    }

    private sealed class TempoMap
    {
        private readonly int division;
        private readonly TempoSegment[] segments;
        public IReadOnlyList<MidiTempoChange> Changes { get; }

        public TempoMap(int division, List<RawTempo> tempos)
        {
            this.division = division;
            var changes = tempos.GroupBy(t => t.Tick).Select(g => g.MaxBy(t => t.Order)!)
                .OrderBy(t => t.Tick).ToList();
            if (changes.Count == 0 || changes[0].Tick != 0)
                changes.Insert(0, new RawTempo(0, DefaultTempo, -1));
            var result = new List<TempoSegment>();
            double elapsedTicks = 0;
            long previousTick = 0;
            var previousTempo = DefaultTempo;
            foreach (var change in changes)
            {
                elapsedTicks += (change.Tick - previousTick) * (double)previousTempo * 10 / division;
                result.Add(new TempoSegment(change.Tick, change.Tempo, elapsedTicks));
                previousTick = change.Tick;
                previousTempo = change.Tempo;
            }
            segments = result.ToArray();
            Changes = segments.Select(s => new MidiTempoChange(s.Tick, s.Tempo, ToTime(s.ElapsedTicks))).ToArray();
        }

        public TimeSpan At(long tick)
        {
            var low = 0;
            var high = segments.Length - 1;
            while (low < high)
            {
                var mid = (low + high + 1) / 2;
                if (segments[mid].Tick <= tick) low = mid;
                else high = mid - 1;
            }
            var segment = segments[low];
            return ToTime(segment.ElapsedTicks + (tick - segment.Tick) * (double)segment.Tempo * 10 / division);
        }

        private static TimeSpan ToTime(double ticks)
        {
            if (!double.IsFinite(ticks) || ticks < 0 || ticks >= long.MaxValue)
                throw new MidiFormatException("MIDI 时间范围超过支持的最大时长。");
            return TimeSpan.FromTicks((long)Math.Round(ticks));
        }
    }

    private sealed class RawTrack(int index)
    {
        public int Index { get; } = index;
        public string Name { get; set; } = string.Empty;
        public long EndTick { get; set; }
        public HashSet<int> Channels { get; } = [];
        public HashSet<int> Programs { get; } = [];
    }

    private sealed record NoteStart(long Tick, int Velocity);
    private sealed record RawNote(int Track, int Channel, int Pitch, int Velocity, long StartTick, long EndTick);
    private sealed record RawTempo(long Tick, int Tempo, int Order);
    private sealed record TempoSegment(long Tick, int Tempo, double ElapsedTicks);
}
