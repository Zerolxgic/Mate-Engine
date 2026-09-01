using System;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// Incremental triple-backtick fence segmenter.
/// Accepts arbitrary chunk boundaries; only ``` fences (not ~~~ / inline ticks) are structural.
/// </summary>
public sealed class MateResponseSegmenter
{
    readonly Guid turnId;
    readonly List<MateResponseSegment> finalized = new List<MateResponseSegment>();
    readonly StringBuilder pending = new StringBuilder();

    enum Mode { Prose, Code }

    Mode mode = Mode.Prose;
    int nextSequence;
    bool frozen;

    readonly StringBuilder proseRaw = new StringBuilder();

    readonly StringBuilder codeRaw = new StringBuilder();
    readonly StringBuilder codeBody = new StringBuilder();
    string codeLanguage = "";

    public MateResponseSegmenter(Guid turnId)
    {
        this.turnId = turnId;
    }

    public Guid TurnId => turnId;
    public bool IsFrozen => frozen;

    public bool Append(string chunk)
    {
        if (frozen) return false;
        if (string.IsNullOrEmpty(chunk)) return true;
        pending.Append(chunk);
        Drain(finalize: false);
        return true;
    }

    public void Finish(bool freeze)
    {
        if (frozen) return;
        Drain(finalize: true);
        if (mode == Mode.Prose)
            EmitProseIfAny();
        else
            EmitCode(fenceClosed: false);
        if (freeze) frozen = true;
    }

    public IReadOnlyList<MateResponseSegment> GetSegmentsSnapshot()
    {
        var list = new List<MateResponseSegment>(finalized.Count + 1);
        for (int i = 0; i < finalized.Count; i++)
            list.Add(finalized[i]);

        if (!frozen)
        {
            if (mode == Mode.Prose)
            {
                string raw = proseRaw.ToString() + pending;
                if (raw.Length > 0)
                {
                    list.Add(new MateResponseSegment(
                        turnId, nextSequence, MateResponseSegmentKind.Prose,
                        raw, raw, "", false, false));
                }
            }
            else
            {
                string raw = codeRaw.ToString() + pending;
                list.Add(new MateResponseSegment(
                    turnId, nextSequence, MateResponseSegmentKind.CodeBlock,
                    codeBody.ToString(), raw, codeLanguage, false, false));
            }
        }

        return list;
    }

