using System;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// In-memory provider-neutral transcript for the detached technical chat.
/// Session-scoped; survives window close/reopen; resets when Mate process stops.
/// </summary>
public sealed class MateChatPresentationModel
{
    public static MateChatPresentationModel Session { get; } = new MateChatPresentationModel();

    readonly object gate = new object();
    readonly List<MateChatTranscriptEntry> entries = new List<MateChatTranscriptEntry>();
    long nextSequence = 1;
    int revision;

    public MateTechnicalChatTheme Theme { get; } = new MateTechnicalChatTheme();

    /// <summary>Incremented on every mutation; UI hosts poll / SSE on changes.</summary>
    public int Revision
    {
        get { lock (gate) return revision; }
    }

    public event Action Changed;

    public IReadOnlyList<MateChatTranscriptEntry> GetSnapshot()
    {
        lock (gate)
        {
            var copy = new List<MateChatTranscriptEntry>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
                copy.Add(CloneEntry(entries[i]));
            return copy;
        }
    }

    public bool HasRunningTurn
    {
        get
        {
            lock (gate)
            {
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    var st = entries[i].State;
                    if (st == MateChatEntryState.Running || st == MateChatEntryState.Pending)
                        return true;
                }
                return false;
            }
        }
    }

    public Guid? ActiveTurnId
    {
        get
        {
            lock (gate)
            {
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    var e = entries[i];
                    if (e.Speaker == MateChatSpeaker.Assistant &&
                        (e.State == MateChatEntryState.Running || e.State == MateChatEntryState.Pending))
                        return e.TurnId;
                }
                return null;
            }
        }
    }

    public void BeginUserTurn(Guid turnId, string userText)
    {
        if (turnId == Guid.Empty) return;
        string text = userText ?? "";

        lock (gate)
        {
            entries.Add(new MateChatTranscriptEntry
            {
                EntryId = Guid.NewGuid(),
                TurnId = turnId,
                Speaker = MateChatSpeaker.User,
                State = MateChatEntryState.Completed,
                PlainText = text,
                Sequence = nextSequence++,
            });

            entries.Add(new MateChatTranscriptEntry
            {
                EntryId = Guid.NewGuid(),
                TurnId = turnId,
                Speaker = MateChatSpeaker.Assistant,
                State = MateChatEntryState.Running,
                PlainText = "",
                Sequence = nextSequence++,
            });
            revision++;
        }
        RaiseChanged();
    }

    public void CompleteAssistant(MateConversationTurn turn)
    {
        if (turn == null) return;
        lock (gate)
        {
            var entry = FindAssistant(turn.TurnId);
            if (entry == null) return;
            entry.State = MateChatEntryState.Completed;
            entry.FailureMessage = "";
            entry.Segments.Clear();
            var segs = turn.GetSegmentsSnapshot();
            for (int i = 0; i < segs.Count; i++)
                entry.Segments.Add(ToView(segs[i]));
            entry.PlainText = turn.GetRawResponseText();
            revision++;
        }
        RaiseChanged();
    }

    public void CancelTurn(Guid turnId)
    {
        lock (gate)
        {
            var entry = FindAssistant(turnId);
            if (entry == null) return;
            if (entry.State == MateChatEntryState.Completed ||
                entry.State == MateChatEntryState.Cancelled ||
                entry.State == MateChatEntryState.Failed)
                return;
            entry.State = MateChatEntryState.Cancelled;
            entry.PlainText = entry.PlainText ?? "";
            revision++;
        }
        RaiseChanged();
    }

    public void FailTurn(Guid turnId, string message)
    {
        lock (gate)
        {
            var entry = FindAssistant(turnId);
            if (entry == null) return;
            if (entry.State == MateChatEntryState.Completed) return;
            entry.State = MateChatEntryState.Failed;
            entry.FailureMessage = Truncate(message, 500);
            entry.PlainText = entry.FailureMessage;
            entry.Segments.Clear();
            revision++;
        }
        RaiseChanged();
    }

    public string ToJsonSnapshot()
    {
        var snap = GetSnapshot();
        var theme = Theme;
        var sb = new StringBuilder(1024);
        sb.Append('{');
        sb.Append("\"revision\":").Append(Revision).Append(',');
        sb.Append("\"hasRunning\":").Append(HasRunningTurn ? "true" : "false").Append(',');
        sb.Append("\"theme\":{");
        AppendJson(sb, "fontFamily", theme.FontFamily); sb.Append(',');
        AppendJson(sb, "codeFontFamily", theme.CodeFontFamily); sb.Append(',');
        sb.Append("\"fontSizePx\":").Append(theme.FontSizePx).Append(',');
        sb.Append("\"codeFontSizePx\":").Append(theme.CodeFontSizePx).Append(',');
        sb.Append("\"messageSpacingPx\":").Append(theme.MessageSpacingPx).Append(',');
        sb.Append("\"maxContentWidthPx\":").Append(theme.MaxContentWidthPx).Append(',');
        AppendJson(sb, "background", theme.Background); sb.Append(',');
        AppendJson(sb, "panel", theme.Panel); sb.Append(',');
        AppendJson(sb, "userBubble", theme.UserBubble); sb.Append(',');
        AppendJson(sb, "assistantBubble", theme.AssistantBubble); sb.Append(',');
        AppendJson(sb, "codeBackground", theme.CodeBackground); sb.Append(',');
        AppendJson(sb, "text", theme.Text); sb.Append(',');
        AppendJson(sb, "muted", theme.Muted); sb.Append(',');
        AppendJson(sb, "accent", theme.Accent); sb.Append(',');
        AppendJson(sb, "border", theme.Border); sb.Append(',');
        AppendJson(sb, "danger", theme.Danger);
        sb.Append("},\"entries\":[");
        for (int i = 0; i < snap.Count; i++)
        {
            if (i > 0) sb.Append(',');
            AppendEntryJson(sb, snap[i]);
        }
        sb.Append("]}");
        return sb.ToString();
    }

    // --- test helpers ---

    public void ResetForTests()
    {
        lock (gate)
        {
            entries.Clear();
            nextSequence = 1;
            revision++;
        }
        RaiseChanged();
    }

    MateChatTranscriptEntry FindAssistant(Guid turnId)
    {
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var e = entries[i];
            if (e.TurnId == turnId && e.Speaker == MateChatSpeaker.Assistant)
                return e;
        }
        return null;
    }

    static MateChatSegmentView ToView(MateResponseSegment seg)
    {
        var view = new MateChatSegmentView
        {
            Kind = seg.Kind,
            Text = seg.Text ?? "",
            Language = seg.Language ?? "",
            FenceClosed = seg.FenceClosed,
        };
        switch (seg.Kind)
        {
            case MateResponseSegmentKind.Prose:
                view.SafeHtml = MateMarkdownProse.ToSafeHtml(seg.Text);
                break;
            case MateResponseSegmentKind.CodeBlock:
                view.SafeHtml = "";
                break;
            case MateResponseSegmentKind.Reasoning:
                // Hidden from ordinary assistant prose by default.
                view.SafeHtml = "";
                view.Text = "";
                break;
            case MateResponseSegmentKind.Tool:
                view.SafeHtml = "<div class=\"seg-tool\">" + MateMarkdownProse.EscapeHtml(seg.Text ?? "") + "</div>";
                break;
            case MateResponseSegmentKind.Control:
            default:
                view.SafeHtml = "";
                view.Text = "";
                break;
        }
        return view;
    }

    static MateChatTranscriptEntry CloneEntry(MateChatTranscriptEntry src)
    {
        var e = new MateChatTranscriptEntry
        {
            EntryId = src.EntryId,
            TurnId = src.TurnId,
            Speaker = src.Speaker,
            State = src.State,
            PlainText = src.PlainText,
            FailureMessage = src.FailureMessage,
            Sequence = src.Sequence,
        };
        for (int i = 0; i < src.Segments.Count; i++)
        {
            var s = src.Segments[i];
            e.Segments.Add(new MateChatSegmentView
            {
                Kind = s.Kind,
                Text = s.Text,
                Language = s.Language,
                FenceClosed = s.FenceClosed,
                SafeHtml = s.SafeHtml,
            });
        }
        return e;
    }

    void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { /* host must not break model */ }
    }

    static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, max) + "...";
    }

    static void AppendEntryJson(StringBuilder sb, MateChatTranscriptEntry e)
    {
        sb.Append('{');
        AppendJson(sb, "entryId", e.EntryId.ToString()); sb.Append(',');
        AppendJson(sb, "turnId", e.TurnId.ToString()); sb.Append(',');
        AppendJson(sb, "speaker", e.Speaker.ToString()); sb.Append(',');
        AppendJson(sb, "state", e.State.ToString()); sb.Append(',');
        AppendJson(sb, "plainText", e.PlainText); sb.Append(',');
        AppendJson(sb, "failureMessage", e.FailureMessage); sb.Append(',');
        sb.Append("\"sequence\":").Append(e.Sequence).Append(',');
        sb.Append("\"segments\":[");
        for (int i = 0; i < e.Segments.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var s = e.Segments[i];
            sb.Append('{');
            AppendJson(sb, "kind", s.Kind.ToString()); sb.Append(',');
            AppendJson(sb, "text", s.Text); sb.Append(',');
            AppendJson(sb, "language", s.Language); sb.Append(',');
            sb.Append("\"fenceClosed\":").Append(s.FenceClosed ? "true" : "false").Append(',');
            AppendJson(sb, "safeHtml", s.SafeHtml);
            sb.Append('}');
        }
        sb.Append("]}");
    }

    static void AppendJson(StringBuilder sb, string key, string value)
    {
        sb.Append('"').Append(key).Append("\":\"").Append(EscapeJson(value)).Append('"');
    }

    static string EscapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t");
    }
}
