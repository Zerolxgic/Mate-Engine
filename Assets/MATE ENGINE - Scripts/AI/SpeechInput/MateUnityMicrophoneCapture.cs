using System;
using UnityEngine;

/// <summary>Unity-owned default-device capture. Browser code only expresses PTT intent.</summary>
public sealed class MateUnityMicrophoneCapture : IMateMicrophoneCapture
{
    AudioClip clip;
    string device;
    int sampleRate;
    bool capturing;

    public bool IsCapturing => capturing;
    public string ActiveDeviceName => device ?? "";

    public bool Begin(int targetSampleRate, out string error)
    {
        error = null;
        if (capturing) { error = "capture already active"; return false; }
        try
        {
            var devices = Microphone.devices;
            if (devices == null || devices.Length == 0) { error = "microphone unavailable"; return false; }
            device = null; // Unity's null device selects the system default.
            sampleRate = targetSampleRate;
            clip = Microphone.Start(device, false, 90, targetSampleRate);
            if (clip == null) { error = "microphone start failed"; return false; }
            capturing = true;
            return true;
        }
        catch (Exception ex) { error = ex.Message; Cancel(); return false; }
    }

    public bool TryFinalize(out MatePcmUtterance utterance, out string error)
    {
        utterance = null; error = null;
        if (!capturing || clip == null) { error = "no active capture"; return false; }
        try
        {
            int position = Microphone.GetPosition(device);
            Microphone.End(device);
            capturing = false;
            if (position <= 0) { error = "no microphone samples"; return false; }
            int channels = Mathf.Max(1, clip.channels);
            int count = Mathf.Min(position * channels, clip.samples * channels);
            var floats = new float[count];
            if (!clip.GetData(floats, 0)) { error = "microphone read failed"; return false; }
            // Slice 9 records mono; downmix unexpected multi-channel input without altering rate metadata.
            int frames = count / channels;
            var pcm = new short[frames];
            for (int f = 0; f < frames; f++)
            {
                float sum = 0; for (int c = 0; c < channels; c++) sum += floats[f * channels + c];
                pcm[f] = (short)Mathf.Clamp(Mathf.RoundToInt((sum / channels) * 32767f), short.MinValue, short.MaxValue);
            }
            utterance = new MatePcmUtterance(pcm, clip.frequency > 0 ? clip.frequency : sampleRate, 1);
            clip = null;
            return true;
        }
        catch (Exception ex) { error = ex.Message; Cancel(); return false; }
    }

    public void Cancel()
    {
        try { if (capturing) Microphone.End(device); } catch { }
        capturing = false; clip = null;
    }
}
