#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Deterministic EditMode verification for Slice 8 speech fluency (preserves Slice 7 cases 1–24).
/// Menu: Mate Engine / Verify Speech Fluency
/// </summary>
public static class MateSpeechOutputVerify
{
    const string MenuPath = "Mate Engine/Verify Speech Fluency";
    const string LogPrefix = "[MateSpeechVerify]";

    [MenuItem(MenuPath)]
    public static void RunFromMenu()
    {
        var result = RunAll();
        if (result.Failed > 0)
        {
            Debug.LogError($"{LogPrefix} FAILED {result.Failed}/{result.Total}. See report: {result.ReportPath}");
            if (!Application.isBatchMode)
                EditorUtility.DisplayDialog("Mate Speech Output", $"FAILED {result.Failed}/{result.Total}\n{result.ReportPath}", "OK");
        }
        else
        {
            Debug.Log($"{LogPrefix} PASS {result.Total}/{result.Total}. Report: {result.ReportPath}");
            if (!Application.isBatchMode)
                EditorUtility.DisplayDialog("Mate Speech Output", $"PASS {result.Total}/{result.Total}", "OK");
        }
    }

    public static VerifyResult RunAll()
    {
        MateSpeechService.OverrideForTests = null;

        var cases = new List<(string id, Action test)>
        {
            ("1", Case1_ProseSpeakable),
            ("2", Case2_CodeBlockExcluded),
            ("3", Case3_ReasoningExcluded),
            ("4", Case4_ToolControlExcluded),
            ("5", Case5_MarkdownStripped),
            ("6", Case6_MarkdownLinkLabel),
            ("7", Case7_BareUrlExcluded),
            ("8", Case8_InlineCodeExcluded),
            ("9", Case9_HostileSentenceSplit),
            ("10", Case10_MultipleSentencesOrder),
            ("11", Case11_TrailingFlush),
            ("12", Case12_EmptyNoJob),
            ("13", Case13_LongBufferFallback),
            ("14", Case14_CancelClearsQueue),
            ("15", Case15_CancelledInFlightCannotPlay),
            ("16", Case16_SpeechOffStops),
            ("17", Case17_PostCancelFreshTurn),
            ("18", Case18_ProviderFailureLeavesChat),
            ("19", Case19_ProviderAbstractionIsolated),
            ("20", Case20_KokoroStreamingPcm16WavDecodes),
            ("21", Case21_MalformedWavRejected),
            ("22", Case22_OpeningStreamedTextEmitsOnce),
            ("23", Case23_ProjectionRewriteDoesNotReplayCommittedOpening),
            ("24", Case24_StreamedCodeSuppressionRemainsOnceOnly),
            ("25", Case25_FirstChunkAndStrongBoundaryRelease),
            ("26", Case26_WeakBoundaryThreshold),
            ("27", Case27_OneChunkPrefetchIsOrdered),
            ("28", Case28_CancelClearsReadyPrefetch),
            ("29", Case29_SpeechOffClearsPrefetch),
            ("30", Case30_PrefetchProviderFailureRecovers),
            ("31", Case31_VoiceCapturedAtSynthesis),
            ("32", Case32_TimingLifecycleRecorded),
            ("33", Case33_PrefetchDepthIsOne),
        };

        var sb = new StringBuilder();
        sb.AppendLine("# Mate Speech Fluency Verification");
        sb.AppendLine($"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        int failed = 0;
        foreach (var (id, test) in cases)
        {
            try
            {
                MateSpeechService.OverrideForTests = null;
                test();
                sb.AppendLine($"- {id}: PASS");
                Debug.Log($"{LogPrefix} {id}: PASS");
            }
            catch (Exception ex)
            {
                failed++;
                sb.AppendLine($"- {id}: FAIL — {ex.Message}");
                Debug.LogError($"{LogPrefix} {id}: FAIL — {ex.Message}");
            }
            finally
            {
                MateSpeechService.OverrideForTests = null;
            }
        }

        sb.AppendLine();
        sb.AppendLine(failed == 0
            ? $"RESULT: PASS ({cases.Count}/{cases.Count})"
            : $"RESULT: FAIL ({failed}/{cases.Count} failed)");

        string reportDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "reports"));
        Directory.CreateDirectory(reportDir);
        string reportPath = Path.Combine(reportDir, "2026-09-02-Mate-Engine-Slice-8-Speech-Fluency-Verify.md");
        File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);

        return new VerifyResult
        {
            Total = cases.Count,
            Failed = failed,
            ReportPath = reportPath,
        };
    }

    /// <summary>Batch-safe entrypoint for the existing Slice 8 deterministic verifier.</summary>
    public static void RunFromCommandLine()
    {
        var result = RunAll();
        if (result.Failed > 0)
            throw new Exception($"Mate Speech Output FAILED {result.Failed}/{result.Total}: {result.ReportPath}");
        Debug.Log($"{LogPrefix} PASS {result.Total}/{result.Total}: {result.ReportPath}");
    }

    public struct VerifyResult
    {
        public int Total;
        public int Failed;
        public string ReportPath;
    }

    static MateConversationTurn ProseTurn(string text)
    {
        var turn = new MateConversationTurn();
        if (!turn.Start()) throw new Exception("Start failed");
        if (!turn.AppendAssistantChunk(text)) throw new Exception("Append failed");
        return turn;
    }

    static void AssertEq(string actual, string expected, string label)
    {
        if (actual != expected)
            throw new Exception($"{label} mismatch.\nExpected: {Show(expected)}\nActual: {Show(actual)}");
    }

    static void AssertTrue(bool cond, string msg)
    {
        if (!cond) throw new Exception(msg);
    }

    static string Show(string s)
    {
        if (s == null) return "<null>";
        return s.Replace("\n", "\\n").Replace("\r", "\\r");
    }

    static void Case1_ProseSpeakable()
    {
        var turn = ProseTurn("Hello there, friend.");
        string speakable = MateSpeechProjector.Project(turn.GetSegmentsSnapshot());
        AssertEq(speakable, "Hello there, friend.", "prose");
    }

    static void Case2_CodeBlockExcluded()
    {
        var turn = new MateConversationTurn();
        turn.Start();
        turn.AppendAssistantChunk("Before.\n```csharp\nint x = 1;\n```\nAfter.");
        turn.Complete();
        string speakable = MateSpeechProjector.Project(turn.GetSegmentsSnapshot());
        AssertTrue(!speakable.Contains("int x"), "code body spoken");
        AssertTrue(speakable.Contains("Before"), "missing before");
        AssertTrue(speakable.Contains("After"), "missing after");
    }

    static void Case3_ReasoningExcluded()
    {
        var turn = new MateConversationTurn();
        turn.Start();
        turn.AppendReasoningChunk("secret chain of thought");
        turn.AppendAssistantChunk("Visible answer.");
        turn.Complete();
        string speakable = MateSpeechProjector.Project(turn.GetSegmentsSnapshot());
        AssertEq(speakable, "Visible answer.", "reasoning leak");
    }

    static void Case4_ToolControlExcluded()
    {
        var turnId = Guid.NewGuid();
        var segs = new List<MateResponseSegment>
        {
            new MateResponseSegment(turnId, 0, MateResponseSegmentKind.Tool, "tool payload", "tool payload", "", true, true),
            new MateResponseSegment(turnId, 1, MateResponseSegmentKind.Control, "ctrl", "ctrl", "", true, true),
            new MateResponseSegment(turnId, 2, MateResponseSegmentKind.Prose, "Say this.", "Say this.", "", true, true),
        };
        string speakable = MateSpeechProjector.Project(segs);
        AssertEq(speakable, "Say this.", "tool/control");
    }

    static void Case5_MarkdownStripped()
    {
        string s = MateSpeechProjector.ProjectProse("Please **restart Mate** now.");
        AssertEq(s, "Please restart Mate now.", "bold");
        s = MateSpeechProjector.ProjectProse("## Important");
        AssertEq(s, "Important", "heading");
    }

    static void Case6_MarkdownLinkLabel()
    {
        string s = MateSpeechProjector.ProjectProse("See [documentation](https://example.com) today.");
        AssertEq(s, "See documentation today.", "link");
        AssertTrue(!s.Contains("https"), "url leaked");
    }

    static void Case7_BareUrlExcluded()
    {
        string s = MateSpeechProjector.ProjectProse("Visit https://example.com for more.");
        AssertTrue(!s.Contains("https"), "bare url spoken: " + s);
        AssertTrue(s.Contains("Visit") && s.Contains("for more"), "prose lost: " + s);
    }

    static void Case8_InlineCodeExcluded()
    {
        string s = MateSpeechProjector.ProjectProse("Run `MateSpeechConfig` locally.");
        AssertTrue(!s.Contains("MateSpeechConfig"), "inline code spoken: " + s);
        AssertTrue(s.Contains("Run") && s.Contains("locally"), "prose lost: " + s);
    }

    static void Case9_HostileSentenceSplit()
    {
        var chunker = new MateSentenceChunker();
        var a = chunker.Append("Hello wor");
        AssertTrue(a.Count == 0, "emitted too early");
        var b = chunker.Append("ld. Next");
        AssertTrue(b.Count == 1, "expected one sentence, got " + b.Count);
        AssertEq(b[0], "Hello world.", "split sentence");
        var c = chunker.Flush();
        AssertTrue(c.Count == 1, "flush count");
        AssertEq(c[0], "Next", "flush text");
    }

    static void Case10_MultipleSentencesOrder()
    {
        var chunker = new MateSentenceChunker();
        var emitted = new List<string>();
        emitted.AddRange(chunker.Append("One. Two! Three?"));
        emitted.AddRange(chunker.Flush());
        AssertTrue(emitted.Count == 3, "count=" + emitted.Count);
        AssertEq(emitted[0], "One.", "1");
        AssertEq(emitted[1], "Two!", "2");
        AssertEq(emitted[2], "Three?", "3");
    }

    static void Case11_TrailingFlush()
    {
        var chunker = new MateSentenceChunker();
        chunker.Append("Trailing words without end");
        var flushed = chunker.Flush();
        AssertTrue(flushed.Count == 1, "flush missing");
        AssertEq(flushed[0], "Trailing words without end", "flush");
    }

    static void Case12_EmptyNoJob()
    {
        RunSync(async () =>
        {
            var provider = new MateFakeTtsProvider();
            var player = new MateFakeSpeechPlayer();
            var orch = MateSpeechOrchestrator.ForTests(provider, player);
            var turnId = Guid.NewGuid();
            orch.OnTurnStarted(turnId);

            var segs = new List<MateResponseSegment>
            {
                new MateResponseSegment(turnId, 0, MateResponseSegmentKind.CodeBlock, "code", "```\ncode\n```", "csharp", true, true),
            };
            orch.OnTurnCompleted(turnId, segs);
            await orch.PumpAsync();
            AssertTrue(orch.EnqueuedJobsForTests.Count == 0, "unexpected jobs: " + orch.EnqueuedJobsForTests.Count);
            AssertTrue(provider.SynthesizeCalls == 0, "synth called");
        });
    }

    static void Case13_LongBufferFallback()
    {
        var chunker = new MateSentenceChunker(maxBufferChars: 80);
        var longWordy = new string('a', 40) + " " + new string('b', 40) + " " + new string('c', 40);
        var emitted = chunker.Append(longWordy);
        AssertTrue(emitted.Count >= 1, "expected safety emit");
        int total = 0;
        foreach (var e in emitted) total += e.Length;
        total += chunker.BufferedLength;
        // Meaning preserved: all letter content still present across emit+buffer.
        AssertTrue(total >= 100, "lost content under fallback");
    }

    static void Case14_CancelClearsQueue()
    {
        RunSync(async () =>
        {
            var provider = new MateFakeTtsProvider { ArtificialDelayMs = 50 };
            var player = new MateFakeSpeechPlayer();
            var orch = MateSpeechOrchestrator.ForTests(provider, player);
            var turnId = Guid.NewGuid();
            orch.OnTurnStarted(turnId);
            orch.OnTurnCompleted(turnId, Segs(turnId, "First sentence. Second sentence. Third sentence."));
            AssertTrue(orch.QueuedCount >= 2 || orch.EnqueuedJobsForTests.Count >= 2, "expected queue");
            orch.OnTurnCancelled(turnId);
            AssertTrue(orch.QueuedCount == 0, "queue not cleared");
            await orch.PumpAsync();
            AssertTrue(player.PlayCount == 0, "played after cancel");
        });
    }

    static void Case15_CancelledInFlightCannotPlay()
    {
        RunSync(async () =>
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var provider = new MateFakeTtsProvider
            {
                BeforeSynthesize = async (req, ct) =>
                {
                    await gate.Task;
                    ct.ThrowIfCancellationRequested();
                }
            };
            var player = new MateFakeSpeechPlayer();
            var orch = MateSpeechOrchestrator.ForTests(provider, player);
            MateSpeechService.OverrideForTests = orch;

            var turnId = Guid.NewGuid();
            orch.OnTurnStarted(turnId);
            orch.OnTurnCompleted(turnId, Segs(turnId, "Hold this sentence."));
            // Start worker
            var pump = orch.PumpAsync();
            // Wait until synthesize starts
            for (int i = 0; i < 100 && provider.SynthesizeCalls == 0; i++)
                await Task.Delay(10);

            orch.OnTurnCancelled(turnId);
            gate.TrySetResult(true);
            await pump;

            AssertTrue(player.PlayCount == 0, "late cancelled audio played");
            AssertTrue(orch.RejectedLateJobsForTests.Count >= 1 || provider.SynthesizeCalls >= 1, "expected reject path");
        });
    }

    static void Case16_SpeechOffStops()
    {
        RunSync(async () =>
        {
            var provider = new MateFakeTtsProvider();
            var player = new MateFakeSpeechPlayer();
            var orch = MateSpeechOrchestrator.ForTests(provider, player, new MateSpeechConfig { speechOutputEnabled = true });
            var turnId = Guid.NewGuid();
            orch.OnTurnStarted(turnId);
            orch.OnSegmentsUpdated(turnId, Segs(turnId, "One. Two. Three."));
            AssertTrue(orch.EnqueuedJobsForTests.Count >= 1, "no jobs before off");
            orch.SetSpeechOutputEnabled(false, save: false);
            AssertTrue(orch.QueuedCount == 0, "queue remains after off");
            await orch.PumpAsync();
            AssertTrue(player.StopCount >= 1 || player.PlayCount == 0, "expected stop or no play");

            // While off, new content must not enqueue.
            int before = orch.EnqueuedJobsForTests.Count;
            orch.OnTurnCompleted(Guid.NewGuid(), Segs(Guid.NewGuid(), "Should not speak."));
            AssertTrue(orch.EnqueuedJobsForTests.Count == before, "jobs while off");
        });
    }

    static void Case17_PostCancelFreshTurn()
    {
        RunSync(async () =>
        {
            var provider = new MateFakeTtsProvider();
            var player = new MateFakeSpeechPlayer();
            var orch = MateSpeechOrchestrator.ForTests(provider, player);
            var t1 = Guid.NewGuid();
            orch.OnTurnStarted(t1);
            orch.OnTurnCompleted(t1, Segs(t1, "Cancelled speech."));
            orch.OnTurnCancelled(t1);

            var t2 = Guid.NewGuid();
            orch.OnTurnStarted(t2);
            orch.OnTurnCompleted(t2, Segs(t2, "Fresh turn speaks."));
            await orch.PumpAsync();
            AssertTrue(player.PlayCount >= 1, "fresh turn did not play");
            AssertTrue(player.PlayedJobs.Exists(j => j.turnId == t2), "wrong turn played");
        });
    }

    static void Case18_ProviderFailureLeavesChat()
    {
        RunSync(async () =>
        {
            var provider = new MateFakeTtsProvider { FailNext = true };
            var player = new MateFakeSpeechPlayer();
            var orch = MateSpeechOrchestrator.ForTests(provider, player);

            var turn = new MateConversationTurn();
            turn.Start();
            turn.AppendAssistantChunk("Chat text remains.");
            turn.Complete();
            string rawBefore = turn.GetRawResponseText();

            orch.OnTurnStarted(turn.TurnId);
            orch.OnTurnCompleted(turn.TurnId, turn.GetSegmentsSnapshot());
            await orch.PumpAsync();

            AssertEq(turn.GetRawResponseText(), rawBefore, "chat corrupted");
            AssertTrue(turn.State == MateConversationTurn.TurnState.Completed, "turn state changed");
            AssertTrue(!string.IsNullOrEmpty(orch.LastError) || orch.ConnectionStatus == MateTtsConnectionStatus.Unavailable,
                "expected bounded TTS error/status");
            AssertTrue(player.PlayCount == 0, "played failed audio");
        });
    }

    static void Case19_ProviderAbstractionIsolated()
    {
        // Segmentation must not know about Kokoro; projector only reads segment kinds.
        var turn = new MateConversationTurn();
        turn.Start();
        turn.AppendAssistantChunk("Hello **world**. `code` and https://x.test end.");
        turn.Complete();
        var segs = turn.GetSegmentsSnapshot();
        foreach (var s in segs)
        {
            // MateResponseSegment has no TTS fields — kind/text only.
            AssertTrue(s.Kind == MateResponseSegmentKind.Prose || s.Kind == MateResponseSegmentKind.CodeBlock,
                "unexpected kind");
        }

        string json = MateKokoroTtsProvider.BuildSpeechJson("hi", "af_bella");
        AssertTrue(json.Contains("\"response_format\":\"wav\""), "kokoro json");
        AssertTrue(json.Contains("\"voice\":\"af_bella\""), "voice");

        // Fake provider works without any Kokoro types in the orchestrator path.
        RunSync(async () =>
        {
            var orch = MateSpeechOrchestrator.ForTests(new MateFakeTtsProvider(), new MateFakeSpeechPlayer());
            orch.OnTurnStarted(turn.TurnId);
            orch.OnTurnCompleted(turn.TurnId, segs);
            await orch.PumpAsync();
            AssertTrue(orch.EnqueuedJobsForTests.Count >= 1, "expected speakable job");
            foreach (var job in orch.EnqueuedJobsForTests)
            {
                AssertTrue(!job.Text.Contains("https"), "url in speech job");
                AssertTrue(!job.Text.Contains("code"), "inline code in speech job: " + job.Text);
                AssertTrue(!job.Text.Contains("**"), "markdown in speech job");
            }
        });
    }

    static void Case20_KokoroStreamingPcm16WavDecodes()
    {
        var clip = MateWavDecoder.DecodePcm16(BuildStreamingPcm16Wav(-32768, 16384));
        try
        {
            AssertTrue(clip != null, "decoder returned null clip");
            AssertTrue(clip.channels == 1, "channels=" + clip.channels);
            AssertTrue(clip.frequency == 24000, "frequency=" + clip.frequency);
            AssertTrue(clip.samples == 2, "samples=" + clip.samples);
            var samples = new float[2];
            AssertTrue(clip.GetData(samples, 0), "could not read decoded samples");
            AssertTrue(Math.Abs(samples[0] + 1f) < 0.0001f, "first sample=" + samples[0]);
            AssertTrue(Math.Abs(samples[1] - 0.5f) < 0.0001f, "second sample=" + samples[1]);
        }
        finally
        {
            if (clip != null) UnityEngine.Object.DestroyImmediate(clip);
        }
    }

    static void Case21_MalformedWavRejected()
    {
        bool rejected = false;
        try { MateWavDecoder.DecodePcm16(new byte[] { 1, 2, 3, 4 }); }
        catch (InvalidOperationException) { rejected = true; }
        AssertTrue(rejected, "malformed WAV was accepted");
    }

    static void Case22_OpeningStreamedTextEmitsOnce()
    {
        var provider = new MateFakeTtsProvider();
        var player = new MateFakeSpeechPlayer();
        var orch = MateSpeechOrchestrator.ForTests(provider, player);
        var turnId = Guid.NewGuid();
        orch.OnTurnStarted(turnId);
        orch.OnSegmentsUpdated(turnId, Segs(turnId, "Here is"));
        orch.OnSegmentsUpdated(turnId, Segs(turnId, "Here is the first sentence."));
        orch.OnTurnCompleted(turnId, Segs(turnId, "Here is the first sentence."));
        AssertJobTexts(orch, "Here is the first sentence.");
    }

    static void Case23_ProjectionRewriteDoesNotReplayCommittedOpening()
    {
        // An incomplete bold span becomes normalized only when its closing ** arrives.
        // Before the correction, this non-prefix projection reset consumption to zero and
        // re-enqueued the already committed opening sentence during completion.
        var provider = new MateFakeTtsProvider();
        var player = new MateFakeSpeechPlayer();
        var orch = MateSpeechOrchestrator.ForTests(provider, player);
        var turnId = Guid.NewGuid();
        orch.OnTurnStarted(turnId);
        orch.OnSegmentsUpdated(turnId, Segs(turnId, "Opening sentence. **Next"));
        orch.OnTurnCompleted(turnId, Segs(turnId, "Opening sentence. **Next**"));
        AssertJobTexts(orch, "Opening sentence.", "Next");
    }

    static void Case24_StreamedCodeSuppressionRemainsOnceOnly()
    {
        var turn = new MateConversationTurn();
        AssertTrue(turn.Start(), "turn start failed");
        var orch = MateSpeechOrchestrator.ForTests(new MateFakeTtsProvider(), new MateFakeSpeechPlayer());
        orch.OnTurnStarted(turn.TurnId);
        turn.AppendAssistantChunk("Intro sentence.\n```python\nprint(\"skip me\")");
        orch.OnSegmentsUpdated(turn.TurnId, turn.GetSegmentsSnapshot());
        turn.AppendAssistantChunk("\n```\nClosing sentence.");
        orch.OnSegmentsUpdated(turn.TurnId, turn.GetSegmentsSnapshot());
        AssertTrue(turn.Complete(), "turn complete failed");
        orch.OnTurnCompleted(turn.TurnId, turn.GetSegmentsSnapshot());
        AssertJobTexts(orch, "Intro sentence.", "Closing sentence.");
    }

    static void Case25_FirstChunkAndStrongBoundaryRelease()
    {
        var chunker = new MateSentenceChunker();
        var first = chunker.Append("This is enough opening text to begin speaking.");
        AssertTrue(first.Count == 0, "terminal boundary must wait for a following stream boundary");
        first.AddRange(chunker.Append(" Next sentence arrives."));
        AssertEq(first[0], "This is enough opening text to begin speaking.", "first strong boundary");
    }

    static void Case26_WeakBoundaryThreshold()
    {
        var chunker = new MateSentenceChunker();
        AssertTrue(chunker.Append("Short, next").Count == 0, "tiny weak fragment released");
        string prefix = new string('a', MateSentenceChunker.WeakBoundaryMinChars) + ", next";
        var released = chunker.Append(prefix);
        AssertTrue(released.Count == 1, "grown weak boundary did not release");
    }

    static void Case27_OneChunkPrefetchIsOrdered()
    {
        RunSync(async () =>
        {
            var provider = new MateFakeTtsProvider();
            var player = new MateFakeSpeechPlayer { OnPlay = async ct => await Task.Delay(35, ct) };
            bool nextSynthesizedDuringPlayback = false;
            provider.BeforeSynthesize = (request, ct) =>
            {
                if (request.ChunkIndex == 1) nextSynthesizedDuringPlayback = player.IsPlaying;
                return Task.CompletedTask;
            };
            var orch = MateSpeechOrchestrator.ForTests(provider, player);
            var turnId = Guid.NewGuid();
            orch.OnTurnStarted(turnId);
            orch.OnTurnCompleted(turnId, Segs(turnId, "First complete sentence. Second complete sentence."));
            await orch.PumpAsync();
            AssertTrue(nextSynthesizedDuringPlayback, "N+1 did not synthesize during N playback");
            AssertTrue(player.PlayedJobs.Count == 2, "prefetch play count");
            AssertTrue(player.PlayedJobs[0].chunkIndex == 0 && player.PlayedJobs[1].chunkIndex == 1, "out-of-order playback");
        });
    }

    static void Case28_CancelClearsReadyPrefetch()
    {
        RunSync(async () =>
        {
            var playerGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var provider = new MateFakeTtsProvider();
            var player = new MateFakeSpeechPlayer { OnPlay = async ct => await playerGate.Task };
            var orch = MateSpeechOrchestrator.ForTests(provider, player);
            var turnId = Guid.NewGuid();
            orch.OnTurnStarted(turnId);
            orch.OnTurnCompleted(turnId, Segs(turnId, "First complete sentence. Second complete sentence."));
            var pump = orch.PumpAsync();
            for (int i = 0; i < 100 && provider.SynthesizeCalls < 2; i++) await Task.Delay(10);
            orch.OnTurnCancelled(turnId);
            playerGate.TrySetResult(true);
            await pump;
            AssertTrue(!orch.HasReadyPrefetch, "ready prefetch survived cancellation");
            AssertTrue(player.PlayedJobs.Count <= 1, "cancelled prefetched audio played");
        });
    }

    static void Case29_SpeechOffClearsPrefetch()
    {
        RunSync(async () =>
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var provider = new MateFakeTtsProvider();
            var player = new MateFakeSpeechPlayer { OnPlay = async ct => await gate.Task };
            var orch = MateSpeechOrchestrator.ForTests(provider, player);
            var turnId = Guid.NewGuid();
            orch.OnTurnStarted(turnId);
            orch.OnTurnCompleted(turnId, Segs(turnId, "First complete sentence. Second complete sentence."));
            var pump = orch.PumpAsync();
            for (int i = 0; i < 100 && provider.SynthesizeCalls < 2; i++) await Task.Delay(10);
            orch.SetSpeechOutputEnabled(false, save: false);
            gate.TrySetResult(true);
            await pump;
            AssertTrue(!orch.HasReadyPrefetch && orch.QueuedCount == 0, "speech off retained prefetched work");
        });
    }

    static void Case30_PrefetchProviderFailureRecovers()
    {
        RunSync(async () =>
        {
            var provider = new MateFakeTtsProvider();
            provider.BeforeSynthesize = (request, ct) =>
            {
                if (request.ChunkIndex == 1) throw new InvalidOperationException("prefetch failure");
                return Task.CompletedTask;
            };
            var player = new MateFakeSpeechPlayer { OnPlay = async ct => await Task.Delay(20, ct) };
            var orch = MateSpeechOrchestrator.ForTests(provider, player);
            var t1 = Guid.NewGuid();
            orch.OnTurnStarted(t1);
            orch.OnTurnCompleted(t1, Segs(t1, "First complete sentence. Failed next sentence."));
            await orch.PumpAsync();
            AssertTrue(player.PlayedJobs.Count == 1, "failed prefetch played or blocked current");
            var t2 = Guid.NewGuid();
            orch.OnTurnStarted(t2);
            orch.OnTurnCompleted(t2, Segs(t2, "Fresh turn recovers."));
            await orch.PumpAsync();
            AssertTrue(player.PlayedJobs.Exists(x => x.turnId == t2), "provider failure wedged fresh turn");
        });
    }

    static void Case31_VoiceCapturedAtSynthesis()
    {
        RunSync(async () =>
        {
            var provider = new MateFakeTtsProvider();
            var orch = MateSpeechOrchestrator.ForTests(provider, null, new MateSpeechConfig { selectedVoice = "voice_a" });
            var turnId = Guid.NewGuid();
            orch.OnTurnStarted(turnId);
            orch.OnTurnCompleted(turnId, Segs(turnId, "First complete sentence."));
            await orch.PumpAsync();
            orch.SetSelectedVoice("voice_b", save: false);
            var next = Guid.NewGuid();
            orch.OnTurnStarted(next);
            orch.OnTurnCompleted(next, Segs(next, "Second complete sentence."));
            await orch.PumpAsync();
            AssertEq(provider.Requests[0].VoiceId, "voice_a", "first voice");
            AssertEq(provider.Requests[1].VoiceId, "voice_b", "future voice");
        });
    }

    static void Case32_TimingLifecycleRecorded()
    {
        RunSync(async () =>
        {
            var orch = MateSpeechOrchestrator.ForTests(new MateFakeTtsProvider(), new MateFakeSpeechPlayer());
            var turnId = Guid.NewGuid();
            orch.OnTurnStarted(turnId);
            orch.OnTurnCompleted(turnId, Segs(turnId, "Timed complete sentence."));
            await orch.PumpAsync();
            var timing = orch.TimingsForTests[0];
            AssertTrue(timing.QueuedUtc != default && timing.SynthesisStartedUtc != default &&
                timing.SynthesisCompletedUtc != default && timing.PlaybackStartedUtc != default &&
                timing.PlaybackCompletedUtc != default, "missing timing lifecycle point");
        });
    }

    static void Case33_PrefetchDepthIsOne()
    {
        RunSync(async () =>
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var provider = new MateFakeTtsProvider();
            var player = new MateFakeSpeechPlayer { OnPlay = async ct => await gate.Task };
            var orch = MateSpeechOrchestrator.ForTests(provider, player);
            var turnId = Guid.NewGuid();
            orch.OnTurnStarted(turnId);
            orch.OnTurnCompleted(turnId, Segs(turnId, "One complete sentence. Two complete sentence. Three complete sentence."));
            var pump = orch.PumpAsync();
            for (int i = 0; i < 100 && provider.SynthesizeCalls < 2; i++) await Task.Delay(10);
            AssertTrue(provider.SynthesizeCalls == 2, "more than one future synthesis started");
            gate.TrySetResult(true);
            await pump;
        });
    }

    static void AssertJobTexts(MateSpeechOrchestrator orch, params string[] expected)
    {
        AssertTrue(orch.EnqueuedJobsForTests.Count == expected.Length,
            "job count expected=" + expected.Length + " actual=" + orch.EnqueuedJobsForTests.Count);
        for (int i = 0; i < expected.Length; i++)
            AssertEq(orch.EnqueuedJobsForTests[i].Text, expected[i], "speech job " + i);
    }

    static byte[] BuildStreamingPcm16Wav(short first, short second)
    {
        // fmt + odd-sized JUNK + data with the 0xFFFFFFFF streamed-length convention.
        var bytes = new byte[58];
        WriteFourCc(bytes, 0, "RIFF");
        WriteUInt32(bytes, 4, uint.MaxValue);
        WriteFourCc(bytes, 8, "WAVE");
        WriteFourCc(bytes, 12, "fmt ");
        WriteUInt32(bytes, 16, 16);
        WriteUInt16(bytes, 20, 1);
        WriteUInt16(bytes, 22, 1);
        WriteUInt32(bytes, 24, 24000);
        WriteUInt32(bytes, 28, 48000);
        WriteUInt16(bytes, 32, 2);
        WriteUInt16(bytes, 34, 16);
        WriteFourCc(bytes, 36, "JUNK");
        WriteUInt32(bytes, 40, 1);
        bytes[44] = 0;
        WriteFourCc(bytes, 46, "data");
        WriteUInt32(bytes, 50, uint.MaxValue);
        WriteUInt16(bytes, 54, unchecked((ushort)first));
        WriteUInt16(bytes, 56, unchecked((ushort)second));
        return bytes;
    }

    static void WriteFourCc(byte[] bytes, int offset, string value)
    {
        bytes[offset] = (byte)value[0]; bytes[offset + 1] = (byte)value[1];
        bytes[offset + 2] = (byte)value[2]; bytes[offset + 3] = (byte)value[3];
    }

    static void WriteUInt16(byte[] bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
    }

    static void WriteUInt32(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
        bytes[offset + 2] = (byte)(value >> 16);
        bytes[offset + 3] = (byte)(value >> 24);
    }

    static List<MateResponseSegment> Segs(Guid turnId, string prose)
    {
        return new List<MateResponseSegment>
        {
            new MateResponseSegment(turnId, 0, MateResponseSegmentKind.Prose, prose, prose, "", true, true),
        };
    }

    /// <summary>
    /// Run async verifier work without capturing UnitySynchronizationContext.
    /// MenuItem/.Wait on the Editor main thread otherwise deadlocks incomplete awaits
    /// (Task.Yield / Task.Delay / TCS) whose continuations need that context to pump.
    /// </summary>
    static void RunSync(Func<Task> work)
    {
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            var task = work();
            if (!task.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("async verify timed out");
            if (task.IsFaulted)
                throw task.Exception?.GetBaseException() ?? new Exception("async verify failed");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
#endif
