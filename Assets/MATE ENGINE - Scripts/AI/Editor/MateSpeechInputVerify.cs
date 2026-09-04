#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

/// <summary>Deterministic Slice 9 verifier. Uses fakes only; no device or Speaches service is contacted.</summary>
public static class MateSpeechInputVerify
{
    const string MenuPath = "Mate Engine/Verify Speech Input";
    [MenuItem(MenuPath)] static void RunMenu()
    {
        var results = new List<string>(); int failed = 0;
        foreach (var c in Cases)
        {
            try { c.Body(); results.Add("- PASS " + c.Name); }
            catch (Exception ex) { failed++; results.Add("- FAIL " + c.Name + ": " + ex.Message); }
        }
        string dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "..", "reports");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "2026-09-03-Mate-Engine-Slice-9-Speech-Input-Verify.md");
        File.WriteAllText(path, "# Mate Speech Input Verification\n\n" + string.Join("\n", results) + "\n\nResult: " + (failed == 0 ? "PASS" : "FAILED") + " (" + (Cases.Length - failed) + "/" + Cases.Length + ")\n");
        if (failed > 0) throw new Exception("Mate Speech Input FAILED " + failed + "/" + Cases.Length + " — " + path);
        UnityEngine.Debug.Log("[MateSpeechInputVerify] PASS " + Cases.Length + "/" + Cases.Length + " " + path);
    }
    public static void RunFromCommandLine() { RunMenu(); }

    sealed class Case { public string Name; public Action Body; public Case(string n, Action b) { Name = n; Body = b; } }
    static readonly Case[] Cases = {
        new Case("01 configured enabled-disabled", ConfigState), new Case("02 PTT enters capture", StartCapture), new Case("03 duplicate start one owner", DuplicateStart),
        new Case("04 stop finalizes one utterance", StopFinalizes), new Case("05 short capture no turn", ShortCapture), new Case("06 max duration bounded", MaximumBounded),
        new Case("07 WAV RIFF structure", WavStructure), new Case("08 WAV metadata/data length", WavMetadata), new Case("09 ASR success transcript", AsrSuccess),
        new Case("10 empty transcript no turn", EmptyTranscript), new Case("11 malformed response isolated", Malformed), new Case("12 unavailable provider isolated", Unavailable),
        new Case("13 ASR cancellation rejects late", CancellationRejectsLate), new Case("14 cancelled capture cannot submit", CancelledCapture), new Case("15 Off invalidates capture", OffInvalidatesCapture),
        new Case("16 Off invalidates transcription", OffInvalidatesTranscription), new Case("17 fresh PTT recovers", FreshRecovery), new Case("18 canonical send once", CanonicalOnce),
        new Case("19 voice user semantics", VoiceSemantics), new Case("20 PTT interruption policy", InterruptionPolicy), new Case("21 lost UI release bounded", MaximumBounded),
        new Case("22 microphone unavailable isolated", MicrophoneUnavailable), new Case("23 provider seam independent", ProviderSeam), new Case("24 config owns ASR defaults", ConfigOwnership),
        new Case("25 Slice 8 preserved boundary", Slice8Boundary),
    };

    static MateSpeechInputConfig Cfg(bool enabled = true) => new MateSpeechInputConfig { speechInputEnabled = enabled, maximumCaptureSeconds = 90, minimumSamples = 4 };
    static MateSpeechInputOrchestrator New(FakeMic mic, FakeAsr asr, MateSpeechInputConfig cfg, Action<string> send = null, Action interrupt = null)
        => new MateSpeechInputOrchestrator(mic, asr, () => cfg, s => { send?.Invoke(s); return true; }, interrupt);
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    static void Wait() { Thread.Sleep(30); }

    static void ConfigState() { var cfg = Cfg(false); var o = New(new FakeMic(), new FakeAsr(), cfg); Assert(o.State == MateSpeechInputState.Disabled, "disabled"); o.SetEnabled(true); Assert(o.State == MateSpeechInputState.Idle, "enabled"); }
    static void StartCapture() { var mic = new FakeMic(); var o = New(mic, new FakeAsr(), Cfg()); Assert(o.StartPushToTalk(out _), "start"); Assert(o.State == MateSpeechInputState.Capturing && mic.BeginCalls == 1, "capturing"); }
    static void DuplicateStart() { var mic = new FakeMic(); var o = New(mic, new FakeAsr(), Cfg()); o.StartPushToTalk(out _); o.StartPushToTalk(out _); Assert(mic.BeginCalls == 1, "two owners"); }
    static void StopFinalizes() { var mic = new FakeMic(); var o = New(mic, new FakeAsr(), Cfg()); o.StartPushToTalk(out _); o.StopPushToTalk(out _); Assert(mic.FinalizeCalls == 1, "finalize count"); }
    static void ShortCapture() { var mic = new FakeMic { Samples = new short[2] }; int sent = 0; var o = New(mic, new FakeAsr(), Cfg(), _ => sent++); o.StartPushToTalk(out _); o.StopPushToTalk(out _); Wait(); Assert(sent == 0 && o.State == MateSpeechInputState.Idle, "short turn"); }
    static void MaximumBounded() { var cfg = Cfg(); cfg.maximumCaptureSeconds = 1; var mic = new FakeMic(); var o = New(mic, new FakeAsr(), cfg); o.StartPushToTalk(out _); typeof(MateSpeechInputOrchestrator).GetField("captureStartedUtc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(o, DateTime.UtcNow.AddSeconds(-2)); o.Tick(); Assert(mic.FinalizeCalls == 1, "unbounded capture"); }
    static void WavStructure() { var b = MatePcm16WavEncoder.Encode(new MatePcmUtterance(new short[] { 1, 2 }, 16000)); Assert(System.Text.Encoding.ASCII.GetString(b, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(b, 8, 4) == "WAVE", "header"); }
    static void WavMetadata() { var b = MatePcm16WavEncoder.Encode(new MatePcmUtterance(new short[] { 1, 2, 3 }, 16000)); Assert(BitConverter.ToInt16(b, 22) == 1 && BitConverter.ToInt32(b, 24) == 16000 && BitConverter.ToInt32(b, 40) == 6, "metadata"); }
    static void AsrSuccess() { int sent = 0; var o = New(new FakeMic(), new FakeAsr { Result = new MateAsrResult(" hello ") }, Cfg(), s => { sent++; Assert(s == "hello", "trim"); }); o.StartPushToTalk(out _); o.StopPushToTalk(out _); Wait(); Assert(sent == 1, "success missing"); }
    static void EmptyTranscript() { int sent = 0; var o = New(new FakeMic(), new FakeAsr { Result = new MateAsrResult(" ") }, Cfg(), _ => sent++); o.StartPushToTalk(out _); o.StopPushToTalk(out _); Wait(); Assert(sent == 0, "empty sent"); }
    static void Malformed() { var o = New(new FakeMic(), new FakeAsr { Result = new MateAsrResult(null, "malformed ASR response") }, Cfg()); o.StartPushToTalk(out _); o.StopPushToTalk(out _); Wait(); Assert(o.State == MateSpeechInputState.Unavailable, "malformed not isolated"); }
    static void Unavailable() { var o = New(new FakeMic(), new FakeAsr { Result = new MateAsrResult(null, "HTTP 503") }, Cfg()); o.StartPushToTalk(out _); o.StopPushToTalk(out _); Wait(); Assert(o.State == MateSpeechInputState.Unavailable, "unavailable"); }
    static void CancellationRejectsLate() { var asr = new FakeAsr { Gate = new TaskCompletionSource<MateAsrResult>() }; int sent = 0; var o = New(new FakeMic(), asr, Cfg(), _ => sent++); o.StartPushToTalk(out _); o.StopPushToTalk(out _); o.Cancel(); asr.Gate.SetResult(new MateAsrResult("late")); Wait(); Assert(sent == 0, "late sent"); }
    static void CancelledCapture() { int sent = 0; var o = New(new FakeMic(), new FakeAsr(), Cfg(), _ => sent++); o.StartPushToTalk(out _); o.Cancel(); o.StopPushToTalk(out _); Wait(); Assert(sent == 0, "cancelled capture sent"); }
    static void OffInvalidatesCapture() { var mic = new FakeMic(); var cfg = Cfg(); var o = New(mic, new FakeAsr(), cfg); o.StartPushToTalk(out _); o.SetEnabled(false); Assert(!mic.IsCapturing && o.State == MateSpeechInputState.Disabled, "off capture"); }
    static void OffInvalidatesTranscription() { var asr = new FakeAsr { Gate = new TaskCompletionSource<MateAsrResult>() }; int sent = 0; var cfg = Cfg(); var o = New(new FakeMic(), asr, cfg, _ => sent++); o.StartPushToTalk(out _); o.StopPushToTalk(out _); o.SetEnabled(false); asr.Gate.SetResult(new MateAsrResult("late")); Wait(); Assert(sent == 0 && o.State == MateSpeechInputState.Disabled, "off transcription"); }
    static void FreshRecovery() { var asr = new FakeAsr { Result = new MateAsrResult(null, "offline") }; var o = New(new FakeMic(), asr, Cfg()); o.StartPushToTalk(out _); o.StopPushToTalk(out _); Wait(); asr.Result = new MateAsrResult("recovered"); Assert(o.StartPushToTalk(out _), "fresh start"); }
    static void CanonicalOnce() { int sent = 0; var o = New(new FakeMic(), new FakeAsr { Result = new MateAsrResult("one") }, Cfg(), _ => sent++); o.StartPushToTalk(out _); o.StopPushToTalk(out _); Wait(); Assert(sent == 1, "not exactly one"); }
    static void VoiceSemantics() { string received = null; var o = New(new FakeMic(), new FakeAsr { Result = new MateAsrResult("same path") }, Cfg(), s => received = s); o.StartPushToTalk(out _); o.StopPushToTalk(out _); Wait(); Assert(received == "same path", "canonical payload"); }
    static void InterruptionPolicy() { int interruptions = 0; var o = New(new FakeMic(), new FakeAsr(), Cfg(), null, () => interruptions++); o.StartPushToTalk(out _); Assert(interruptions == 1, "not cancelled before capture"); }
    static void MicrophoneUnavailable() { var o = New(new FakeMic { BeginError = "unavailable" }, new FakeAsr(), Cfg()); Assert(!o.StartPushToTalk(out _ ) && o.State == MateSpeechInputState.Unavailable, "mic failure"); }
    static void ProviderSeam() { Assert(typeof(IMateAsrProvider).GetMethod("TranscribeAsync") != null && typeof(IMateMicrophoneCapture).GetMethod("Begin") != null, "seams missing"); }
    static void ConfigOwnership() { var c = new MateSpeechInputConfig(); Assert(c.baseUrl == MateSpeechInputConfig.DefaultBaseUrl && c.modelId == MateSpeechInputConfig.DefaultModelId && c.language == "en" && c.maximumCaptureSeconds == 90, "defaults"); }
    static void Slice8Boundary() { Assert(typeof(MateSpeechOrchestrator).GetMethod("OnTurnStarted") != null, "speech output interface changed"); }

    sealed class FakeMic : IMateMicrophoneCapture
    { public int BeginCalls, FinalizeCalls; public short[] Samples = new short[] { 1, 2, 3, 4, 5 }; public string BeginError; public bool IsCapturing { get; private set; } public string ActiveDeviceName => "fake";
      public bool Begin(int rate, out string error) { BeginCalls++; error = BeginError; IsCapturing = error == null; return IsCapturing; }
      public bool TryFinalize(out MatePcmUtterance u, out string error) { FinalizeCalls++; error = null; u = new MatePcmUtterance(Samples, 16000); IsCapturing = false; return true; } public void Cancel() { IsCapturing = false; } }
    sealed class FakeAsr : IMateAsrProvider
    { public MateAsrResult Result = new MateAsrResult("ok"); public TaskCompletionSource<MateAsrResult> Gate;
      public Task<MateAsrResult> TranscribeAsync(MatePcmUtterance u, CancellationToken t) => Gate != null ? Gate.Task : Task.FromResult(Result); public Task<MateAsrConnectionStatus> ProbeAsync(CancellationToken t) => Task.FromResult(MateAsrConnectionStatus.Connected); }
}
#endif
