using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>One entry of the Visual Studio Activity Log.</summary>
/// <param name="Type">Information, Warning or Error.</param>
/// <param name="Source">The component that wrote the entry.</param>
/// <param name="Description">The message.</param>
/// <param name="ComponentGuid">The package or component GUID, when the entry has one.</param>
public sealed record ActivityLogEntry(string Type, string Source, string Description, string ComponentGuid)
{
    /// <summary>Gets a value indicating whether the entry is an error.</summary>
    public bool IsError => string.Equals(Type, "Error", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override string ToString() => $"{Type} [{Source}] {Description} {ComponentGuid}".Trim();
}

/// <summary>The Activity Log as XML text and its parsed entries.</summary>
public sealed class ActivityLog
{
    private ActivityLog(string rawText, IReadOnlyList<ActivityLogEntry> entries)
    {
        RawText = rawText;
        Entries = entries;
    }

    /// <summary>Gets the log as Visual Studio returned it, trimmed to valid XML.</summary>
    public string RawText { get; }

    /// <summary>Gets the parsed entries.</summary>
    public IReadOnlyList<ActivityLogEntry> Entries { get; }

    /// <summary>Gets the entries that mention Pillion (by name, assembly or package GUID).</summary>
    /// <param name="packageGuid">The package GUID.</param>
    /// <returns>The matching entries.</returns>
    public IEnumerable<ActivityLogEntry> EntriesAbout(Guid packageGuid)
    {
        var guidText = packageGuid.ToString("D");
        return Entries.Where(e =>
            e.Source.IndexOf("Pillion", StringComparison.OrdinalIgnoreCase) >= 0
            || e.Description.IndexOf("Pillion", StringComparison.OrdinalIgnoreCase) >= 0
            || e.Description.IndexOf(guidText, StringComparison.OrdinalIgnoreCase) >= 0
            || e.ComponentGuid.IndexOf(guidText, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>Parses the buffer returned by the Activity Log service.</summary>
    /// <param name="text">The buffer, or <see langword="null"/>.</param>
    /// <returns>The log; empty when there is no buffer.</returns>
    public static ActivityLog Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ActivityLog(string.Empty, []);
        }

        // The service returns a sequence of <entry> elements with no root, followed by NUL characters.
        var end = text!.LastIndexOf('>');
        var fragment = end >= 0 ? text.Substring(0, end + 1) : text;
        if (fragment.StartsWith("<?xml", StringComparison.Ordinal))
        {
            fragment = fragment.Substring(fragment.IndexOf("?>", StringComparison.Ordinal) + 2);
        }

        var xml = fragment.TrimStart().StartsWith("<activity", StringComparison.Ordinal)
            ? fragment
            : "<activity>" + Environment.NewLine + fragment + Environment.NewLine + "</activity>";
        try
        {
            var document = XDocument.Parse(xml);
            var entries = document.Descendants("entry")
                .Select(e => new ActivityLogEntry(
                    (string?)e.Element("type") ?? string.Empty,
                    (string?)e.Element("source") ?? string.Empty,
                    (string?)e.Element("description") ?? string.Empty,
                    (string?)e.Element("guid") ?? string.Empty))
                .ToList();
            return new ActivityLog(xml, entries);
        }
        catch (XmlException ex)
        {
            throw new InvalidOperationException("The Activity Log buffer is not valid XML: " + ex.Message, ex);
        }
    }
}

/// <summary>
/// The undocumented interface behind <c>SVsActivityLog</c> that returns the in-memory Activity Log; the integration
/// test framework uses it the same way to save the log when a test fails.
/// </summary>
[ComImport]
[Guid("4F111D70-F291-428D-8E40-CB1D4B1A7BDE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVsActivityLogDumper
{
    /// <summary>Returns the in-memory Activity Log as XML.</summary>
    /// <returns>The log.</returns>
    [MethodImpl(MethodImplOptions.InternalCall)]
    [return: MarshalAs(UnmanagedType.BStr)]
    string GetActivityLogBuffer();
}
