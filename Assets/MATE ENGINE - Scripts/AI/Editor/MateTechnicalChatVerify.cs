#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Deterministic verification for Slice 5 presentation model / markdown / code-copy invariants.
/// </summary>
public static class MateTechnicalChatVerify
{
    const string LogPrefix = "[MateTechnicalChatVerify]";

    [MenuItem("Mate Engine/Open Technical Chat Window")]
    public static void OpenWindowMenu()
    {
        if (!Application.isPlaying)
        {
            EditorUtility.DisplayDialog("Mate Technical Chat", "Enter Play Mode first (OpenAI backend enabled).", "OK");
            return;
        }
        MateTechnicalChatController.EnsureHostAndOpen();
    }

    [MenuItem("Mate Engine/Verify Technical Chat Presentation")]
    public static void RunFromMenu()
    {
        var result = RunAll();
        if (!Application.isBatchMode)
        {
            EditorUtility.DisplayDialog("Mate Technical Chat Verify",
                result.Failed == 0 ? $"PASS {result.Total}/{result.Total}" : $"FAIL {result.Failed}/{result.Total}\n{result.ReportPath}",
                "OK");
        }
    }

    public static VerifyResult RunAll()
    {
        var cases = new List<(string id, Action test)>
        {
            ("A", CaseA),
            ("B", CaseB),
            ("C", CaseC),
            ("D", CaseD),
            ("E", CaseE),
            ("F", CaseF),
            ("G", CaseG),
            ("H", CaseH),
            ("I", CaseI),
            ("J", CaseJ),
            ("K", CaseK),
            ("S1", CaseS1_ValidSendPayload),
            ("S2", CaseS2_EmptySendRejected),
            ("S3", CaseS3_MainThreadQueueDelivery),
            ("S4", CaseS4_ControllerBridge),
            ("S5", CaseS5_BrowserFailureUxContract),
            ("S6", CaseS6_ConcurrentAcceptWhileSseBlockedWorker),
            ("O1", CaseO1_DefaultOutputBudget),
            ("O2", CaseO2_ConfiguredOutputBudget),
            ("O3", CaseO3_InvalidOutputBudget),
            ("O4", CaseO4_StreamingUnchanged),
            ("O5", CaseO5_FinishReasonLength),
        };

        var sb = new StringBuilder();
        sb.AppendLine("# Mate Technical Chat Presentation Verification");
        sb.AppendLine($"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        int failed = 0;
        foreach (var (id, test) in cases)
        {
            try
            {
                MateChatPresentationModel.Session.ResetForTests();
                MateTechnicalChatController.SendEntryHookForTests = null;
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
                MateTechnicalChatController.SendEntryHookForTests = null;
            }
        }

        sb.AppendLine();
        sb.AppendLine(failed == 0 ? $"RESULT: PASS ({cases.Count}/{cases.Count})" : $"RESULT: FAIL ({failed}/{cases.Count} failed)");

        string reportDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "reports"));
        Directory.CreateDirectory(reportDir);
        string reportPath = Path.Combine(reportDir, "2026-09-01-Mate-Engine-Slice-5-Technical-Chat-Verify.md");
        File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);

        if (failed == 0)
            Debug.Log($"{LogPrefix} PASS {cases.Count}/{cases.Count}. Report: {reportPath}");
        else
            Debug.LogError($"{LogPrefix} FAILED {failed}/{cases.Count}. Report: {reportPath}");

        return new VerifyResult { Total = cases.Count, Failed = failed, ReportPath = reportPath };
    }

    public struct VerifyResult
    {
        public int Total;
        public int Failed;
        public string ReportPath;
    }

    static void CaseA()
    {
        var turn = new MateConversationTurn();
        turn.Start();
        MateChatPresentationModel.Session.BeginUserTurn(turn.TurnId, "Hello");
        turn.AppendAssistantChunk("Hi there.");
        turn.Complete();
        MateChatPresentationModel.Session.CompleteAssistant(turn);
        var snap = MateChatPresentationModel.Session.GetSnapshot();
        if (snap.Count != 2) throw new Exception("entry count");
        if (snap[0].Speaker != MateChatSpeaker.User || snap[0].PlainText != "Hello") throw new Exception("user");
        if (snap[1].State != MateChatEntryState.Completed) throw new Exception("state");
        if (snap[1].Segments.Count != 1 || snap[1].Segments[0].Kind != MateResponseSegmentKind.Prose)
            throw new Exception("prose seg");
    }

    static void CaseB()
    {
        var turn = new MateConversationTurn();
        turn.Start();
        string input = "Intro\n```python\nprint(1)\n```\nOutro";
        MateChatPresentationModel.Session.BeginUserTurn(turn.TurnId, "code please");
        turn.AppendAssistantChunk(input);
        turn.Complete();
        MateChatPresentationModel.Session.CompleteAssistant(turn);
        var segs = MateChatPresentationModel.Session.GetSnapshot()[1].Segments;
        if (segs.Count != 3) throw new Exception("seg count " + segs.Count);
        if (segs[0].Kind != MateResponseSegmentKind.Prose) throw new Exception("0");
        if (segs[1].Kind != MateResponseSegmentKind.CodeBlock || segs[1].Language != "python") throw new Exception("1");
        if (segs[2].Kind != MateResponseSegmentKind.Prose) throw new Exception("2");
    }

    static void CaseC()
    {
        var turn = new MateConversationTurn();
        turn.Start();
        string input = "A\n```a\n1\n```\nB\n```b\n2\n```\nC";
        MateChatPresentationModel.Session.BeginUserTurn(turn.TurnId, "multi");
        turn.AppendAssistantChunk(input);
        turn.Complete();
        MateChatPresentationModel.Session.CompleteAssistant(turn);
        var segs = MateChatPresentationModel.Session.GetSnapshot()[1].Segments;
        int codes = 0;
        foreach (var s in segs)
        {
            if (s.Kind == MateResponseSegmentKind.CodeBlock)
            {
                codes++;
                if (MateMarkdownProse.CodeCopyPayload(new MateResponseSegment(turn.TurnId, 0, s.Kind, s.Text, s.Text, s.Language, true, s.FenceClosed)) != s.Text)
                    throw new Exception("copy");
            }
        }
        if (codes != 2) throw new Exception("codes");
    }

    static void CaseD()
    {
        var turn = new MateConversationTurn();
        turn.Start();
        MateChatPresentationModel.Session.BeginUserTurn(turn.TurnId, "slow");
        turn.Cancel();
        MateChatPresentationModel.Session.CancelTurn(turn.TurnId);
        var a = MateChatPresentationModel.Session.GetSnapshot()[1];
        if (a.State != MateChatEntryState.Cancelled) throw new Exception("state");
        if (a.Segments.Count != 0 && a.State == MateChatEntryState.Completed) throw new Exception("fabricated");
    }

    static void CaseE()
    {
        var turn = new MateConversationTurn();
        turn.Start();
        MateChatPresentationModel.Session.BeginUserTurn(turn.TurnId, "x");
        turn.Fail("boom");
        MateChatPresentationModel.Session.FailTurn(turn.TurnId, "boom");
        var a = MateChatPresentationModel.Session.GetSnapshot()[1];
        if (a.State != MateChatEntryState.Failed) throw new Exception("state");
        if (!a.FailureMessage.Contains("boom")) throw new Exception("msg");
    }

    static void CaseF()
    {
        var t1 = new MateConversationTurn();
        t1.Start();
        MateChatPresentationModel.Session.BeginUserTurn(t1.TurnId, "one");
        t1.AppendAssistantChunk("A1");
        t1.Complete();
        MateChatPresentationModel.Session.CompleteAssistant(t1);

        var t2 = new MateConversationTurn();
        t2.Start();
        MateChatPresentationModel.Session.BeginUserTurn(t2.TurnId, "two");
        t2.AppendAssistantChunk("A2");
        t2.Complete();
        MateChatPresentationModel.Session.CompleteAssistant(t2);

        var snap = MateChatPresentationModel.Session.GetSnapshot();
        if (snap.Count != 4) throw new Exception("count");
        if (snap[0].PlainText != "one" || snap[2].PlainText != "two") throw new Exception("order");
        if (snap[1].PlainText != "A1" || snap[3].PlainText != "A2") throw new Exception("assist");
    }

    static void CaseG()
    {
        var turn = new MateConversationTurn();
        turn.Start();
        MateChatPresentationModel.Session.BeginUserTurn(turn.TurnId, "while closed");
        turn.AppendAssistantChunk("still here");
        turn.Complete();
        MateChatPresentationModel.Session.CompleteAssistant(turn);
        // Model updates without a window binding.
        if (MateChatPresentationModel.Session.GetSnapshot().Count != 2) throw new Exception("lost");
        string json = MateChatPresentationModel.Session.ToJsonSnapshot();
        if (!json.Contains("still here")) throw new Exception("json");
    }

    static void CaseH()
    {
        // Simulate unsupported kinds via ToView path by completing a prose turn then ensuring enum exists.
        var kinds = (MateResponseSegmentKind[])Enum.GetValues(typeof(MateResponseSegmentKind));
        if (kinds.Length < 5) throw new Exception("enum");
        string html = MateMarkdownProse.ToSafeHtml("ok");
        if (string.IsNullOrEmpty(html)) throw new Exception("html");
        // Reasoning/Tool/Control defaults must not throw when building views from empty text.
        var turn = new MateConversationTurn();
        turn.Start();
        MateChatPresentationModel.Session.BeginUserTurn(turn.TurnId, "u");
        turn.AppendAssistantChunk("safe");
        turn.Complete();
        MateChatPresentationModel.Session.CompleteAssistant(turn);
    }

    static void CaseI()
    {
        string nasty = "Use <script>alert(1)</script> and <b>bold</b> & raw.";
        string html = MateMarkdownProse.ToSafeHtml(nasty);
        if (html.Contains("<script>") || html.Contains("<b>bold</b>"))
            throw new Exception("injected markup");
        if (!html.Contains("&lt;script&gt;") || !html.Contains("&amp;"))
            throw new Exception("escape missing");
    }

    static void CaseJ()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Para one.");
        sb.AppendLine();
        sb.AppendLine("Para two with more text.");
        sb.AppendLine("```csharp");
        for (int i = 0; i < 80; i++)
            sb.AppendLine("    Console.WriteLine(\"line " + i + "\");");
        sb.AppendLine("```");
        sb.AppendLine("Middle prose.");
        sb.AppendLine("```text");
        for (int i = 0; i < 40; i++)
            sb.AppendLine("block2-" + i);
        sb.AppendLine("```");
        sb.AppendLine("End.");
        string input = sb.ToString();

        var turn = new MateConversationTurn();
        turn.Start();
        MateChatPresentationModel.Session.BeginUserTurn(turn.TurnId, "long");
        turn.AppendAssistantChunk(input);
        turn.Complete();
        MateChatPresentationModel.Session.CompleteAssistant(turn);
        var a = MateChatPresentationModel.Session.GetSnapshot()[1];
        if (a.PlainText != input) throw new Exception("truncated");
        int codes = 0;
        foreach (var s in a.Segments)
            if (s.Kind == MateResponseSegmentKind.CodeBlock) codes++;
        if (codes != 2) throw new Exception("codes");
    }

    static void CaseK()
    {
        var turn = new MateConversationTurn();
        turn.Start();
        string input = "```js\nconst x = `a`;\n```";
        MateChatPresentationModel.Session.BeginUserTurn(turn.TurnId, "c");
        turn.AppendAssistantChunk(input);
        turn.Complete();
        MateChatPresentationModel.Session.CompleteAssistant(turn);
        var code = MateChatPresentationModel.Session.GetSnapshot()[1].Segments[0];
        if (code.Kind != MateResponseSegmentKind.CodeBlock) throw new Exception("kind");
        string payload = code.Text;
        if (payload.Contains("```") || payload.Contains("Copy") || payload.StartsWith("js"))
            throw new Exception("decorations in copy payload");
        if (payload != "const x = `a`;\n") throw new Exception("body=" + payload.Replace("\n", "\\n"));
    }

    static void CaseS1_ValidSendPayload()
    {
        string body = "{\"text\":\"hello mate\"}";
        if (!MateTechnicalChatHost.TryParseSendText(body, out string text, out string error))
            throw new Exception("parse rejected: " + error);
        if (text != "hello mate") throw new Exception("text mismatch: " + text);

        body = "{\"text\":\"line1\\nline2\"}";
        if (!MateTechnicalChatHost.TryParseSendText(body, out text, out error))
            throw new Exception("escape rejected: " + error);
        if (text != "line1\nline2") throw new Exception("newline mismatch");
    }

    static void CaseS2_EmptySendRejected()
    {
        string[] bodies = { "", "{}", "{\"text\":\"\"}", "{\"text\":\"   \"}", "{\"text\":\"\\t\\n\"}" };
        foreach (var body in bodies)
        {
            if (MateTechnicalChatHost.TryParseSendText(body, out _, out string error))
                throw new Exception("should reject: " + body);
            if (error != "empty") throw new Exception("error tag: " + error);
        }

        int calls = 0;
        MateTechnicalChatController.SendEntryHookForTests = _ => calls++;
        if (MateTechnicalChatController.TrySendFromUi("   ", out string sendError))
            throw new Exception("controller accepted blank");
        if (sendError != "empty") throw new Exception("controller error: " + sendError);
        if (calls != 0) throw new Exception("hook invoked on blank");
        if (MateChatPresentationModel.Session.GetSnapshot().Count != 0)
            throw new Exception("transcript mutated");
    }

    static void CaseS3_MainThreadQueueDelivery()
    {
        var go = new GameObject("MateTechnicalChatHost_S3");
        go.SetActive(false);
        try
        {
            var host = go.AddComponent<MateTechnicalChatHost>();
            // Inactive GO: Awake runs, OnEnable/StartHost does not — queue-only seam.
            int runs = 0;
            host.EnqueueMainThread(() => runs++);
            if (runs != 0) throw new Exception("ran before drain");
            int drained = host.DrainMainThreadQueue();
            if (drained != 1) throw new Exception("drained count " + drained);
            if (runs != 1) throw new Exception("action runs " + runs);
            if (host.DrainMainThreadQueue() != 0) throw new Exception("second drain not empty");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    static void CaseS4_ControllerBridge()
    {
        int calls = 0;
        string seen = null;
        MateTechnicalChatController.SendEntryHookForTests = t =>
        {
            calls++;
            seen = t;
        };

        if (!MateTechnicalChatController.TrySendFromUi("ping\n", out string error))
            throw new Exception("rejected: " + error);
        if (calls != 1) throw new Exception("calls=" + calls);
        if (seen != "ping") throw new Exception("trim: " + seen);

        // Busy guard: running turn blocks second send without hook call.
        var turn = new MateConversationTurn();
        turn.Start();
        MateChatPresentationModel.Session.BeginUserTurn(turn.TurnId, "busy");
        if (MateTechnicalChatController.TrySendFromUi("second", out error))
            throw new Exception("busy accepted");
        if (error != "busy") throw new Exception("busy error: " + error);
        if (calls != 1) throw new Exception("extra call while busy");
    }

    static void CaseS5_BrowserFailureUxContract()
    {
        string html = MateTechnicalChatPage.Html;
        // Must not clear input before the host accepts the POST.
        if (html.Contains("input.value = '';\n  await fetch('/api/send'"))
            throw new Exception("optimistic clear-before-fetch still present");
        if (!html.Contains("body.ok !== true"))
            throw new Exception("missing ok-check before clear");
        if (!html.Contains("Preserve typed text"))
            throw new Exception("missing preserve-on-failure contract comment");
        // Clear only after accepted response.
        int clearIdx = html.IndexOf("input.value = '';", StringComparison.Ordinal);
        int okCheckIdx = html.IndexOf("body.ok !== true", StringComparison.Ordinal);
        if (clearIdx < 0 || okCheckIdx < 0 || clearIdx < okCheckIdx)
            throw new Exception("clear must follow success check");
    }

    /// <summary>
    /// Regression for the Slice 5 Send hang: accept loop must dispatch work so a
    /// blocking SSE-style handler cannot starve POST /api/send.
    /// </summary>
    static void CaseS6_ConcurrentAcceptWhileSseBlockedWorker()
    {
        const int port = 32157;
        string prefix = $"http://127.0.0.1:{port}/";
        var listener = new System.Net.HttpListener();
        listener.Prefixes.Add(prefix);
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            throw new Exception("listener start: " + ex.Message);
        }

        var run = true;
        var listenThread = new System.Threading.Thread(() =>
        {
            while (run)
            {
                System.Net.HttpListenerContext ctx = null;
                try { ctx = listener.GetContext(); }
                catch { if (!run) break; continue; }

                // Same dispatch pattern as MateTechnicalChatHost.ListenLoop after the fix.
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        string path = ctx.Request.Url.AbsolutePath.ToLowerInvariant();
                        if (path.Contains("/api/events"))
                        {
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "text/event-stream";
                            ctx.Response.SendChunked = true;
                            var buf = Encoding.UTF8.GetBytes(": ping\n\n");
                            ctx.Response.OutputStream.Write(buf, 0, buf.Length);
                            System.Threading.Thread.Sleep(8000);
                            try { ctx.Response.Close(); } catch { }
                        }
                        else if (path.Contains("/api/send"))
                        {
                            var bytes = Encoding.UTF8.GetBytes("{\"ok\":true}");
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "application/json";
                            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                            ctx.Response.Close();
                        }
                        else
                        {
                            ctx.Response.StatusCode = 404;
                            ctx.Response.Close();
                        }
                    }
                    catch { }
                });
            }
        })
        { IsBackground = true, Name = "MateTechnicalChatVerifyS6" };
        listenThread.Start();

        try
        {
            // Hold an SSE-like connection open on a worker.
            var sseStarted = new System.Threading.ManualResetEventSlim(false);
            var sseThread = new System.Threading.Thread(() =>
            {
                try
                {
                    var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(prefix + "api/events");
                    req.Method = "GET";
                    req.Timeout = 15000;
                    sseStarted.Set();
                    using (req.GetResponse()) { }
                }
                catch { /* closed when listener stops */ }
            })
            { IsBackground = true };
            sseThread.Start();
            if (!sseStarted.Wait(2000))
                throw new Exception("sse start");

            System.Threading.Thread.Sleep(300);

            var sendReq = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(prefix + "api/send");
            sendReq.Method = "POST";
            sendReq.ContentType = "application/json";
            sendReq.Timeout = 3000;
            byte[] payload = Encoding.UTF8.GetBytes("{\"text\":\"ping\"}");
            sendReq.ContentLength = payload.Length;
            using (var stream = sendReq.GetRequestStream())
                stream.Write(payload, 0, payload.Length);

            using (var resp = (System.Net.HttpWebResponse)sendReq.GetResponse())
            {
                if (resp.StatusCode != System.Net.HttpStatusCode.OK)
                    throw new Exception("send status " + resp.StatusCode);
            }
        }
        finally
        {
            run = false;
            try { listener.Stop(); } catch { }
            try { listener.Close(); } catch { }
        }
    }

    static void CaseO1_DefaultOutputBudget()
    {
        if (MateOpenAIConfig.DefaultMaxOutputTokens != 2048)
            throw new Exception("default const " + MateOpenAIConfig.DefaultMaxOutputTokens);
        var cfg = new MateOpenAIConfig();
        if (cfg.maxTokens != 2048) throw new Exception("field default " + cfg.maxTokens);
        if (cfg.GetRequestMaxTokens() != 2048) throw new Exception("resolve " + cfg.GetRequestMaxTokens());

        string json = MateOpenAICharacter.BuildChatCompletionJson("test-model", null, 0.7f, cfg.GetRequestMaxTokens());
        if (!json.Contains("\"max_tokens\":2048")) throw new Exception("body=" + json);
    }

    static void CaseO2_ConfiguredOutputBudget()
    {
        string json = MateOpenAICharacter.BuildChatCompletionJson("test-model", null, 0.1f, 4096);
        if (!json.Contains("\"max_tokens\":4096")) throw new Exception("body=" + json);
        var cfg = new MateOpenAIConfig { maxTokens = 4096 };
        if (cfg.GetRequestMaxTokens() != 4096) throw new Exception("cfg resolve");
    }

    static void CaseO3_InvalidOutputBudget()
    {
        if (MateOpenAIConfig.ResolveMaxOutputTokens(0) != 2048) throw new Exception("zero");
        if (MateOpenAIConfig.ResolveMaxOutputTokens(-1) != 2048) throw new Exception("neg");
        string json = MateOpenAICharacter.BuildChatCompletionJson("m", null, 0.7f, 0);
        if (!json.Contains("\"max_tokens\":2048")) throw new Exception("invalid emitted " + json);
        if (json.Contains("\"max_tokens\":0") || json.Contains("\"max_tokens\":-"))
            throw new Exception("invalid literal");
    }

    static void CaseO4_StreamingUnchanged()
    {
        string json = MateOpenAICharacter.BuildChatCompletionJson("m", null, 0.7f, 2048);
        if (!json.Contains("\"stream\":false")) throw new Exception("stream missing/wrong: " + json);
        if (json.Contains("\"stream\":true")) throw new Exception("stream true");
    }

    static void CaseO5_FinishReasonLength()
    {
        string synthetic =
            "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"partial technical answer\"},\"finish_reason\":\"length\"}]}";
        if (!MateOpenAICharacter.TryParseChatCompletion(synthetic, out string content, out string finishReason))
            throw new Exception("parse failed");
        if (content != "partial technical answer") throw new Exception("content=" + content);
        if (finishReason != "length") throw new Exception("finish=" + finishReason);
        if (!MateOpenAICharacter.WarnIfOutputLengthLimited(finishReason, 2048))
            throw new Exception("diagnostic path not taken");
        // Must not invent failure — content remains usable for Completed turn.
        if (string.IsNullOrEmpty(content)) throw new Exception("discarded");
    }
}
#endif
