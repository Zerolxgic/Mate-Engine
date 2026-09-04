using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Generation-owned PTT lifecycle. It never calls an LLM provider directly.</summary>
public sealed class MateSpeechInputOrchestrator
{
    readonly IMateMicrophoneCapture microphone;
    readonly IMateAsrProvider asr;
    readonly Func<MateSpeechInputConfig> config;
    readonly Func<string, bool> submitCanonicalUserMessage;
    readonly Action interruptAssistantAndSpeech;
    CancellationTokenSource workCts;
    long generation;
    DateTime captureStartedUtc;

    public MateSpeechInputState State { get; private set; }
    public MateAsrConnectionStatus ConnectionStatus { get; private set; } = MateAsrConnectionStatus.Configured;
    public string LastError { get; private set; }
    public string ActiveDeviceName => microphone?.ActiveDeviceName ?? "";
    public long Generation => generation;

    public MateSpeechInputOrchestrator(IMateMicrophoneCapture microphone, IMateAsrProvider asr, Func<MateSpeechInputConfig> config, Func<string, bool> submit, Action interrupt)
    {
        this.microphone = microphone; this.asr = asr; this.config = config; submitCanonicalUserMessage = submit; interruptAssistantAndSpeech = interrupt;
        State = IsEnabled ? MateSpeechInputState.Idle : MateSpeechInputState.Disabled;
    }
    bool IsEnabled => (config?.Invoke() ?? new MateSpeechInputConfig()).speechInputEnabled;

    public bool StartPushToTalk(out string error)
    {
        error = null;
        if (!IsEnabled) { State = MateSpeechInputState.Disabled; error = "speech input off"; return false; }
        if (State == MateSpeechInputState.Capturing) return true; // duplicate start cannot create another owner.
        if (State == MateSpeechInputState.Transcribing || State == MateSpeechInputState.Submitting) Cancel("replaced by fresh PTT");
        interruptAssistantAndSpeech?.Invoke();
        generation++;
        LastError = null;
        if (microphone == null || !microphone.Begin(16000, out error))
        {
            State = MateSpeechInputState.Unavailable; LastError = error ?? "microphone unavailable"; return false;
        }
        captureStartedUtc = DateTime.UtcNow; State = MateSpeechInputState.Capturing; return true;
    }

    public bool StopPushToTalk(out string error)
    {
        error = null;
        if (State != MateSpeechInputState.Capturing) { error = "not capturing"; return false; }
        if (!microphone.TryFinalize(out var utterance, out error)) { State = MateSpeechInputState.Error; LastError = error; return false; }
        if (utterance.Samples.Length < (config?.Invoke() ?? new MateSpeechInputConfig()).GetMinimumSamples()) { State = MateSpeechInputState.Idle; LastError = "capture too short"; return true; }
        long ownedGeneration = generation; workCts?.Cancel(); workCts?.Dispose(); workCts = new CancellationTokenSource();
        State = MateSpeechInputState.Transcribing; _ = TranscribeAndSubmitAsync(utterance, ownedGeneration, workCts.Token); return true;
    }

    public void Tick()
    {
        if (State != MateSpeechInputState.Capturing) return;
        if ((DateTime.UtcNow - captureStartedUtc).TotalSeconds >= (config?.Invoke() ?? new MateSpeechInputConfig()).GetMaximumCaptureSeconds())
            StopPushToTalk(out _);
    }

    public void SetEnabled(bool enabled)
    {
        var cfg = config?.Invoke(); if (cfg != null) cfg.speechInputEnabled = enabled;
        if (!enabled) { Cancel("speech input off"); State = MateSpeechInputState.Disabled; }
        else if (State == MateSpeechInputState.Disabled) { LastError = null; State = MateSpeechInputState.Idle; }
    }

    public void Cancel(string reason = "cancelled")
    {
        generation++; try { workCts?.Cancel(); } catch { } try { microphone?.Cancel(); } catch { }
        if (IsEnabled) State = MateSpeechInputState.Idle; else State = MateSpeechInputState.Disabled;
        LastError = reason;
    }

    async Task TranscribeAndSubmitAsync(MatePcmUtterance utterance, long ownedGeneration, CancellationToken token)
    {
        try
        {
            var result = await asr.TranscribeAsync(utterance, token);
            if (token.IsCancellationRequested || ownedGeneration != generation || !IsEnabled) return;
            if (result == null || !result.Success) { ConnectionStatus = MateAsrConnectionStatus.Unavailable; State = MateSpeechInputState.Unavailable; LastError = result?.Error ?? "ASR unavailable"; return; }
            string transcript = (result.Transcript ?? "").Trim();
            if (transcript.Length == 0) { State = MateSpeechInputState.Idle; LastError = "empty transcript"; return; }
            ConnectionStatus = MateAsrConnectionStatus.Connected; State = MateSpeechInputState.Submitting;
            if (ownedGeneration != generation || token.IsCancellationRequested || !IsEnabled) return;
            if (submitCanonicalUserMessage == null || !submitCanonicalUserMessage(transcript)) { State = MateSpeechInputState.Error; LastError = "canonical send rejected"; return; }
            State = MateSpeechInputState.Idle; LastError = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (ownedGeneration == generation) { State = MateSpeechInputState.Error; LastError = ex.Message; } }
    }
}
