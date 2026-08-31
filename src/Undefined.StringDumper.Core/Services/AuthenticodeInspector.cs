using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace Undefined.StringDumper.Core.Services;

internal static partial class AuthenticodeInspector
{
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionIgnore = 0;
    private const uint WtdRevocationCheckNone = 0x10;
    private const uint WtdCacheOnlyUrlRetrieval = 0x1000;
    private const uint ErrorSuccess = 0;
    private const uint TrustENoSignature = 0x800B0100;
    private const uint TrustEProviderUnknown = 0x800B0001;
    private const uint TrustESubjectFormUnknown = 0x800B0003;
    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    internal static (string? Status, string? SignerName) Inspect(string? path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return (null, null);
        }

        nint fileInfoPointer = 0;
        nint pathPointer = 0;
        try
        {
            pathPointer = Marshal.StringToCoTaskMemUni(path);
            var fileInfo = new WinTrustFileInfo
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = pathPointer,
            };
            fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);

            var trustData = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = WtdUiNone,
                RevocationChecks = WtdRevokeNone,
                UnionChoice = WtdChoiceFile,
                InfoStruct = fileInfoPointer,
                StateAction = WtdStateActionIgnore,
                ProvFlags = WtdRevocationCheckNone | WtdCacheOnlyUrlRetrieval,
            };

            var actionId = WinTrustActionGenericVerifyV2;
            var status = unchecked((uint)WinVerifyTrust(new nint(-1), ref actionId, ref trustData));
            if (status == ErrorSuccess)
            {
                return ("已验证", TryGetSignerName(path));
            }

            return status is TrustENoSignature or TrustEProviderUnknown or TrustESubjectFormUnknown
                ? ("未签名", null)
                : ($"签名验证失败 (0x{status:x8})", TryGetSignerName(path));
        }
        catch (CryptographicException)
        {
            return ("未签名", null);
        }
        catch (IOException)
        {
            return (null, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, null);
        }
        finally
        {
            if (fileInfoPointer != 0)
            {
                Marshal.FreeCoTaskMem(fileInfoPointer);
            }

            if (pathPointer != 0)
            {
                Marshal.FreeCoTaskMem(pathPointer);
            }
        }
    }

    private static string? TryGetSignerName(string path)
    {
        using var certificate = X509Certificate.CreateFromSignedFile(path);
        var match = CommonNameRegex().Match(certificate.Subject);
        if (!match.Success)
        {
            return string.IsNullOrWhiteSpace(certificate.Subject) ? null : certificate.Subject;
        }

        var name = match.Groups["quoted"].Success
            ? match.Groups["quoted"].Value.Replace("\"\"", "\"", StringComparison.Ordinal)
            : match.Groups["plain"].Value;
        return Regex.Unescape(name).Trim();
    }

    [GeneratedRegex("""(?:^|,\s*)CN=(?:"(?<quoted>(?:[^"]|"")*)"|(?<plain>(?:\\.|[^,])*))""", RegexOptions.IgnoreCase)]
    private static partial Regex CommonNameRegex();

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(nint windowHandle, ref Guid actionId, ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        internal uint StructSize;
        internal nint FilePath;
        internal nint FileHandle;
        internal nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        internal uint StructSize;
        internal nint PolicyCallbackData;
        internal nint SipClientData;
        internal uint UiChoice;
        internal uint RevocationChecks;
        internal uint UnionChoice;
        internal nint InfoStruct;
        internal uint StateAction;
        internal nint StateData;
        internal nint UrlReference;
        internal uint ProvFlags;
        internal uint UiContext;
        internal nint SignatureSettings;
    }
}
