using System;
using System.Collections.Generic;

/// <summary>
/// Provider-neutral conversation turn: one user-request / assistant-response lifecycle
/// with typed incremental segmentation and terminal-state safety.
/// </summary>
public sealed class MateConversationTurn
{
    public enum TurnState
    {
        Created = 0,
        Running = 1,
        Completed = 2,
        Cancelled = 3,
        Failed = 4,
    }

    readonly MateResponseSegmenter segmenter;
    string failureMessage;

    public Guid TurnId { get; }
    public TurnState State { get; private set; }
    public string FailureMessage => failureMessage;

    public bool IsTerminal =>
        State == TurnState.Completed ||
        State == TurnState.Cancelled ||
        State == TurnState.Failed;

    public MateConversationTurn()
    {
        TurnId = Guid.NewGuid();
        State = TurnState.Created;
        segmenter = new MateResponseSegmenter(TurnId);
    }

    /// <summary>Test/helper factory with an explicit ID (still immutable after construction).</summary>
    public MateConversationTurn(Guid turnId)
    {
        if (turnId == Guid.Empty) throw new ArgumentException("TurnId must be non-empty.", nameof(turnId));
        TurnId = turnId;
        State = TurnState.Created;
        segmenter = new MateResponseSegmenter(TurnId);
    }

    public bool Start()
    {
        if (State != TurnState.Created) return false;
        State = TurnState.Running;
        return true;
    }

    public bool AppendAssistantChunk(string chunk)
    {
        if (State != TurnState.Running) return false;
        return segmenter.Append(chunk ?? "");
    }

    public bool AppendReasoningChunk(string chunk)
    {
        if (State != TurnState.Running) return false;
        return segmenter.AppendReasoning(chunk ?? "");
    }

    public bool Complete()
    {
        if (State != TurnState.Running) return false;
        segmenter.Finish(freeze: true);
        State = TurnState.Completed;
        return true;
    }

    public bool Cancel()
    {
        if (IsTerminal) return false;
        if (State == TurnState.Created)
        {
            segmenter.Finish(freeze: true);
            State = TurnState.Cancelled;
            return true;
        }

        // Running
        segmenter.Finish(freeze: true);
        State = TurnState.Cancelled;
        return true;
    }

    public bool Fail(string message)
    {
        if (IsTerminal) return false;
        failureMessage = TruncateFailure(message);
        if (State == TurnState.Created)
        {
            segmenter.Finish(freeze: true);
            State = TurnState.Failed;
            return true;
        }

        segmenter.Finish(freeze: true);
        State = TurnState.Failed;
        return true;
    }

    public IReadOnlyList<MateResponseSegment> GetSegmentsSnapshot()
    {
        return segmenter.GetSegmentsSnapshot();
    }

    public string GetRawResponseText()
    {
        return segmenter.GetRawText();
    }

    static string TruncateFailure(string message)
    {
        if (string.IsNullOrEmpty(message)) return "";
        const int max = 500;
        return message.Length <= max ? message : message.Substring(0, max) + "...";
    }
}
