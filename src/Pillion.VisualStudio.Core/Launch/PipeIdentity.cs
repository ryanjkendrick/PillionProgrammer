using System;
using System.Security.Principal;

namespace Pillion.VisualStudio.Core;

/// <summary>The SIDs a process identity is known by, as far as owning a named pipe is concerned.</summary>
internal interface IPipeIdentity
{
    /// <summary>Gets the SID of the signed-in user (<c>WindowsIdentity.User</c>).</summary>
    SecurityIdentifier? User { get; }

    /// <summary>
    /// Gets the SID that owns the objects this token creates (<c>WindowsIdentity.Owner</c>): the user for a normal
    /// token, <c>BUILTIN\Administrators</c> (S-1-5-32-544) for an elevated token of an administrator.
    /// </summary>
    SecurityIdentifier? TokenOwner { get; }
}

/// <summary>Decides who may own the pipe Pillion Host serves.</summary>
internal static class PipeIdentity
{
    /// <summary>Gets the identity of the current process.</summary>
    public static IPipeIdentity Current { get; } = new WindowsPipeIdentity();

    /// <summary>
    /// Determines whether <paramref name="owner"/> is the user or the token owner of <paramref name="identity"/>.
    /// A missing owner is never trusted, and neither is any other SID.
    /// </summary>
    /// <param name="owner">The owner of the pipe.</param>
    /// <param name="identity">The identity of the current process.</param>
    /// <returns><see langword="true"/> when the owner may be trusted.</returns>
    public static bool IsTrustedOwner(SecurityIdentifier? owner, IPipeIdentity identity)
    {
        if (identity is null)
        {
            throw new ArgumentNullException(nameof(identity));
        }

        if (owner is null)
        {
            return false;
        }

        return owner.Equals(identity.User) || owner.Equals(identity.TokenOwner);
    }

    private sealed class WindowsPipeIdentity : IPipeIdentity
    {
        public SecurityIdentifier? User
        {
            get
            {
                using var identity = WindowsIdentity.GetCurrent();
                return identity.User;
            }
        }

        public SecurityIdentifier? TokenOwner
        {
            get
            {
                using var identity = WindowsIdentity.GetCurrent();
                return identity.Owner;
            }
        }
    }
}
