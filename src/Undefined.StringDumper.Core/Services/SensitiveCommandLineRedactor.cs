using System.Text.RegularExpressions;

namespace Undefined.StringDumper.Core.Services;

public static partial class SensitiveCommandLineRedactor
{
    public static string? Redact(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return commandLine;
        }

        var redacted = OptionEqualsRegex().Replace(commandLine, "${prefix}[REDACTED]");
        redacted = OptionValueRegex().Replace(redacted, "${prefix}[REDACTED]");
        redacted = JavaPropertyRegex().Replace(redacted, "${prefix}[REDACTED]");
        return UrlSecretRegex().Replace(redacted, "${prefix}[REDACTED]");
    }

    [GeneratedRegex("""(?ix)(?<prefix>(?:--?|/)(?:access[-_]?token|client[-_]?token|refresh[-_]?token|auth[-_]?token|token|password|passwd|secret|api[-_]?key)\s*=\s*)(?:"(?:[^"]|"")*"|'[^']*'|[^\s]+)""")]
    private static partial Regex OptionEqualsRegex();

    [GeneratedRegex("""(?ix)(?<prefix>(?:--?|/)(?:access[-_]?token|client[-_]?token|refresh[-_]?token|auth[-_]?token|token|password|passwd|secret|api[-_]?key)\s+)(?:"(?:[^"]|"")*"|'[^']*'|[^\s]+)""")]
    private static partial Regex OptionValueRegex();

    [GeneratedRegex("""(?ix)(?<prefix>-D[^\s=]*(?:token|password|passwd|secret|api[-_]?key)[^\s=]*=)(?:"(?:[^"]|"")*"|'[^']*'|[^\s]+)""")]
    private static partial Regex JavaPropertyRegex();

    [GeneratedRegex("""(?ix)(?<prefix>(?:[?&])(?:access[-_]?token|client[-_]?token|refresh[-_]?token|auth[-_]?token|token|password|passwd|secret|api[-_]?key)=)[^&\s"]+""")]
    private static partial Regex UrlSecretRegex();
}
