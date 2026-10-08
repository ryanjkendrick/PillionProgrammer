using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Pillion.Common.Secrets;

/// <summary>
/// An <see cref="ISecretStore"/> backed by the Windows Credential Manager. Each secret is a generic credential with
/// the target name <c>PillionProgrammer/&lt;name&gt;</c>, the current user as its user name, local-machine
/// persistence, and the value stored as UTF-16 bytes in the credential blob.
/// </summary>
public sealed partial class WindowsCredentialManagerSecretStore : ISecretStore
{
    /// <summary>The prefix of every credential target name this store creates.</summary>
    public const string TargetPrefix = "PillionProgrammer/";

    /// <summary>Initializes a new instance of the <see cref="WindowsCredentialManagerSecretStore"/> class.</summary>
    /// <exception cref="PlatformNotSupportedException">The current platform is not Windows.</exception>
    public WindowsCredentialManagerSecretStore()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The Windows Credential Manager is only available on Windows.");
        }
    }

    /// <inheritdoc/>
    public string? Get(string name)
    {
        SecretNames.Validate(name);

        if (!NativeMethods.CredRead(TargetPrefix + name, NativeMethods.CredTypeGeneric, 0, out var handle))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == NativeMethods.ErrorNotFound)
            {
                return null;
            }

            throw Failure("read", error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeMethods.Credential>(handle);
            return ReadBlob(credential);
        }
        finally
        {
            NativeMethods.CredFree(handle);
        }
    }

    /// <inheritdoc/>
    public unsafe void Set(string name, string value)
    {
        SecretNames.Validate(name);
        SecretNames.ValidateValue(value);

        var blob = Encoding.Unicode.GetBytes(value);
        var target = Marshal.StringToHGlobalUni(TargetPrefix + name);
        var userName = Marshal.StringToHGlobalUni(Environment.UserName);
        try
        {
            fixed (byte* blobPointer = blob)
            {
                var credential = new NativeMethods.Credential
                {
                    Type = NativeMethods.CredTypeGeneric,
                    TargetName = target,
                    CredentialBlobSize = (uint)blob.Length,
                    CredentialBlob = (IntPtr)blobPointer,
                    Persist = NativeMethods.CredPersistLocalMachine,
                    UserName = userName,
                };

                if (!NativeMethods.CredWrite(ref credential, 0))
                {
                    throw Failure("write", Marshal.GetLastPInvokeError());
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
            Marshal.FreeHGlobal(target);
            Marshal.FreeHGlobal(userName);
        }
    }

    /// <inheritdoc/>
    public bool Delete(string name)
    {
        SecretNames.Validate(name);

        if (NativeMethods.CredDelete(TargetPrefix + name, NativeMethods.CredTypeGeneric, 0))
        {
            return true;
        }

        var error = Marshal.GetLastPInvokeError();
        return error == NativeMethods.ErrorNotFound ? false : throw Failure("delete", error);
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> List()
    {
        if (!NativeMethods.CredEnumerate(TargetPrefix + "*", 0, out var count, out var handle))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == NativeMethods.ErrorNotFound ? [] : throw Failure("list", error);
        }

        try
        {
            var names = new List<string>((int)count);
            for (var i = 0; i < count; i++)
            {
                var entry = Marshal.ReadIntPtr(handle, i * IntPtr.Size);
                var credential = Marshal.PtrToStructure<NativeMethods.Credential>(entry);
                if (credential.Type != NativeMethods.CredTypeGeneric)
                {
                    continue;
                }

                var target = Marshal.PtrToStringUni(credential.TargetName);
                if (target is not null && target.StartsWith(TargetPrefix, StringComparison.OrdinalIgnoreCase) && target.Length > TargetPrefix.Length)
                {
                    names.Add(target[TargetPrefix.Length..]);
                }
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
        finally
        {
            NativeMethods.CredFree(handle);
        }
    }

    private static string ReadBlob(NativeMethods.Credential credential)
    {
        if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
        {
            return string.Empty;
        }

        var bytes = new byte[credential.CredentialBlobSize];
        Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
        try
        {
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static SecretStoreException Failure(string operation, int win32Error)
        => new($"The Windows Credential Manager could not {operation} a secret (Win32 error {win32Error}: {new Win32Exception(win32Error).Message}).");

    private static partial class NativeMethods
    {
        public const uint CredTypeGeneric = 1;
        public const uint CredPersistLocalMachine = 2;
        public const int ErrorNotFound = 1168;

        [StructLayout(LayoutKind.Sequential)]
        public struct Credential
        {
            public uint Flags;
            public uint Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public long LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CredWrite(ref Credential credential, uint flags);

        [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CredDelete(string target, uint type, uint flags);

        [LibraryImport("advapi32.dll", EntryPoint = "CredEnumerateW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CredEnumerate(string? filter, uint flags, out uint count, out IntPtr credentials);

        [LibraryImport("advapi32.dll")]
        public static partial void CredFree(IntPtr buffer);
    }
}
