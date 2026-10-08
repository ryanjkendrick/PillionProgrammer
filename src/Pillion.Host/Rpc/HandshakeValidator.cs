using System.Globalization;
using Pillion.Contracts;

namespace Pillion.Host.Rpc;

/// <summary>The handshake check that failed, or <see cref="Passed"/>.</summary>
internal enum HandshakeCheck
{
    /// <summary>All checks passed.</summary>
    Passed,

    /// <summary>The request was missing.</summary>
    Request,

    /// <summary>The process connected to the pipe is not the <c>--parent-pid</c> process.</summary>
    PipeClientProcess,

    /// <summary>The protocol token does not match the launch token.</summary>
    ProtocolToken,

    /// <summary>The request's <see cref="HandshakeRequest.ClientProcessId"/> is not the connected process.</summary>
    ClientProcessId,

    /// <summary>The request's contract major version differs from the host's.</summary>
    ContractVersion,
}

/// <summary>The outcome of validating a <see cref="HandshakeRequest"/>.</summary>
/// <param name="Check">The check that failed, or <see cref="HandshakeCheck.Passed"/>.</param>
/// <param name="Reason">A reason that names the failed check without revealing the token; <see langword="null"/> when passed.</param>
internal readonly record struct HandshakeOutcome(HandshakeCheck Check, string? Reason)
{
    /// <summary>Gets a value indicating whether every check passed.</summary>
    public bool Accepted => Check == HandshakeCheck.Passed;
}

/// <summary>The handshake rules, kept free of transport concerns so they can be tested directly.</summary>
internal static class HandshakeValidator
{
    /// <summary>Validates a handshake request.</summary>
    /// <param name="request">The client's request.</param>
    /// <param name="token">The launch token.</param>
    /// <param name="parentProcessId">The <c>--parent-pid</c> value.</param>
    /// <param name="pipeClientProcessId">The pipe client's process id as reported by the OS, if known.</param>
    /// <returns>The outcome. Checks run in this order: pipe client process, token, request process id, contract version.</returns>
    public static HandshakeOutcome Validate(HandshakeRequest? request, LaunchToken token, int parentProcessId, int? pipeClientProcessId)
    {
        ArgumentNullException.ThrowIfNull(token);

        if (request is null)
        {
            return new(HandshakeCheck.Request, "The handshake request is missing.");
        }

        if (pipeClientProcessId is not { } clientId || clientId != parentProcessId)
        {
            return new(HandshakeCheck.PipeClientProcess, "The process connected to the pipe is not the process that launched the host.");
        }

        if (!token.Matches(request.ProtocolToken))
        {
            return new(HandshakeCheck.ProtocolToken, "The protocol token does not match.");
        }

        if (request.ClientProcessId != clientId)
        {
            return new(HandshakeCheck.ClientProcessId, "The client process id in the request does not match the process connected to the pipe.");
        }

        if (!ContractVersion.IsCompatible(request.ContractMajor, request.ContractMinor))
        {
            return new(
                HandshakeCheck.ContractVersion,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Contract version {0}.{1} is not compatible with the host's contract version {2}.",
                    request.ContractMajor,
                    request.ContractMinor,
                    ContractVersion.Text));
        }

        return new(HandshakeCheck.Passed, null);
    }
}
