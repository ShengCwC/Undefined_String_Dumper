namespace Undefined.StringDumper.Core.Models;

public sealed record ProcessDetails
{
    public static ProcessDetails Empty { get; } = new();

    public string? FileVersion { get; init; }

    public string? SignatureStatus { get; init; }

    public string? SignerName { get; init; }

    public string? CommandLine { get; init; }

    public string? CurrentDirectory { get; init; }

    public string? PebAddress { get; init; }

    public string? ImageType { get; init; }

    public string? ParentProcess { get; init; }

    public string? MitigationPolicies { get; init; }

    public string? Protection { get; init; }
}
