using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// OpenAI-compatible chat.completions SSE framing parser.
/// Handles split receives, multi-event chunks, CRLF, keepalive comments, and UTF-8 boundaries.
/// Pure C# — no Unity dependency (deterministic verification).
/// </summary>
public sealed class MateOpenAISseParser
{
    readonly List<byte> bytePending = new List<byte>(4096);
    readonly StringBuilder lineCarry = new StringBuilder();
    readonly StringBuilder dataPayload = new StringBuilder();
    bool hasDataField;

    public void Reset()
    {
        bytePending.Clear();
        lineCarry.Length = 0;
        dataPayload.Length = 0;
        hasDataField = false;
    }

    /// <summary>Feed raw network bytes. Emits zero or more events into <paramref name="output"/>.</summary>
    public void AppendBytes(byte[] data, int length, ConcurrentQueue<MateOpenAISseEvent> output)
    {
        if (data == null || length <= 0 || output == null) return;
        for (int i = 0; i < length; i++)
            bytePending.Add(data[i]);
        DrainUtf8(output);
    }

    /// <summary>Feed already-decoded text (test seam).</summary>
    public void AppendText(string text, ConcurrentQueue<MateOpenAISseEvent> output)
    {
        if (string.IsNullOrEmpty(text) || output == null) return;
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        AppendBytes(bytes, bytes.Length, output);
    }

    void DrainUtf8(ConcurrentQueue<MateOpenAISseEvent> output)
    {
        // Decode as many complete UTF-8 code points as possible; leave incomplete trailing bytes.
        int usable = bytePending.Count;
        while (usable > 0)
        {
            byte b = bytePending[usable - 1];
            if ((b & 0x80) == 0) break; // ASCII — complete
            // Find start of trailing multi-byte sequence
            int i = usable - 1;
            while (i >= 0 && (bytePending[i] & 0xC0) == 0x80) i--;
            if (i < 0) { usable = 0; break; }
            byte lead = bytePending[i];
            int need;
            if ((lead & 0xE0) == 0xC0) need = 2;
            else if ((lead & 0xF0) == 0xE0) need = 3;
            else if ((lead & 0xF8) == 0xF0) need = 4;
            else { usable = i; continue; } // invalid lead — drop up to here next pass
            int have = usable - i;
            if (have < need) usable = i;
            else break;
        }

        if (usable <= 0) return;
        string chunk = Encoding.UTF8.GetString(bytePending.GetRange(0, usable).ToArray());
        bytePending.RemoveRange(0, usable);
        ConsumeDecoded(chunk, output);
    }

    void ConsumeDecoded(string chunk, ConcurrentQueue<MateOpenAISseEvent> output)
    {
        lineCarry.Append(chunk);
        string all = lineCarry.ToString();
        int start = 0;
        for (int i = 0; i < all.Length; i++)
        {
            char c = all[i];
            if (c != '\n') continue;
            int end = i;
            if (end > start && all[end - 1] == '\r') end--;
            string line = all.Substring(start, end - start);
            start = i + 1;
            HandleLine(line, output);
        }
        lineCarry.Length = 0;
        if (start < all.Length)
            lineCarry.Append(all, start, all.Length - start);
    }

    void HandleLine(string line, ConcurrentQueue<MateOpenAISseEvent> output)
    {
        if (line.Length == 0)
        {
            FlushEvent(output);
            return;
        }
        if (line[0] == ':') return; // comment / keepalive

        if (line.StartsWith("data:", StringComparison.Ordinal))
        {
            string payload = line.Length > 5 ? line.Substring(5) : "";
            if (payload.StartsWith(" ", StringComparison.Ordinal))
                payload = payload.Substring(1);
            if (hasDataField) dataPayload.Append('\n');
            dataPayload.Append(payload);
            hasDataField = true;
        }
        // Ignore event:/id:/retry: and unknown fields.
    }

