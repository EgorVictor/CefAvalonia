using System.Collections.Generic;
using System.Text;

namespace CefSharp.Avalonia;

/// <summary>
/// Wire format for the C# &lt;-&gt; CefBrowser.Native stdio channel.
///
/// Every message is a single line:
///   {command-or-event}|{field}|{field}...
///
/// Fields that may contain arbitrary text (URLs, titles, paths, dialog text)
/// must be escaped with <see cref="Escape"/> so that '|', CR and LF never break
/// framing. Numeric/boolean fields are written verbatim.
///
/// Protocol history:
///   2 - pipe name / stdio, no escaping, per-browser ids
///   3 - escaped text fields, JS dialog round-trip, Hello handshake
/// </summary>
internal static class CefProtocol
{
    /// <summary>Protocol version spoken by this assembly. Must match the native host.</summary>
    public const int Version = 3;

    /// <summary>Escapes a free-text field for the line protocol.</summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '|': sb.Append("\\p"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Reverses <see cref="Escape"/>.</summary>
    public static string Unescape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c != '\\' || i + 1 >= value.Length)
            {
                sb.Append(c);
                continue;
            }

            char n = value[++i];
            switch (n)
            {
                case '\\': sb.Append('\\'); break;
                case 'p': sb.Append('|'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                default:
                    // Unknown escape: keep it verbatim so older peers still parse.
                    sb.Append('\\').Append(n);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Splits a message payload on unescaped '|'.
    /// The returned fields are already unescaped.
    /// </summary>
    public static string[] Split(string? payload, int maxFields = int.MaxValue)
    {
        if (string.IsNullOrEmpty(payload)) return [];

        var fields = new List<string>(8);
        var current = new StringBuilder(payload.Length);
        for (int i = 0; i < payload.Length; i++)
        {
            char c = payload[i];
            if (c == '\\' && i + 1 < payload.Length)
            {
                // Preserve the escape pair; Unescape() resolves it below.
                current.Append(c).Append(payload[++i]);
                continue;
            }
            if (c == '|' && fields.Count + 1 < maxFields)
            {
                fields.Add(Unescape(current.ToString()));
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        fields.Add(Unescape(current.ToString()));
        return fields.ToArray();
    }

    /// <summary>
    /// Splits into exactly <paramref name="count"/> fields; the last field keeps
    /// any remaining separators. Returns false when there are too few fields.
    /// </summary>
    public static bool TrySplit(string? payload, int count, out string[] fields)
    {
        fields = Split(payload, count);
        return fields.Length == count;
    }
}
