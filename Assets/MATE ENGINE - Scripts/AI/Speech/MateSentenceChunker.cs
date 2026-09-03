using System;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// Streaming-safe sentence/phrase chunker for TTS.
/// Tolerates hostile provider splits; preserves exact text order.
/// </summary>
public sealed class MateSentenceChunker
{
    public const int DefaultMaxBufferChars = 480;
    // The first release favors time-to-first-speech; later weak boundaries need
    // more context so Kokoro has enough audio to hide one synthesis request.
    public const int FirstChunkMinChars = 32;
    public const int WeakBoundaryMinChars = 96;

    readonly StringBuilder buffer = new StringBuilder();
    readonly int maxBufferChars;
    bool hasEmittedChunk;

    public MateSentenceChunker(int maxBufferChars = DefaultMaxBufferChars)
    {
        maxBufferChars = Math.Max(64, maxBufferChars);
        this.maxBufferChars = maxBufferChars;
    }

    public int BufferedLength => buffer.Length;

    /// <summary>Append streamed speakable text; emit completed sentence/phrase units.</summary>
    public List<string> Append(string text)
    {
        var emitted = new List<string>();
        if (string.IsNullOrEmpty(text)) return emitted;

        buffer.Append(text);
        EmitCompleted(emitted, flushAll: false);
        EmitSafetyFallback(emitted);
        return emitted;
    }

    /// <summary>Flush remaining reasonable trailing prose at turn completion.</summary>
    public List<string> Flush()
    {
        var emitted = new List<string>();
        EmitCompleted(emitted, flushAll: true);
        string rest = buffer.ToString().Trim();
        buffer.Clear();
        if (!IsSpeakableChunk(rest)) return emitted;
        emitted.Add(rest);
        hasEmittedChunk = true;
        return emitted;
    }

    public void Reset()
    {
        buffer.Clear();
        hasEmittedChunk = false;
    }

    void EmitCompleted(List<string> emitted, bool flushAll)
    {
        while (true)
        {
            string s = buffer.ToString();
            int cut = FindReleaseBoundary(s, !hasEmittedChunk, flushAll);
            if (cut < 0) break;

            string chunk = s.Substring(0, cut + 1).Trim();
            buffer.Remove(0, cut + 1);
            // Consume following whitespace so the next sentence starts clean.
            while (buffer.Length > 0 && char.IsWhiteSpace(buffer[0]))
                buffer.Remove(0, 1);

            if (IsSpeakableChunk(chunk))
            {
                emitted.Add(chunk);
                hasEmittedChunk = true;
            }
        }
    }

    void EmitSafetyFallback(List<string> emitted)
    {
        while (buffer.Length >= maxBufferChars)
        {
            string s = buffer.ToString();
            int cut = FindSoftBreak(s, maxBufferChars);
            if (cut < 0) cut = Math.Min(maxBufferChars, s.Length) - 1;
            if (cut < 0) break;

            string chunk = s.Substring(0, cut + 1).Trim();
            buffer.Remove(0, cut + 1);
            while (buffer.Length > 0 && char.IsWhiteSpace(buffer[0]))
                buffer.Remove(0, 1);

            if (IsSpeakableChunk(chunk))
            {
                emitted.Add(chunk);
                hasEmittedChunk = true;
            }
            else if (buffer.Length == 0)
                break;
        }
    }

    static int FindReleaseBoundary(string s, bool isFirstChunk, bool flushAll)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool strong = c == '.' || c == '!' || c == '?';
            bool weak = c == ',' || c == ';' || c == ':' || c == '—' || c == '–' || c == '-';
            if (!strong && !weak) continue;

            // Avoid splitting common abbreviations / decimals: digit.digit
            if (strong && c == '.' && i > 0 && i + 1 < s.Length &&
                char.IsDigit(s[i - 1]) && char.IsDigit(s[i + 1]))
                continue;

            int minimum = isFirstChunk ? FirstChunkMinChars : WeakBoundaryMinChars;
            if (weak && i + 1 < minimum)
                continue;

            if (i + 1 >= s.Length)
                return flushAll ? i : -1;

            char next = s[i + 1];
            if (char.IsWhiteSpace(next) || next == '"' || next == '\'' || next == ')')
                return i;
        }
        return -1;
    }

    static int FindSoftBreak(string s, int limit)
    {
        int end = Math.Min(limit, s.Length) - 1;
        for (int i = end; i >= Math.Max(0, end - 80); i--)
        {
            if (char.IsWhiteSpace(s[i])) return i;
        }
        return end;
    }

    static bool IsSpeakableChunk(string chunk)
    {
        if (string.IsNullOrWhiteSpace(chunk)) return false;
        for (int i = 0; i < chunk.Length; i++)
        {
            if (char.IsLetterOrDigit(chunk[i])) return true;
        }
        return false;
    }
}
