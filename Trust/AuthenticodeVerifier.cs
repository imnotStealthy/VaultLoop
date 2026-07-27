using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace ReplayGlitchGTA;

/// <summary>
/// Authenticode verification of a file on disk, and the identity of its signer.
/// </summary>
/// <remarks>
/// The file handle held by the caller is passed to WinVerifyTrust so that the bytes verified
/// are the bytes the caller already opened: verifying by path alone would leave a window in
/// which the file is replaced between the verification and the use.
/// </remarks>
internal static class AuthenticodeVerifier
{
    private const uint WinTrustUiNone = 2;
    private const uint WinTrustRevokeWholeChain = 1;
    private const uint WinTrustChoiceFile = 1;
    private const uint WinTrustStateActionIgnore = 0;
    private const uint WinTrustRevocationCheckChainExcludeRoot = 0x80;
    private const uint WinTrustDisableMd2Md4 = 0x2000;

    private static readonly Guid GenericVerifyV2Action =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    /// <summary>True when the file carries a signature that chains to a trusted root.</summary>
    internal static bool IsSignatureValid(string executablePath, SafeFileHandle fileHandle)
    {
        var fileInfo = new WinTrustFileInfo
        {
            StructSize = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo)),
            FilePath = executablePath,
            FileHandle = fileHandle.DangerousGetHandle(),
            KnownSubject = IntPtr.Zero
        };
        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustFileInfo)));
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var trustData = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf(typeof(WinTrustData)),
                UiChoice = WinTrustUiNone,
                RevocationChecks = WinTrustRevokeWholeChain,
                UnionChoice = WinTrustChoiceFile,
                FileInfo = fileInfoPointer,
                StateAction = WinTrustStateActionIgnore,
                ProviderFlags = WinTrustRevocationCheckChainExcludeRoot |
                                WinTrustDisableMd2Md4
            };
            var action = GenericVerifyV2Action;
            return WinVerifyTrust(IntPtr.Zero, ref action, ref trustData) == 0;
        }
        finally
        {
            Marshal.DestroyStructure(fileInfoPointer, typeof(WinTrustFileInfo));
            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }

    /// <summary>
    /// The simple name of the signer certificate. Throws when the file carries no readable
    /// signer, which the callers translate into a rejection.
    /// </summary>
    internal static string ReadPublisherName(string executablePath)
    {
        using var signer = X509Certificate.CreateFromSignedFile(executablePath);
        using var certificate = new X509Certificate2(signer);
        return certificate.GetNameInfo(X509NameType.SimpleName, false).Trim();
    }

    internal static bool IsRockstarPublisher(string executablePath)
    {
        var publisher = ReadPublisherName(executablePath);
        return publisher.Equals("Rockstar Games, Inc.", StringComparison.OrdinalIgnoreCase) ||
               publisher.Equals("Rockstar Games, Inc", StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle, ref Guid actionId, ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        internal uint StructSize;

        [MarshalAs(UnmanagedType.LPWStr)]
        internal string FilePath;

        internal IntPtr FileHandle;
        internal IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        internal uint StructSize;
        internal IntPtr PolicyCallbackData;
        internal IntPtr SipClientData;
        internal uint UiChoice;
        internal uint RevocationChecks;
        internal uint UnionChoice;
        internal IntPtr FileInfo;
        internal uint StateAction;
        internal IntPtr StateData;
        internal IntPtr UrlReference;
        internal uint ProviderFlags;
        internal uint UiContext;
        internal IntPtr SignatureSettings;
    }
}
