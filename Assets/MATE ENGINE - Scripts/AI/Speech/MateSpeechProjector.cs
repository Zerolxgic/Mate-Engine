using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Deterministic speakable projection of canonical response segments.
/// Does not mutate segments, history, or the canonical transcript.
/// </summary>
public static class MateSpeechProjector
{
    static readonly Regex InlineCode = new Regex(@"`[^`]+`", RegexOptions.Compiled);
    static readonly Regex MdLink = new Regex(@"\[([^\]]*)\]\(([^)]*)\)", RegexOptions.Compiled);
    static readonly Regex Bold = new Regex(@"(\*\*|__)(.*?)\1", RegexOptions.Compiled);
    static readonly Regex Italic = new Regex(@"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)|(?<!_)_(?!_)(.+?)(?<!_)_(?!_)", RegexOptions.Compiled);
    static readonly Regex Heading = new Regex(@"^\s{0,3}#{1,6}\s+", RegexOptions.Compiled | RegexOptions.Multiline);
    static readonly Regex BareUrl = new Regex(@"https?://[^\s<>\]]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex WinPath = new Regex(@"\b[A-Za-z]:\\(?:[^\s|<>""']+)", RegexOptions.Compiled);
    static readonly Regex UnixAbsPath = new Regex(@"(?<![\w:/])/(?:(?:Users|home|var|tmp|opt|etc|mnt|media)(?:/[^\s|<>""']+)+)", RegexOptions.Compiled);
    static readonly Regex Strike = new Regex(@"~~(.*?)~~", RegexOptions.Compiled);
    static readonly Regex Image = new Regex(@"!\[([^\]]*)\]\(([^)]*)\)", RegexOptions.Compiled);
    static readonly Regex MultiSpace = new Regex(@"[ \t]{2,}", RegexOptions.Compiled);

    /// <summary>
    /// Project the speakable portion of a segment snapshot into plain text (order-preserving).
    /// </summary>
    public static string Project(IReadOnlyList<MateResponseSegment> segments)
    {
        if (segments == null || segments.Count == 0) return "";

        var sb = new StringBuilder();
        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            if (seg == null) continue;
            if (seg.Kind != MateResponseSegmentKind.Prose) continue;

            string speakable = ProjectProse(seg.Text);
            if (string.IsNullOrWhiteSpace(speakable)) continue;

            if (sb.Length > 0)
            {
                char last = sb[sb.Length - 1];
                char first = speakable[0];
                if (!char.IsWhiteSpace(last) && !char.IsWhiteSpace(first))
                    sb.Append(' ');
            }
            sb.Append(speakable);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Strip Markdown / non-speech material from a single prose string.</summary>
    public static string ProjectProse(string prose)
    {
        if (string.IsNullOrEmpty(prose)) return "";

        string s = prose.Replace("\r\n", "\n").Replace("\r", "\n");

        // Images: keep alt text if present, drop URL.
        s = Image.Replace(s, m => m.Groups[1].Value ?? "");
        // Links: keep label, drop URL.
        s = MdLink.Replace(s, m => m.Groups[1].Value ?? "");
        // Inline code: omit entirely for Slice 7.
        s = InlineCode.Replace(s, " ");
        // Bold / strike / italic markup.
        s = Strike.Replace(s, m => m.Groups[1].Value ?? "");
        s = Bold.Replace(s, m => m.Groups[2].Value ?? "");
        s = Italic.Replace(s, m =>
        {
            string a = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            return a ?? "";
        });
        // Headings: drop # markers, keep text.
        s = Heading.Replace(s, "");
        // Bare URLs and obvious paths.
        s = BareUrl.Replace(s, " ");
        s = WinPath.Replace(s, " ");
        s = UnixAbsPath.Replace(s, " ");
        // Residual markdown punctuation that is presentation-only.
        s = s.Replace("**", "").Replace("__", "");
        // Soften leftover backticks from incomplete inline spans.
        s = s.Replace("`", " ");

        // Collapse whitespace while preserving newlines as spaces for speech.
        s = s.Replace('\n', ' ');
        s = MultiSpace.Replace(s, " ");
        return s.Trim();
    }
}
