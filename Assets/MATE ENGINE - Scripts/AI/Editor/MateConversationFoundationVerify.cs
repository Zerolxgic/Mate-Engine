#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Deterministic EditMode verification for Slice 4 conversation/segment foundation (cases A–Q).
/// Menu: Mate Engine / Verify Conversation Foundation
/// </summary>
public static class MateConversationFoundationVerify
{
    const string MenuPath = "Mate Engine/Verify Conversation Foundation";
    const string LogPrefix = "[MateConversationFoundation]";

    [MenuItem(MenuPath)]
    public static void RunFromMenu()
    {
        var result = RunAll();
        if (result.Failed > 0)
        {
            Debug.LogError($"{LogPrefix} FAILED {result.Failed}/{result.Total}. See report: {result.ReportPath}");
            if (!Application.isBatchMode)
                EditorUtility.DisplayDialog("Mate Conversation Foundation", $"FAILED {result.Failed}/{result.Total}\n{result.ReportPath}", "OK");
        }
        else
        {
            Debug.Log($"{LogPrefix} PASS {result.Total}/{result.Total}. Report: {result.ReportPath}");
            if (!Application.isBatchMode)
                EditorUtility.DisplayDialog("Mate Conversation Foundation", $"PASS {result.Total}/{result.Total}", "OK");
        }
    }

    public static VerifyResult RunAll()
    {
        var cases = new List<(string id, Action test)>
        {
            ("A", CaseA_PlainProse),
            ("B", CaseB_SplitProse),
            ("C", CaseC_InlineBackticks),
            ("D", CaseD_CompleteFenceWithLanguage),
            ("E", CaseE_HostileFenceSplit),
            ("F", CaseF_CharByCharFence),
            ("G", CaseG_UnlabeledFence),
            ("H", CaseH_EmptyFence),
            ("I", CaseI_MultipleFences),
            ("J", CaseJ_BackticksInsideCode),
            ("K", CaseK_UnclosedOnComplete),
            ("L", CaseL_CancelDuringProse),
            ("M", CaseM_CancelInsideCode),
            ("N", CaseN_FailAfterPartial),
            ("O", CaseO_RepeatedTerminal),
            ("P", CaseP_TurnIsolationLateChunk),
            ("Q", CaseQ_LosslessCorpus),
        };

        var sb = new StringBuilder();
        sb.AppendLine("# Mate Conversation Foundation Verification");
        sb.AppendLine($"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        int failed = 0;
        foreach (var (id, test) in cases)
        {
            try
            {
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
        }

        sb.AppendLine();
        sb.AppendLine(failed == 0 ? $"RESULT: PASS ({cases.Count}/{cases.Count})" : $"RESULT: FAIL ({failed}/{cases.Count} failed)");

        string reportDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "reports"));
        Directory.CreateDirectory(reportDir);
        string reportPath = Path.Combine(reportDir, "2026-09-01-Mate-Engine-Slice-4-Foundation-Verify.md");
        File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);

