using System;
using System.Collections.Generic;

/// <summary>
/// Provider-neutral chat presentation types for the detached technical-chat surface.
/// Distinct from provider request history.
/// </summary>
public enum MateChatSpeaker
{
    User = 0,
    Assistant = 1,
    System = 2,
}

public enum MateChatEntryState
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Cancelled = 3,
    Failed = 4,
}

public sealed class MateChatSegmentView
{
    public MateResponseSegmentKind Kind;
    public string Text = "";
    public string Language = "";
    public bool FenceClosed;
    public string SafeHtml = ""; // pre-rendered safe HTML for prose; empty for code (UI uses Text)
}

public sealed class MateChatTranscriptEntry
{
    public Guid EntryId;
    public Guid TurnId;
    public MateChatSpeaker Speaker;
    public MateChatEntryState State;
    public string PlainText = "";
    public string FailureMessage = "";
    public readonly List<MateChatSegmentView> Segments = new List<MateChatSegmentView>();
    public long Sequence;
}

/// <summary>
/// Centralized presentation theme variables (no user-facing editor in Slice 5).
/// </summary>
public sealed class MateTechnicalChatTheme
{
    public string FontFamily = "Segoe UI, system-ui, sans-serif";
    public string CodeFontFamily = "Cascadia Code, Consolas, ui-monospace, monospace";
    public int FontSizePx = 14;
    public int CodeFontSizePx = 13;
    public int MessageSpacingPx = 12;
    public int MaxContentWidthPx = 860;
    public string Background = "#12141a";
    public string Panel = "#1a1e27";
    public string UserBubble = "#2a4a6e";
    public string AssistantBubble = "#232833";
    public string CodeBackground = "#0d1117";
    public string Text = "#e8eaed";
    public string Muted = "#9aa0a6";
    public string Accent = "#7aa2f7";
    public string Border = "#2f3643";
    public string Danger = "#f07178";
}