    public string GetRawText()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < finalized.Count; i++)
            sb.Append(finalized[i].RawText);

        if (mode == Mode.Prose)
        {
            sb.Append(proseRaw);
            sb.Append(pending);
        }
        else
        {
            sb.Append(codeRaw);
            sb.Append(pending);
        }

        return sb.ToString();
    }

    void Drain(bool finalize)
    {
        while (pending.Length > 0)
        {
            string data = pending.ToString();
            int consumed = mode == Mode.Prose
                ? ConsumeProse(data, finalize)
                : ConsumeCode(data, finalize);

            if (consumed <= 0)
                return;

            pending.Remove(0, consumed);
        }
    }

    int ConsumeProse(string data, bool finalize)
    {
        int searchFrom = 0;
        while (searchFrom <= data.Length)
        {
            int fence = FindLineStartTripleTick(data, searchFrom, ProseIsLineStart);
            if (fence < 0)
            {
                if (finalize)
                {
                    proseRaw.Append(data);
                    return data.Length;
                }

                // Hold an incomplete line-start backtick run that could become ```.
                int hold = IncompleteLineStartTickHold(data, ProseIsLineStart);
                if (hold >= 0)
                {
                    if (hold > 0) proseRaw.Append(data, 0, hold);
                    return hold > 0 ? hold : 0;
                }

                proseRaw.Append(data);
                return data.Length;
            }

            // Longer than 3 ticks at line start is not a Slice-4 fence — skip past this run.
            int run = CountTicks(data, fence);
            if (run != 3)
            {
                searchFrom = fence + run;
                continue;
            }

            // Try to complete the opening fence line (``` + info + newline).
            if (!TryReadOpenFence(data, fence, out int openLen, out string lang, out bool complete))
            {
                if (!finalize)
                {
                    // Commit prose before the candidate; wait for the rest.
                    if (fence > 0)
                    {
                        proseRaw.Append(data, 0, fence);
                        return fence;
                    }
                    return 0;
                }

                // Finalizing with incomplete open — remainder is prose.
                proseRaw.Append(data);
                return data.Length;
            }

            if (!complete)
            {
                if (fence > 0)
                {
                    proseRaw.Append(data, 0, fence);
                    return fence;
                }
                return 0;
            }

            if (fence > 0)
                proseRaw.Append(data, 0, fence);
            EmitProseIfAny();

            codeRaw.Clear();
            codeBody.Clear();
            codeLanguage = lang;
            codeRaw.Append(data, fence, openLen);
            mode = Mode.Code;
            return fence + openLen;
        }

        // Unreachable, but keep compiler happy.
        proseRaw.Append(data);
        return data.Length;
    }

    int ConsumeCode(string data, bool finalize)
    {
        int searchFrom = 0;
        while (searchFrom <= data.Length)
        {
            int fence = FindLineStartTripleTick(data, searchFrom, CodeIsLineStart);
            if (fence < 0)
            {
                if (finalize)
                {
                    codeBody.Append(data);
                    codeRaw.Append(data);
                    return data.Length;
                }

                int hold = IncompleteLineStartTickHold(data, CodeIsLineStart);
                if (hold >= 0)
                {
                    if (hold > 0)
                    {
                        codeBody.Append(data, 0, hold);
                        codeRaw.Append(data, 0, hold);
                        return hold;
                    }
                    return 0;
                }

                codeBody.Append(data);
                codeRaw.Append(data);
                return data.Length;
            }

            int run = CountTicks(data, fence);
            if (run != 3)
            {
                // Not a close. Consume through this tick run as body and continue.
                // But if the run is incomplete at EOF, hold it.
                if (!finalize && fence + run >= data.Length && run < 3 && CodeIsLineStart(data, fence))
                {
                    if (fence > 0)
                    {
                        codeBody.Append(data, 0, fence);
                        codeRaw.Append(data, 0, fence);
                        return fence;
                    }
                    return 0;
                }

                searchFrom = fence + Math.Max(run, 1);
                continue;
            }

            if (!TryReadCloseFence(data, fence, out int closeLen, out bool complete))
            {
                // Triple ticks at line start but not a valid close line (e.g. ```x) —
                // treat as body content.
                searchFrom = fence + 3;
                continue;
            }

            if (!complete)
            {
                if (!finalize)
                {
                    if (fence > 0)
                    {
                        codeBody.Append(data, 0, fence);
                        codeRaw.Append(data, 0, fence);
                        return fence;
                    }
                    return 0;
                }

                codeBody.Append(data);
                codeRaw.Append(data);
                return data.Length;
            }

            if (fence > 0)
            {
                codeBody.Append(data, 0, fence);
                codeRaw.Append(data, 0, fence);
            }

            codeRaw.Append(data, fence, closeLen);
            EmitCode(fenceClosed: true);
            mode = Mode.Prose;
            proseRaw.Clear();
            return fence + closeLen;
        }

        codeBody.Append(data);
        codeRaw.Append(data);
        return data.Length;
    }

    void EmitProseIfAny()
    {
        if (proseRaw.Length == 0) return;
        string raw = proseRaw.ToString();
        finalized.Add(new MateResponseSegment(
            turnId, nextSequence++, MateResponseSegmentKind.Prose,
            raw, raw, "", true, false));
        proseRaw.Clear();
    }

    void EmitCode(bool fenceClosed)
    {
        string raw = codeRaw.ToString();
        if (raw.Length == 0 && codeBody.Length == 0) return;
        finalized.Add(new MateResponseSegment(
            turnId, nextSequence++, MateResponseSegmentKind.CodeBlock,
            codeBody.ToString(), raw, codeLanguage, true, fenceClosed));
        codeRaw.Clear();
        codeBody.Clear();
        codeLanguage = "";
    }

    bool ProseIsLineStart(string data, int index)
    {
        if (index > 0) return data[index - 1] == '\n';
        return proseRaw.Length == 0 || proseRaw[proseRaw.Length - 1] == '\n';
    }

    bool CodeIsLineStart(string data, int index)
    {
        if (index > 0) return data[index - 1] == '\n';
        return codeBody.Length == 0 || codeBody[codeBody.Length - 1] == '\n';
    }

    static int FindLineStartTripleTick(string data, int start, Func<string, int, bool> isLineStart)
    {
        for (int i = start; i < data.Length; i++)
        {
            if (data[i] != '`') continue;
            if (!isLineStart(data, i)) continue;
            return i;
        }
        return -1;
    }

    static int CountTicks(string data, int start)
    {
        int n = 0;
        for (int i = start; i < data.Length && data[i] == '`'; i++)
            n++;
        return n;
    }

    /// <summary>
    /// If the string ends with a line-start run of 1-2 backticks (or 3 without a completed
    /// open/close line yet handled elsewhere), return the index where holding should begin.
    /// Returns -1 if nothing should be held.
    /// </summary>
    static int IncompleteLineStartTickHold(string data, Func<string, int, bool> isLineStart)
    {
        if (data.Length == 0) return -1;

        // Find start of trailing backtick run.
        int i = data.Length - 1;
        if (data[i] != '`') return -1;
        while (i > 0 && data[i - 1] == '`')
            i--;

        int run = data.Length - i;
        if (run >= 3) return -1; // complete triple present; caller handles parse
        if (!isLineStart(data, i)) return -1;
        return i;
    }

    static bool TryReadOpenFence(
        string data,
        int start,
        out int length,
        out string language,
        out bool complete)
    {
        length = 0;
        language = "";
        complete = false;

        if (start + 2 >= data.Length) return false;
        if (CountTicks(data, start) != 3) return false;

        int i = start + 3;
        int infoStart = i;
        while (i < data.Length && data[i] != '\n' && data[i] != '\r')
            i++;

        if (i >= data.Length)
        {
            // Waiting for end of info line / newline.
            return true; // candidate recognized, incomplete
        }

        language = data.Substring(infoStart, i - infoStart).Trim();
        if (data[i] == '\r')
        {
            i++;
            if (i >= data.Length) return true; // incomplete CRLF
            if (data[i] == '\n') i++;
        }
        else
        {
            i++; // \n
        }

        length = i - start;
        complete = true;
        return true;
    }

    static bool TryReadCloseFence(
        string data,
        int start,
        out int length,
        out bool complete)
    {
        length = 0;
        complete = false;

        int run = CountTicks(data, start);
        if (run < 3)
            return true; // incomplete candidate

        if (run != 3)
            return false;

        int i = start + 3;
        while (i < data.Length && (data[i] == ' ' || data[i] == '\t'))
            i++;

        if (i >= data.Length)
        {
            // ``` at EOF closes.
            length = i - start;
            complete = true;
            return true;
        }

        if (data[i] == '\r')
        {
            i++;
            if (i < data.Length && data[i] == '\n') i++;
            else if (i >= data.Length)
            {
                // incomplete CRLF — still treat ```\r at end as waiting? keep incomplete
                return true;
            }
        }
        else if (data[i] == '\n')
        {
            i++;
        }
        else
        {
            // ```info on close line — not a close for Slice 4.
            return false;
        }

        length = i - start;
        complete = true;
        return true;
    }
}
