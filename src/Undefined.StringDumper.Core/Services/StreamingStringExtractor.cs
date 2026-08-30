using System.Text;
using Undefined.StringDumper.Core.Models;

namespace Undefined.StringDumper.Core.Services;

/// <summary>
/// Extracts byte strings with the state machine used by Process Hacker 2.39.
/// "Unicode" in that implementation means printable single-byte characters
/// separated by zero bytes, rather than arbitrary UTF-16 code units.
/// </summary>
public sealed class StreamingStringExtractor
{
    private readonly ScanOptions _options;
    private readonly MemoryRegionKind _regionKind;
    private readonly StringBuilder _display = new();
    private byte _previousByte;
    private bool _previousPrintable;
    private bool _beforePreviousPrintable;
    private long _runLength;
    private ulong _nextAddress;
    private bool _hasInput;

    public StreamingStringExtractor(ScanOptions options, MemoryRegionKind regionKind)
    {
        options.Validate();
        _options = options;
        _regionKind = regionKind;
    }

    public void Consume(ReadOnlySpan<byte> bytes, ulong baseAddress, ICollection<ExtractedString> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (bytes.IsEmpty)
        {
            return;
        }

        if (_hasInput && baseAddress != _nextAddress)
        {
            FlushPending(output);
            ResetState();
        }

        for (var index = 0; index < bytes.Length; index++)
        {
            ConsumeByte(bytes[index], baseAddress + (ulong)index, output);
        }

        _nextAddress = checked(baseAddress + (ulong)bytes.Length);
        _hasInput = true;
    }

    public void Complete(ICollection<ExtractedString> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        FlushPending(output);
        ResetState();
    }

    private void ConsumeByte(byte value, ulong address, ICollection<ExtractedString> output)
    {
        var printable = IsProcessHackerPrintable(value);

        if (_beforePreviousPrintable && _previousPrintable && printable)
        {
            Append(value);
        }
        else if (_beforePreviousPrintable && _previousPrintable && !printable)
        {
            if (_runLength >= _options.MinimumLength)
            {
                Emit(address, isWide: false, bias: 0, output);
            }
            else if (value == 0)
            {
                ResetWith(_previousByte);
            }
            else
            {
                ClearRun();
            }
        }
        else if (_beforePreviousPrintable && !_previousPrintable && printable)
        {
            if (_previousByte == 0)
            {
                Append(value);
            }
        }
        else if (_beforePreviousPrintable && !_previousPrintable && !printable)
        {
            if (_runLength >= _options.MinimumLength)
            {
                Emit(address, isWide: true, bias: 0, output);
            }
            else
            {
                ClearRun();
            }
        }
        else if (!_beforePreviousPrintable && _previousPrintable && printable)
        {
            if (_runLength >= _options.MinimumLength + 1L)
            {
                ExcludeLastCharacter();
                Emit(address, isWide: true, bias: 1, output);
            }
            else
            {
                ResetWith(_previousByte, value);
            }
        }
        else if (!_beforePreviousPrintable && !_previousPrintable && printable)
        {
            Append(value);
        }

        _beforePreviousPrintable = _previousPrintable;
        _previousPrintable = printable;
        _previousByte = value;
    }

    private void FlushPending(ICollection<ExtractedString> output)
    {
        if (!_hasInput)
        {
            return;
        }

        // Two non-printable sentinels terminate both narrow and zero-separated
        // wide runs without becoming part of the result.
        ConsumeByte(0xFF, _nextAddress, output);
        ConsumeByte(0xFF, checked(_nextAddress + 1), output);
    }

    private void Append(byte value)
    {
        if (_display.Length < _options.MaximumStringLength)
        {
            _display.Append((char)value);
        }

        _runLength++;
    }

    private void ResetWith(byte value)
    {
        ClearRun();
        Append(value);
    }

    private void ResetWith(byte first, byte second)
    {
        ClearRun();
        Append(first);
        Append(second);
    }

    private void ExcludeLastCharacter()
    {
        _runLength--;
        if (_runLength < _display.Length)
        {
            _display.Length = checked((int)_runLength);
        }
    }

    private void Emit(ulong currentAddress, bool isWide, int bias, ICollection<ExtractedString> output)
    {
        var lengthInBytes = isWide ? checked(_runLength * 2L) : _runLength;
        var startAddress = checked(currentAddress - (ulong)bias - (ulong)lengthInBytes);
        var enabled = isWide ? _options.DetectUnicode : _options.DetectAscii;

        if (enabled)
        {
            output.Add(new ExtractedString(
                startAddress,
                lengthInBytes > int.MaxValue ? int.MaxValue : (int)lengthInBytes,
                _display.ToString(),
                isWide ? EncodingKind.Utf16LittleEndian : EncodingKind.Ascii,
                _regionKind));
        }

        ClearRun();
    }

    private void ClearRun()
    {
        _runLength = 0;
        _display.Clear();
    }

    private void ResetState()
    {
        ClearRun();
        _previousByte = 0;
        _previousPrintable = false;
        _beforePreviousPrintable = false;
        _nextAddress = 0;
        _hasInput = false;
    }

    private static bool IsProcessHackerPrintable(byte value) =>
        value is 0x09 or 0x0A or 0x0D or >= 0x20 and <= 0x7E;
}
