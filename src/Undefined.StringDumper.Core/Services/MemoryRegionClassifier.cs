using Undefined.StringDumper.Core.Models;
using Undefined.StringDumper.Core.Native;

namespace Undefined.StringDumper.Core.Services;

public static class MemoryRegionClassifier
{
    public static MemoryRegionKind Classify(uint nativeType) => nativeType switch
    {
        NativeMethods.MemPrivate => MemoryRegionKind.Private,
        NativeMethods.MemMapped => MemoryRegionKind.Mapped,
        NativeMethods.MemImage => MemoryRegionKind.Image,
        _ => MemoryRegionKind.Unknown,
    };

    public static bool IsIncluded(MemoryRegionKind kind, ScanOptions options) => kind switch
    {
        MemoryRegionKind.Private => options.IncludePrivate,
        MemoryRegionKind.Mapped => options.IncludeMapped,
        MemoryRegionKind.Image => options.IncludeImage,
        _ => false,
    };

    public static bool IsReadable(uint state, uint protection)
    {
        if (state != NativeMethods.MemCommit)
        {
            return false;
        }

        if ((protection & NativeMethods.PageGuard) != 0)
        {
            return false;
        }

        var baseProtection = protection & 0xFF;
        return baseProtection is 0x02 or 0x04 or 0x08 or 0x10 or 0x20 or 0x40 or 0x80;
    }
}
