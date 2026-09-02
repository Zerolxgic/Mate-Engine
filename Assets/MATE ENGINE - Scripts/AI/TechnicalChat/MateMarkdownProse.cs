using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Bounded safe Markdown → HTML for technical-chat prose.
/// Escapes raw model text before injecting any presentation tags.
/// </summary>
public static class MateMarkdownProse
{
    public static string ToSafeHtml(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return "";

        // Normalize newlines; keep content faithful otherwise.
        string src = markdown.Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = src.Split('\n');
        var sb = new StringBuilder(src.Length + 64);
        int i = 0;
        while (i < lines.Length)
        {
            string line = lines[i];

            // Fenced code inside prose renderer should be rare (segments separate code),
            // but if present, render as pre/code without markdown inside.
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                string lang = line.Length > 3 ? EscapeHtml(line.Substring(3).Trim()) : "";
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].StartsWith("```", StringComparison.Ordinal))
                {
                    if (code.Length > 0) code.Append('\n');
                    code.Append(lines[i]);
                    i++;
                }
                if (i < lines.Length) i++; // closing fence
                sb.Append("<pre class=\"md-code\"><code");
                if (!string.IsNullOrEmpty(lang)) sb.Append(" data-lang=\"").Append(lang).Append('"');
                sb.Append('>').Append(EscapeHtml(code.ToString())).Append("</code></pre>");
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                sb.Append("<div class=\"md-gap\"></div>");
                i++;
                continue;
            }

            // Headings
            var hm = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
            if (hm.Success)
            {
                int level = Math.Min(hm.Groups[1].Value.Length, 6);
                sb.Append("<h").Append(level).Append(" class=\"md-h\">")
                    .Append(Inline(hm.Groups[2].Value))
                    .Append("</h").Append(level).Append('>');
                i++;
                continue;
            }

            // Blockquote
            if (line.StartsWith("> ", StringComparison.Ordinal) || line == ">")
            {
                string body = line.Length >= 2 && line[0] == '>' ? line.Substring(line.StartsWith("> ", StringComparison.Ordinal) ? 2 : 1).TrimStart() : "";
                sb.Append("<blockquote class=\"md-quote\">").Append(Inline(body)).Append("</blockquote>");
                i++;
                continue;
            }

            // Unordered list run
            if (Regex.IsMatch(line, @"^\s*[-*+]\s+"))
            {
                sb.Append("<ul class=\"md-ul\">");
                while (i < lines.Length && Regex.IsMatch(lines[i], @"^\s*[-*+]\s+"))
                {
                    string item = Regex.Replace(lines[i], @"^\s*[-*+]\s+", "");
                    sb.Append("<li>").Append(Inline(item)).Append("</li>");
                    i++;
                }
                sb.Append("</ul>");
                continue;
            }

            // Ordered list run
            if (Regex.IsMatch(line, @"^\s*\d+\.\s+"))
            {
                sb.Append("<ol class=\"md-ol\">");
                while (i < lines.Length && Regex.IsMatch(lines[i], @"^\s*\d+\.\s+"))
                {
                    string item = Regex.Replace(lines[i], @"^\s*\d+\.\s+", "");
                    sb.Append("<li>").Append(Inline(item)).Append("</li>");
                    i++;
                }
                sb.Append("</ol>");
                continue;
            }

            // Paragraph: merge consecutive non-blank plain lines
            var para = new StringBuilder();
            while (i < lines.Length)
            {
                string l = lines[i];
                if (string.IsNullOrWhiteSpace(l)) break;
                if (l.StartsWith("```", StringComparison.Ordinal)) break;
                if (Regex.IsMatch(l, @"^(#{1,6})\s+")) break;
                if (l.StartsWith(">")) break;
                if (Regex.IsMatch(l, @"^\s*[-*+]\s+")) break;
                if (Regex.IsMatch(l, @"^\s*\d+\.\s+")) break;
                if (para.Length > 0) para.Append(' ');
                para.Append(l.TrimEnd());
                i++;
            }
            sb.Append("<p class=\"md-p\">").Append(Inline(para.ToString())).Append("</p>");
        }

        return sb.ToString();
    }

    static string Inline(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        // Escape first — model cannot inject tags.
        string s = EscapeHtml(text);

        // Inline code `...`
        s = Regex.Replace(s, @"`([^`]+)`", "<code class=\"md-inline\">$1</code>");

        // Bold **...** or __...__
        s = Regex.Replace(s, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
        s = Regex.Replace(s, @"__(.+?)__", "<strong>$1</strong>");

        // Emphasis *...* or _..._ (avoid matching inside words for _)
        s = Regex.Replace(s, @"(?<!\w)\*(.+?)\*(?!\w)", "<em>$1</em>");
        s = Regex.Replace(s, @"(?<!\w)_(.+?)_(?!\w)", "<em>$1</em>");

        // Visible links [text](url) — no auto-navigation attribute beyond href; UI may ignore clicks.
        s = Regex.Replace(s, @"\[([^\]]+)\]\(([^)]+)\)",
            m => "<span class=\"md-link\" title=\"" + m.Groups[2].Value + "\">" + m.Groups[1].Value + "</span>");

        return s;
    }

    public static string EscapeHtml(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;");
    }

    public static string CodeCopyPayload(MateResponseSegment segment)
    {
        if (segment == null) return "";
        return segment.Text ?? "";
    }
}
