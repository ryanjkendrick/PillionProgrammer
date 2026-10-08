using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Pillion.VisualStudio.Core;

/// <summary>Reads a file's embedded Authenticode signer and verifies it with <c>WinVerifyTrust</c>, offline.</summary>
public static class AuthenticodeInspector
{
    /// <summary>Gets the signer of a file.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The signer, or <see langword="null"/> when the file has no embedded Authenticode signature.</returns>
    public static SignerInfo? GetSigner(string path)
    {
        X509Certificate2 certificate;
        try
        {
#pragma warning disable SYSLIB0057 // net48 has no X509CertificateLoader.
            using var signed = X509Certificate.CreateFromSignedFile(path);
            certificate = new X509Certificate2(signed);
#pragma warning restore SYSLIB0057
        }
        catch (CryptographicException)
        {
            return null;
        }

        using (certificate)
        {
            var result = NativeMethods.VerifyFile(path);
            return new SignerInfo(certificate.Thumbprint, certificate.Subject, Classify(result));
        }
    }

    /// <summary>Maps a <c>WinVerifyTrust</c> result to <see cref="SignatureIntegrity"/>.</summary>
    /// <param name="hresult">The result.</param>
    /// <returns>
    /// <see cref="SignatureIntegrity.Trusted"/> for success; <see cref="SignatureIntegrity.UntrustedChain"/> when only
    /// the certificate chain is the problem (untrusted or test root, broken chain, expired without a timestamp);
    /// otherwise <see cref="SignatureIntegrity.Invalid"/> (bad digest, no signature, explicitly distrusted, and so on).
    /// </returns>
    public static SignatureIntegrity Classify(int hresult) => unchecked((uint)hresult) switch
    {
        0 => SignatureIntegrity.Trusted,
        0x800B0109 => SignatureIntegrity.UntrustedChain, // CERT_E_UNTRUSTEDROOT
        0x800B010A => SignatureIntegrity.UntrustedChain, // CERT_E_CHAINING
        0x800B010D => SignatureIntegrity.UntrustedChain, // CERT_E_UNTRUSTEDTESTROOT
        0x800B0101 => SignatureIntegrity.UntrustedChain, // CERT_E_EXPIRED
        _ => SignatureIntegrity.Invalid,
    };

    private static class NativeMethods
    {
        private const uint WTD_UI_NONE = 2;
        private const uint WTD_REVOKE_NONE = 0;
        private const uint WTD_CHOICE_FILE = 1;
        private const uint WTD_STATEACTION_VERIFY = 1;
        private const uint WTD_STATEACTION_CLOSE = 2;
        private const uint WTD_REVOCATION_CHECK_NONE = 0x10;
        private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;

        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        public static int VerifyFile(string path)
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = path,
            };

            var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            try
            {
                Marshal.StructureToPtr(fileInfo, filePtr, fDeleteOld: false);
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice = WTD_UI_NONE,
                    fdwRevocationChecks = WTD_REVOKE_NONE,
                    dwUnionChoice = WTD_CHOICE_FILE,
                    pFile = filePtr,
                    dwStateAction = WTD_STATEACTION_VERIFY,
                    dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL,
                };

                var action = GenericVerifyV2;
                var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

                data.dwStateAction = WTD_STATEACTION_CLOSE;
                _ = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                return result;
            }
            finally
            {
                Marshal.DestroyStructure<WINTRUST_FILE_INFO>(filePtr);
                Marshal.FreeHGlobal(filePtr);
            }
        }

        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }
    }
}
