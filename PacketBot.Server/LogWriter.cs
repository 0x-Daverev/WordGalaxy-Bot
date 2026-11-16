using System.Text;
using System.Text.RegularExpressions;

namespace PacketBot.Server;

// Account of the code that is writing, so the log says which account a line
// belongs to. AsyncLocal flows into the TCP reader, reconnects and module
// tasks started inside the scope; the holder is mutable so the label can be
// replaced by the account ID once /map has answered.
public sealed class LogTag
{
    private static readonly AsyncLocal<LogTag?> current = new();

    public LogTag(string name) => Name = name;

    public string Name { get; set; }

    public static LogTag? Current => current.Value;

    // Everything started from the calling flow after this call is tagged.
    public static LogTag Enter(string name)
    {
        var tag = new LogTag(name);
        current.Value = tag;
        return tag;
    }

    public static void Enter(LogTag? tag) => current.Value = tag;
}

// Known secrets (PHPSESSID values, Telegram token) never reach the log, even
// if some message or exception text happens to include them.
public static class LogSecrets
{
    private static readonly object sync = new();
    private static string[] values = Array.Empty<string>();

    // Generic shapes as a second line of defence for values not registered.
    private static readonly Regex Patterns = new(
        @"(?<k>PHPSESSID[=:]\s*)[^\s;&,""']+|(?<k>/bot)\d+:[A-Za-z0-9_-]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static void Add(string? secret)
    {
        // Very short values would mask ordinary words.
        if (string.IsNullOrWhiteSpace(secret) || secret.Trim().Length < 8) return;
        lock (sync)
            if (!values.Contains(secret.Trim(), StringComparer.Ordinal))
                values = values.Append(secret.Trim()).OrderByDescending(v => v.Length).ToArray();
    }

    public static string Redact(string line)
    {
        foreach (string secret in values)
            line = line.Replace(secret, "****", StringComparison.Ordinal);
        return Patterns.Replace(line, m => m.Groups["k"].Value + "****");
    }
}

// Console output of the server, one complete line at a time:
//   [<n>]HH:mm:ss.fff [account] message
// <n> is the syslog priority that journald reads from each line (6 info,
// 3 error), written only when running under systemd/journald.
public sealed class LogWriter : TextWriter
{
    public const int Info = 6;
    public const int Error = 3;

    private readonly TextWriter inner;
    private readonly int? priority;
    private readonly Func<DateTime> clock;
    private readonly StringBuilder line = new();
    private readonly object sync = new();

    public LogWriter(TextWriter inner, int? priority = null, Func<DateTime>? clock = null)
    {
        this.inner = inner;
        this.priority = priority;
        this.clock = clock ?? (() => DateTime.Now);
    }

    // journald sets JOURNAL_STREAM for services whose output goes to it.
    public static bool UnderJournald(Func<string, string?> environment) =>
        !string.IsNullOrEmpty(environment("JOURNAL_STREAM"));

    public override Encoding Encoding => inner.Encoding;

    public override void Write(char value)
    {
        lock (sync)
        {
            if (value == '\n') EmitLine();
            else if (value != '\r') line.Append(value);
        }
    }

    public override void Write(string? value)
    {
        if (value == null) return;
        lock (sync)
            foreach (char c in value) Write(c);
    }

    public override void WriteLine(string? value)
    {
        lock (sync)
        {
            Write(value);
            EmitLine();
        }
    }

    public override void WriteLine() => WriteLine((string?)null);

    private void EmitLine()
    {
        string text = line.ToString();
        line.Clear();
        if (text.Length == 0)
        {
            inner.Write('\n');
            inner.Flush();
            return;
        }
        var output = new StringBuilder();
        if (priority != null) output.Append('<').Append(priority.Value).Append('>');
        output.Append(clock().ToString("HH:mm:ss.fff")).Append(' ');
        if (LogTag.Current is { } tag) output.Append('[').Append(tag.Name).Append("] ");
        output.Append(LogSecrets.Redact(text)).Append('\n');
        inner.Write(output.ToString());
        inner.Flush();
    }

    public override void Flush()
    {
        lock (sync)
        {
            if (line.Length > 0) EmitLine();
            inner.Flush();
        }
    }
}
