namespace Undefined.StringDumper.App.Services;

internal static class DumperRecoveryInput
{
    private const string Prefix = "usd-restore-v1:";

    public static bool TryParseBundle(string? value, out string credential, out string archiveId)
    {
        credential = string.Empty;
        archiveId = string.Empty;
        var text = value?.Trim() ?? string.Empty;
        if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var payload = text[Prefix.Length..];
        var separator = payload.IndexOf(':');
        if (separator <= 0 || separator == payload.Length - 1) return false;
        if (!Guid.TryParse(payload[..separator], out var parsedArchiveId)) return false;

        var parsedCredential = payload[(separator + 1)..].Trim();
        if (!IsCredential(parsedCredential)) return false;

        credential = parsedCredential;
        archiveId = parsedArchiveId.ToString("D");
        return true;
    }

    private static bool IsCredential(string value) =>
        value.Length == 47 &&
        value.StartsWith("usd_", StringComparison.Ordinal) &&
        value[4..].All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
}
