using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// Where the Options pages keep provider API keys. The extension writes and deletes keys and checks whether one is
/// stored; it never reads a key back (only Pillion Host does, when it calls the provider).
/// </summary>
public interface ICredentialStore
{
    /// <summary>Gets a value indicating whether a key is stored for a provider. The key itself is not read.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <returns><see langword="true"/> when a key is stored.</returns>
    bool Exists(string providerId);

    /// <summary>Stores (or replaces) a provider's key.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="secret">The key. Never logged or included in an exception message.</param>
    void Write(string providerId, string secret);

    /// <summary>Deletes a provider's key.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <returns><see langword="true"/> when a key was deleted; <see langword="false"/> when none was stored.</returns>
    bool Delete(string providerId);
}

/// <summary>
/// An <see cref="ICredentialStore"/> over the Windows Credential Manager, compatible with Pillion Host's
/// <c>WindowsCredentialManagerSecretStore</c> (Pillion.Common, .NET 10, which cannot be loaded into devenv.exe): a
/// generic credential with target <c>PillionProgrammer/&lt;provider id&gt;</c>, the current user name, local-machine
/// persistence and the key as UTF-16 bytes in the blob.
/// </summary>
public sealed class CredentialManagerStore : ICredentialStore
{
    /// <summary>The prefix of every credential target name, shared with Pillion Host.</summary>
    public const string TargetPrefix = "PillionProgrammer/";

    /// <summary>The Credential Manager limit for a credential blob (CRED_MAX_CREDENTIAL_BLOB_SIZE = 5 * 512 bytes).</summary>
    public const int MaxBlobBytes = 5 * 512;

    /// <summary>Gets the credential target name for a provider.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <returns><c>PillionProgrammer/&lt;provider id&gt;</c>.</returns>
    public static string TargetFor(string providerId)
    {
        ValidateName(providerId);
        return TargetPrefix + providerId;
    }

    /// <summary>
    /// Checks that a provider id can name a secret: 1 to 128 letters, digits, '.', '_' or '-' (the host's rule).
    /// </summary>
    /// <param name="providerId">The provider id.</param>
    /// <returns><see langword="true"/> when the id is valid.</returns>
    public static bool IsValidName(string? providerId)
    {
        if (string.IsNullOrEmpty(providerId) || providerId!.Length > 128)
        {
            return false;
        }

        foreach (var c in providerId)
        {
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c is '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public bool Exists(string providerId)
    {
        var target = TargetFor(providerId);
        if (!NativeMethods.CredReadW(target, NativeMethods.CRED_TYPE_GENERIC, 0, out var handle))
        {
            var error = Marshal.GetLastWin32Error();
            return error == NativeMethods.ERROR_NOT_FOUND ? false : throw Failure("check", error);
        }

        // Presence only: the blob is never copied out of the native buffer.
        NativeMethods.CredFree(handle);
        return true;
    }

    /// <inheritdoc/>
    public void Write(string providerId, string secret)
    {
        var target = TargetFor(providerId);
        if (string.IsNullOrEmpty(secret))
        {
            throw new ArgumentException("A key must not be empty. Use Delete to remove a key.", nameof(secret));
        }

        if (secret.Length * sizeof(char) > MaxBlobBytes)
        {
            throw new ArgumentException(
                "The key is too long to store: the limit is " + (MaxBlobBytes / sizeof(char)) + " characters.",
                nameof(secret));
        }

        var blob = Encoding.Unicode.GetBytes(secret);
        var pinned = GCHandle.Alloc(blob, GCHandleType.Pinned);
        var targetPointer = Marshal.StringToHGlobalUni(target);
        var userPointer = Marshal.StringToHGlobalUni(Environment.UserName);
        try
        {
            var credential = new NativeMethods.CREDENTIAL
            {
                Type = NativeMethods.CRED_TYPE_GENERIC,
                TargetName = targetPointer,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = pinned.AddrOfPinnedObject(),
                Persist = NativeMethods.CRED_PERSIST_LOCAL_MACHINE,
                UserName = userPointer,
            };

            if (!NativeMethods.CredWriteW(ref credential, 0))
            {
                throw Failure("store", Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Array.Clear(blob, 0, blob.Length);
            pinned.Free();
            Marshal.FreeHGlobal(targetPointer);
            Marshal.FreeHGlobal(userPointer);
        }
    }

    /// <inheritdoc/>
    public bool Delete(string providerId)
    {
        var target = TargetFor(providerId);
        if (NativeMethods.CredDeleteW(target, NativeMethods.CRED_TYPE_GENERIC, 0))
        {
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        return error == NativeMethods.ERROR_NOT_FOUND ? false : throw Failure("delete", error);
    }

    /// <summary>Reads a stored key. For tests only: the extension never reads keys back.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <returns>The key, or <see langword="null"/> when none is stored.</returns>
    internal static string? ReadForTest(string providerId)
    {
        var target = TargetFor(providerId);
        if (!NativeMethods.CredReadW(target, NativeMethods.CRED_TYPE_GENERIC, 0, out var handle))
        {
            var error = Marshal.GetLastWin32Error();
            return error == NativeMethods.ERROR_NOT_FOUND ? null : throw Failure("read", error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeMethods.CREDENTIAL>(handle);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return string.Empty;
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            NativeMethods.CredFree(handle);
        }
    }

    private static void ValidateName(string providerId)
    {
        if (!IsValidName(providerId))
        {
            throw new ArgumentException("A provider id must be 1 to 128 letters, digits, '.', '_' or '-'.", nameof(providerId));
        }
    }

    private static InvalidOperationException Failure(string operation, int win32Error) =>
        new("The Windows Credential Manager could not " + operation + " the key (Win32 error " + win32Error + ": "
            + new Win32Exception(win32Error).Message + ").");
}
