using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DetPS2.Core;

/// <summary>
/// Deterministic input recording and playback (Phase 11).
/// Frames are keyed by MasterCycles; pad state applied before RunFor slices.
/// </summary>
public sealed class InputRecording
{
    public readonly struct Frame
    {
        public ulong Cycle { get; init; }
        public uint Buttons { get; init; }
    }

    private readonly List<Frame> _frames = new();
    private int _playbackIndex;
    private bool _recording;
    private bool _playing;

    public bool IsRecording => _recording;
    public bool IsPlaying => _playing;
    public int FrameCount => _frames.Count;
    public IReadOnlyList<Frame> Frames => _frames;

    public void Reset()
    {
        _frames.Clear();
        _playbackIndex = 0;
        _recording = false;
        _playing = false;
    }

    public void StartRecording()
    {
        _frames.Clear();
        _playbackIndex = 0;
        _recording = true;
        _playing = false;
    }

    public void StopRecording() => _recording = false;

    public void StartPlayback()
    {
        _playbackIndex = 0;
        _playing = true;
        _recording = false;
    }

    public void StopPlayback() => _playing = false;

    public void Record(ulong cycle, uint buttons)
    {
        if (!_recording) return;
        // Coalesce identical consecutive states
        if (_frames.Count > 0)
        {
            var last = _frames[^1];
            if (last.Buttons == buttons) return;
        }
        _frames.Add(new Frame { Cycle = cycle, Buttons = buttons });
    }

    /// <summary>Apply any frames due at or before current cycle. Returns buttons to set (or null if none).</summary>
    public uint? PollPlayback(ulong cycle)
    {
        if (!_playing || _playbackIndex >= _frames.Count) return null;
        uint? last = null;
        while (_playbackIndex < _frames.Count && _frames[_playbackIndex].Cycle <= cycle)
        {
            last = _frames[_playbackIndex].Buttons;
            _playbackIndex++;
        }
        return last;
    }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(0x52504E49u); // 'INPR'
        w.Write(1u); // version
        w.Write(_frames.Count);
        foreach (var f in _frames)
        {
            w.Write(f.Cycle);
            w.Write(f.Buttons);
        }
        return ms.ToArray();
    }

    public bool Deserialize(byte[] data)
    {
        if (data == null || data.Length < 12) return false;
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        if (r.ReadUInt32() != 0x52504E49u) return false;
        if (r.ReadUInt32() != 1u) return false;
        int n = r.ReadInt32();
        _frames.Clear();
        for (int i = 0; i < n; i++)
        {
            _frames.Add(new Frame { Cycle = r.ReadUInt64(), Buttons = r.ReadUInt32() });
        }
        _playbackIndex = 0;
        return true;
    }
}
