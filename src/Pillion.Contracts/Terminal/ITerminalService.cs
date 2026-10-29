using System.Threading;
using System.Threading.Tasks;

namespace Pillion.Contracts;

/// <summary>
/// The Pillion terminal (design section 5, Tool windows; section 8, Command execution), implemented by the host and called by
/// the VSIX. Every pseudo-console the Command Runner opens (a session's command shell, a background process, a developer shell
/// the user opened) is a terminal with an id; the host pushes its rendered screen as the notification <c>terminalScreenChanged</c>
/// (<see cref="TerminalScreen"/>) and background-process state as <c>backgroundProcessChanged</c>
/// (<see cref="BackgroundProcessInfo"/>), neither replayed to a client that connects later, which calls
/// <see cref="ListTerminalsAsync"/> and <see cref="GetScreenAsync"/>. Added in contract 1.4 (work item 3.5; implemented by 3.11,
/// consumed by 3.15). A 1.3 host answers every method with StreamJsonRpc's method-not-found error.
/// </summary>
public interface ITerminalService
{
    /// <summary>
    /// Writes text to a terminal's input, as the user typed it in the Terminal window's input line (the way a prompt waiting for
    /// input is answered; the agent never answers one). Line endings are sent as given.
    /// </summary>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="text">The text, including any newline.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>A task that completes when the text has been written. An unknown or closed terminal fails with <see cref="PillionErrorCode.PolicyDenied"/>.</returns>
    Task SendInputAsync(string terminalId, string text, CancellationToken cancellationToken);

    /// <summary>Resizes a terminal's pseudo-console to the Terminal window's size.</summary>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="columns">The width in cells.</param>
    /// <param name="rows">The height in cells.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>A task that completes when the console has been resized.</returns>
    Task ResizeAsync(string terminalId, int columns, int rows, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a shell for the user with the Developer PowerShell environment of the launching Visual Studio (its installation
    /// path from <see cref="HandshakeRequest.VisualStudioInstallationPath"/>), starting in the solution root. Commands the user
    /// runs there are visible to <c>@terminal</c> and the agent's <c>get_terminal_output</c>.
    /// </summary>
    /// <param name="request">The shell and working directory.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The new terminal.</returns>
    Task<TerminalInfo> OpenDeveloperShellAsync(OpenDeveloperShellRequest request, CancellationToken cancellationToken);

    /// <summary>Gets a terminal's whole rendered screen and scrollback, for a Terminal window that opens mid-session.</summary>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The screen, with the whole scrollback in <see cref="TerminalScreen.ScrollbackDelta"/>.</returns>
    Task<TerminalScreen> GetScreenAsync(string terminalId, CancellationToken cancellationToken);

    /// <summary>Lists the terminals the host has open.</summary>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The terminals, oldest first.</returns>
    Task<TerminalInfo[]> ListTerminalsAsync(CancellationToken cancellationToken);

    /// <summary>Closes a terminal, killing its process tree within 1 s through the session's Job Object. A background process is stopped the same way.</summary>
    /// <param name="terminalId">The terminal.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>A task that completes when the process tree is gone.</returns>
    Task CloseTerminalAsync(string terminalId, CancellationToken cancellationToken);
}