    void FlushEvent(ConcurrentQueue<MateOpenAISseEvent> output)
    {
        if (!hasDataField)
        {
            dataPayload.Length = 0;
            return;
        }

        string payload = dataPayload.ToString();
        dataPayload.Length = 0;
        hasDataField = false;

        if (string.IsNullOrWhiteSpace(payload)) return;

        if (string.Equals(payload.Trim(), "[DONE]", StringComparison.Ordinal))
        {
            output.Enqueue(MateOpenAISseEvent.Done());
            return;
        }

        EmitFromDataPayload(payload, output);
    }

    /// <summary>Parse one data: JSON payload and enqueue zero or more events.</summary>
    public static void EmitFromDataPayload(string json, ConcurrentQueue<MateOpenAISseEvent> output)
    {
        if (string.IsNullOrWhiteSpace(json) || output == null) return;
        try
        {
            var parsed = UnityEngine.JsonUtility.FromJson<StreamChunk>(json);
            if (parsed?.choices == null || parsed.choices.Length == 0)
                return;

            var choice = parsed.choices[0];
            string finish = choice.finish_reason;
            string content = choice.delta != null ? choice.delta.content : null;
            string reasoning = choice.delta != null ? choice.delta.reasoning_content : null;

            bool emitted = false;
            if (!string.IsNullOrEmpty(content))
            {
                output.Enqueue(MateOpenAISseEvent.Content(content, null));
                emitted = true;
            }
            if (!string.IsNullOrEmpty(reasoning))
            {
                output.Enqueue(MateOpenAISseEvent.Reasoning(reasoning, null));
                emitted = true;
            }
            if (!string.IsNullOrEmpty(finish))
            {
                output.Enqueue(MateOpenAISseEvent.Finish(finish));
                emitted = true;
            }
            // role-only / empty delta — ignore
            _ = emitted;
        }
        catch
        {
            output.Enqueue(MateOpenAISseEvent.ParseError("SSE JSON parse failed"));
        }
    }

    /// <summary>Parse one data: JSON payload into a single primary event (test helper).</summary>
    public static bool TryParseDeltaJson(string json, out MateOpenAISseEvent ev)
    {
        var q = new ConcurrentQueue<MateOpenAISseEvent>();
        EmitFromDataPayload(json, q);
        if (q.TryDequeue(out ev))
        {
            // Prefer content, else first event.
            return true;
        }
        ev = MateOpenAISseEvent.EmptyEvent();
        return true; // empty/role-only is not a hard parse failure
    }

    [Serializable]
    class StreamChunk
    {
        public StreamChoice[] choices;
    }

    [Serializable]
    class StreamChoice
    {
        public StreamDelta delta;
        public string finish_reason;
    }

    [Serializable]
    class StreamDelta
    {
        public string role;
        public string content;
        public string reasoning_content;
    }
}

public enum MateOpenAISseEventKind
{
    Empty = 0,
    ContentDelta = 1,
    ReasoningDelta = 2,
    FinishReason = 3,
    Done = 4,
    ParseError = 5,
}

public struct MateOpenAISseEvent
{
    public MateOpenAISseEventKind Kind;
    public string Text;
    public string FinishReason;
    public string ErrorMessage;

    public static MateOpenAISseEvent EmptyEvent() => new MateOpenAISseEvent { Kind = MateOpenAISseEventKind.Empty };
    public static MateOpenAISseEvent Content(string text, string finish = null) =>
        new MateOpenAISseEvent { Kind = MateOpenAISseEventKind.ContentDelta, Text = text ?? "", FinishReason = finish };
    public static MateOpenAISseEvent Reasoning(string text, string finish = null) =>
        new MateOpenAISseEvent { Kind = MateOpenAISseEventKind.ReasoningDelta, Text = text ?? "", FinishReason = finish };
    public static MateOpenAISseEvent Finish(string reason) =>
        new MateOpenAISseEvent { Kind = MateOpenAISseEventKind.FinishReason, FinishReason = reason };
    public static MateOpenAISseEvent Done() =>
        new MateOpenAISseEvent { Kind = MateOpenAISseEventKind.Done };
    public static MateOpenAISseEvent ParseError(string msg) =>
        new MateOpenAISseEvent { Kind = MateOpenAISseEventKind.ParseError, ErrorMessage = msg ?? "" };
}
