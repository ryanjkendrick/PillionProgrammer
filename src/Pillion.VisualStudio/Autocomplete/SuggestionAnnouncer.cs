using System;
using System.Reflection;
using System.Windows.Automation.Peers;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;

namespace Pillion.VisualStudio.Autocomplete;

/// <summary>
/// Announces a new ghost-text suggestion to screen readers with a UI Automation notification raised on the editor
/// (design section 5, Accessibility). Switched off with <c>"autocomplete": { "announceSuggestions": false }</c>.
/// </summary>
internal static class SuggestionAnnouncer
{
    /// <summary>The activity id of the notification, so screen readers can group or filter it.</summary>
    public const string ActivityId = "Pillion.Suggestion";

    private const int MaxSpokenLength = 120;

    /// <summary>Raises the notification on the UI thread (switching to it if needed).</summary>
    /// <param name="joinableTaskFactory">The joinable task factory used to reach the UI thread.</param>
    /// <param name="view">The editor view showing the suggestion.</param>
    /// <param name="suggestion">The suggested text.</param>
    public static void Announce(JoinableTaskFactory joinableTaskFactory, ITextView view, string suggestion)
    {
        if (view is not IWpfTextView wpfView || string.IsNullOrWhiteSpace(suggestion))
        {
            return;
        }

        var raise = RaiseNotification.Value;
        if (raise is null)
        {
            return;
        }

        var text = SpokenText(suggestion);
        joinableTaskFactory.RunAsync(async () =>
        {
            await joinableTaskFactory.SwitchToMainThreadAsync();
            if (wpfView.IsClosed)
            {
                return;
            }

            var element = wpfView.VisualElement;
            var peer = UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
            if (peer is not null)
            {
                raise(peer, text);
            }
        }).FileAndForget("pillion/autocomplete/announce");
    }

    /// <summary>
    /// <c>AutomationPeer.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.ImportantMostRecent, text, activityId)</c>.
    /// It is in the .NET Framework 4.8 runtime that Visual Studio runs on but not in the net48 reference assemblies, so it
    /// is bound by reflection; <see langword="null"/> when the runtime lacks it.
    /// </summary>
    private static readonly Lazy<Action<AutomationPeer, string>?> RaiseNotification = new(() =>
    {
        try
        {
            var method = typeof(AutomationPeer).GetMethod("RaiseNotificationEvent", BindingFlags.Public | BindingFlags.Instance);
            if (method?.GetParameters() is not { Length: 4 } parameters)
            {
                return null;
            }

            var kind = Enum.Parse(parameters[0].ParameterType, "Other");
            var processing = Enum.Parse(parameters[1].ParameterType, "ImportantMostRecent");
            return (peer, text) => method.Invoke(peer, [kind, processing, text, ActivityId]);
        }
#pragma warning disable CA1031 // Without the API there is no announcement.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    });

    /// <summary>Builds the spoken text: the first line of the suggestion and how many more lines follow.</summary>
    /// <param name="suggestion">The suggestion.</param>
    /// <returns>For example "Suggestion: return x + y; (2 more lines)".</returns>
    internal static string SpokenText(string suggestion)
    {
        var lines = suggestion.Replace("\r\n", "\n").Split('\n');
        var first = string.Empty;
        var index = 0;
        for (; index < lines.Length; index++)
        {
            if (lines[index].Trim().Length > 0)
            {
                first = lines[index].Trim();
                break;
            }
        }

        if (first.Length > MaxSpokenLength)
        {
            first = first.Substring(0, MaxSpokenLength);
        }

        var more = 0;
        for (var i = index + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length > 0)
            {
                more++;
            }
        }

        return "Suggestion: " + first + (more switch
        {
            0 => string.Empty,
            1 => " (1 more line)",
            _ => " (" + more.ToString(System.Globalization.CultureInfo.InvariantCulture) + " more lines)",
        });
    }
}