        return new VerifyResult
        {
            Total = cases.Count,
            Failed = failed,
            ReportPath = reportPath,
        };
    }

    public struct VerifyResult
    {
        public int Total;
        public int Failed;
        public string ReportPath;
    }

    static MateConversationTurn RunningTurn(params string[] chunks)
    {
        var turn = new MateConversationTurn();
        if (!turn.Start()) throw new Exception("Start failed");
        foreach (var c in chunks)
        {
            if (!turn.AppendAssistantChunk(c)) throw new Exception("Append rejected while Running");
        }
        return turn;
    }

    static void AssertRaw(MateConversationTurn turn, string expected)
    {
        string raw = turn.GetRawResponseText();
        if (raw != expected)
            throw new Exception($"Raw mismatch.\nExpected ({expected.Length}): {Show(expected)}\nActual ({raw.Length}): {Show(raw)}");
    }

    static void AssertKinds(MateConversationTurn turn, params MateResponseSegmentKind[] kinds)
    {
        var segs = turn.GetSegmentsSnapshot();
        if (segs.Count != kinds.Length)
            throw new Exception($"Segment count {segs.Count} != {kinds.Length}");
        for (int i = 0; i < kinds.Length; i++)
        {
            if (segs[i].Kind != kinds[i])
                throw new Exception($"Segment[{i}] kind {segs[i].Kind} != {kinds[i]}");
            if (segs[i].TurnId != turn.TurnId)
                throw new Exception($"Segment[{i}] TurnId mismatch");
            if (segs[i].Sequence != i)
                throw new Exception($"Segment[{i}] Sequence {segs[i].Sequence} != {i}");
            if (!segs[i].IsFinalized)
                throw new Exception($"Segment[{i}] not finalized");
        }
    }

    static string Show(string s)
    {
        if (s == null) return "<null>";
        return s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }

    static void CaseA_PlainProse()
    {
        const string input = "Hello world.";
        var turn = RunningTurn(input);
        if (!turn.Complete()) throw new Exception("Complete failed");
        AssertKinds(turn, MateResponseSegmentKind.Prose);
        AssertRaw(turn, input);
        if (turn.GetSegmentsSnapshot()[0].Text != input) throw new Exception("Prose Text mismatch");
        if (turn.State != MateConversationTurn.TurnState.Completed) throw new Exception("State");
    }

    static void CaseB_SplitProse()
    {
        var turn = RunningTurn("Hel", "lo ", "wor", "ld.");
        turn.Complete();
        AssertKinds(turn, MateResponseSegmentKind.Prose);
        AssertRaw(turn, "Hello world.");
    }

    static void CaseC_InlineBackticks()
    {
        const string input = "Run `git status` next.";
        var turn = RunningTurn(input);
        turn.Complete();
        AssertKinds(turn, MateResponseSegmentKind.Prose);
        AssertRaw(turn, input);
    }

    static void CaseD_CompleteFenceWithLanguage()
    {
        string input = "Here is code:\n```python\nprint(\"hello\")\n```\nDone.";
        var turn = RunningTurn(input);
        turn.Complete();
        AssertKinds(turn, MateResponseSegmentKind.Prose, MateResponseSegmentKind.CodeBlock, MateResponseSegmentKind.Prose);
        var segs = turn.GetSegmentsSnapshot();
        if (segs[1].Language != "python") throw new Exception("language");
        if (!segs[1].FenceClosed) throw new Exception("FenceClosed");
        if (segs[1].Text != "print(\"hello\")\n") throw new Exception("code Text: " + Show(segs[1].Text));
        AssertRaw(turn, input);
    }

    static void CaseE_HostileFenceSplit()
    {
        var turn = RunningTurn(
            "Here is some code:\n`",
            "``py",
            "thon\nprint('hello",
            "')\n``",
            "`\nDone.");
        turn.Complete();
        AssertKinds(turn, MateResponseSegmentKind.Prose, MateResponseSegmentKind.CodeBlock, MateResponseSegmentKind.Prose);
        var segs = turn.GetSegmentsSnapshot();
        if (segs[1].Language != "python") throw new Exception("language=" + segs[1].Language);
        if (!segs[1].FenceClosed) throw new Exception("FenceClosed");
        if (segs[1].Text.Contains("```")) throw new Exception("fence leaked into Text");
        if (segs[1].Text != "print('hello')\n") throw new Exception("code Text=" + Show(segs[1].Text));
        AssertRaw(turn, "Here is some code:\n```python\nprint('hello')\n```\nDone.");
    }

    static void CaseF_CharByCharFence()
    {
        var turn = RunningTurn("`", "`", "`", "powershell\n", "Get-ChildItem\n", "`", "`", "`");
        turn.Complete();
        AssertKinds(turn, MateResponseSegmentKind.CodeBlock);
        var seg = turn.GetSegmentsSnapshot()[0];
        if (seg.Language != "powershell") throw new Exception("lang");
        if (!seg.FenceClosed) throw new Exception("closed");
        if (seg.Text != "Get-ChildItem\n") throw new Exception("text=" + Show(seg.Text));
        AssertRaw(turn, "```powershell\nGet-ChildItem\n```");
    }

    static void CaseG_UnlabeledFence()
    {
        string input = "```\nraw text\n```";
        var turn = RunningTurn(input);
        turn.Complete();
        AssertKinds(turn, MateResponseSegmentKind.CodeBlock);
        var seg = turn.GetSegmentsSnapshot()[0];
        if (!string.IsNullOrEmpty(seg.Language)) throw new Exception("lang should be empty");
        if (seg.Text != "raw text\n") throw new Exception("text");
        AssertRaw(turn, input);
    }

    static void CaseH_EmptyFence()
    {
        string input = "```text\n```";
        var turn = RunningTurn(input);
        turn.Complete();
        AssertKinds(turn, MateResponseSegmentKind.CodeBlock);
        var seg = turn.GetSegmentsSnapshot()[0];
        if (seg.Language != "text") throw new Exception("lang");
        if (seg.Text != "") throw new Exception("empty body expected, got " + Show(seg.Text));
        if (!seg.FenceClosed) throw new Exception("closed");
        AssertRaw(turn, input);
    }

    static void CaseI_MultipleFences()
    {
        string input = "A\n```a\n1\n```\nB\n```b\n2\n```\nC";
        var turn = RunningTurn(input);
        turn.Complete();
        AssertKinds(turn,
            MateResponseSegmentKind.Prose,
            MateResponseSegmentKind.CodeBlock,
            MateResponseSegmentKind.Prose,
            MateResponseSegmentKind.CodeBlock,
            MateResponseSegmentKind.Prose);
        var segs = turn.GetSegmentsSnapshot();
        if (segs[1].Language != "a" || segs[3].Language != "b") throw new Exception("langs");
        AssertRaw(turn, input);
    }

    static void CaseJ_BackticksInsideCode()
    {
        string input = "```js\nconst s = `x`;\nlet t = ``;\n```";
        var turn = RunningTurn(input);
        turn.Complete();
        AssertKinds(turn, MateResponseSegmentKind.CodeBlock);
        var seg = turn.GetSegmentsSnapshot()[0];
        if (!seg.FenceClosed) throw new Exception("closed");
        if (!seg.Text.Contains("`x`") || !seg.Text.Contains("``")) throw new Exception("inner ticks lost");
        AssertRaw(turn, input);
    }

    static void CaseK_UnclosedOnComplete()
    {
        string input = "Start:\n```python\nprint(\"partial\")";
        var turn = RunningTurn(input);
        turn.Complete();
        AssertKinds(turn, MateResponseSegmentKind.Prose, MateResponseSegmentKind.CodeBlock);
        var segs = turn.GetSegmentsSnapshot();
        if (segs[1].FenceClosed) throw new Exception("should be unclosed");
        if (turn.State != MateConversationTurn.TurnState.Completed) throw new Exception("state");
        AssertRaw(turn, input);
    }

    static void CaseL_CancelDuringProse()
    {
        var turn = RunningTurn("partial prose");
        if (!turn.Cancel()) throw new Exception("Cancel failed");
        if (turn.State != MateConversationTurn.TurnState.Cancelled) throw new Exception("state");
        AssertKinds(turn, MateResponseSegmentKind.Prose);
        AssertRaw(turn, "partial prose");
        if (turn.AppendAssistantChunk("LATE")) throw new Exception("late append accepted");
        AssertRaw(turn, "partial prose");
    }

    static void CaseM_CancelInsideCode()
    {
        var turn = RunningTurn("```python\nprint(1");
        turn.Cancel();
        if (turn.State != MateConversationTurn.TurnState.Cancelled) throw new Exception("state");
        AssertKinds(turn, MateResponseSegmentKind.CodeBlock);
        var seg = turn.GetSegmentsSnapshot()[0];
        if (seg.FenceClosed) throw new Exception("FenceClosed should be false");
        if (turn.AppendAssistantChunk(")\n```")) throw new Exception("late close accepted");
        AssertRaw(turn, "```python\nprint(1");
    }

    static void CaseN_FailAfterPartial()
    {
        var turn = RunningTurn("oops ");
        turn.Fail("boom");
        if (turn.State != MateConversationTurn.TurnState.Failed) throw new Exception("state");
        AssertRaw(turn, "oops ");
        if (turn.AppendAssistantChunk("more")) throw new Exception("late append");
        AssertRaw(turn, "oops ");
    }

    static void CaseO_RepeatedTerminal()
    {
        var a = RunningTurn("x");
        a.Cancel();
        if (a.Cancel()) throw new Exception("second Cancel should be false");
        AssertRaw(a, "x");

        var b = RunningTurn("y");
        b.Complete();
        if (b.Cancel()) throw new Exception("Cancel after Complete should be false");
        AssertRaw(b, "y");

        var c = RunningTurn("z");
        c.Fail("e");
        if (c.Complete()) throw new Exception("Complete after Fail should be false");
        AssertRaw(c, "z");
    }

    static void CaseP_TurnIsolationLateChunk()
    {
        var turnA = new MateConversationTurn();
        turnA.Start();
        turnA.AppendAssistantChunk("A-partial");
        turnA.Cancel();

        var turnB = new MateConversationTurn();
        turnB.Start();
        turnB.AppendAssistantChunk("B-content");

        if (turnA.AppendAssistantChunk("LATE-A")) throw new Exception("A accepted late");
        turnB.Complete();

        AssertRaw(turnA, "A-partial");
        AssertRaw(turnB, "B-content");
        if (turnA.TurnId == turnB.TurnId) throw new Exception("TurnIds not unique");
        if (turnB.GetSegmentsSnapshot()[0].TurnId != turnB.TurnId) throw new Exception("B segment ownership");
    }

    static void CaseQ_LosslessCorpus()
    {
        string[] corpus =
        {
            "Hello world.",
            "Run `git status` next.",
            "Here is code:\n```python\nprint(\"hello\")\n```\nDone.",
            "```\nraw text\n```",
            "```text\n```",
            "A\n```a\n1\n```\nB\n```b\n2\n```\nC",
            "```js\nconst s = `x`;\nlet t = ``;\n```",
            "Start:\n```python\nprint(\"partial\")",
            "",
            "line1\nline2\nline3",
            "```powershell\nGet-ChildItem\n```",
        };

        foreach (string input in corpus)
        {
            var turn = new MateConversationTurn();
            turn.Start();
            // Character-split stress + whole-string path
            if (input.Length == 0)
            {
                turn.AppendAssistantChunk("");
            }
            else if (input.Length < 8)
            {
                foreach (char ch in input)
                    turn.AppendAssistantChunk(ch.ToString());
            }
            else
            {
                turn.AppendAssistantChunk(input.Substring(0, input.Length / 2));
                turn.AppendAssistantChunk(input.Substring(input.Length / 2));
            }
            turn.Complete();
            AssertRaw(turn, input);

            // Also verify Concat(RawText) == GetRawResponseText
            var segs = turn.GetSegmentsSnapshot();
            var concat = new StringBuilder();
            foreach (var s in segs) concat.Append(s.RawText);
            if (concat.ToString() != input)
                throw new Exception("Concat RawText mismatch for " + Show(input));
        }
    }
}
#endif
