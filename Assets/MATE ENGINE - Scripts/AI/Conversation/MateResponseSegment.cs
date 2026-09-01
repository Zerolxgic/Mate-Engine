using System;

/// <summary>
/// Provider-neutral typed response segment for one conversation turn.
/// Speech/TTS policy is intentionally not represented here.
/// </summary>
public sealed class MateResponseSegment
{
    public Guid TurnId { get; }
    public int Sequence { get; }
    public MateResponseSegmentKind Kind { get; }
    public string Text { get; }
    public string RawText { get; }
    public string Language { get; }
    public bool IsFinalized { get; }
    public bool FenceClosed { get; }

    public MateResponseSegment(
        Guid turnId,
        int sequence,
        MateResponseSegmentKind kind,
        string text,
        string rawText,
        string language,
        bool isFinalized,
        bool fenceClosed)
    {
        TurnId = turnId;
        Sequence = sequence;
        Kind = kind;
        Text = text ?? "";
        RawText = rawText ?? "";
        Language = language ?? "";
        IsFinalized = isFinalized;
        FenceClosed = fenceClosed;
    }

    public MateResponseSegment WithFinalized(bool isFinalized, bool? fenceClosed = null)
    {
        return new MateResponseSegment(
            TurnId,
            Sequence,
            Kind,
            Text,
            RawText,
            Language,
            isFinalized,
            fenceClosed ?? FenceClosed);
    }
}

public enum MateResponseSegmentKind
{
    Prose = 0,
    CodeBlock = 1,
    Reasoning = 2,
    Tool = 3,
    Control = 4,
}
