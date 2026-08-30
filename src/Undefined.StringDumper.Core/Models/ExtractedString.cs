namespace Undefined.StringDumper.Core.Models;

public sealed record ExtractedString(
    ulong Address,
    int Length,
    string Value,
    EncodingKind Encoding,
    MemoryRegionKind RegionKind)
{
    public string AddressText => $"0x{Address:X}";

    public string EncodingText => Encoding switch
    {
        EncodingKind.Ascii => "ASCII",
        EncodingKind.Utf16LittleEndian => "UTF-16",
        _ => Encoding.ToString(),
    };

    public string RegionText => RegionKind switch
    {
        MemoryRegionKind.Private => "Private",
        MemoryRegionKind.Mapped => "Mapped",
        MemoryRegionKind.Image => "Image",
        _ => "Unknown",
    };
}
