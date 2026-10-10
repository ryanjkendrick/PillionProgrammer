using System;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Text;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.ToolWindows;

/// <summary>
/// Gives C# and Visual Basic code blocks Roslyn's colourisation. Roslyn classifies a text buffer only when the buffer is
/// an open document of some workspace, so each such code block's buffer is opened as the only document of a small
/// private workspace that uses Visual Studio's own Roslyn host services. Only syntax is classified (the project has no
/// references), which colours keywords, strings, comments and numbers as the editor does.
/// </summary>
/// <remarks>
/// The Roslyn editor assemblies this needs (<c>Microsoft.VisualStudio.LanguageServices</c> for the host services and
/// <c>Microsoft.CodeAnalysis.EditorFeatures.Text</c> for <c>AsTextContainer</c>) ship with Visual Studio but are not
/// referenced at compile time, to keep the 17.14 package graph unchanged: they are found by reflection once, and when
/// that fails the code blocks simply stay uncoloured.
/// </remarks>
internal sealed class RoslynCodeWorkspaces
{
    private readonly HostServices _host;
    private readonly Func<ITextBuffer, SourceTextContainer> _asTextContainer;

    private RoslynCodeWorkspaces(HostServices host, Func<ITextBuffer, SourceTextContainer> asTextContainer)
    {
        _host = host;
        _asTextContainer = asTextContainer;
    }

    /// <summary>Finds Visual Studio's Roslyn services. UI thread.</summary>
    /// <param name="componentModel">The MEF component model.</param>
    /// <param name="log">The extension log.</param>
    /// <returns>The factory, or <see langword="null"/> when Roslyn's editor services cannot be found.</returns>
    public static RoslynCodeWorkspaces? TryCreate(IComponentModel componentModel, PillionLog? log)
    {
        try
        {
            var workspaceType = FindType("Microsoft.VisualStudio.LanguageServices.VisualStudioWorkspace", "Microsoft.VisualStudio.LanguageServices");
            var extensionsType = FindType("Microsoft.CodeAnalysis.Text.Extensions", "Microsoft.CodeAnalysis.EditorFeatures.Text");
            var asTextContainer = extensionsType?.GetMethod("AsTextContainer", BindingFlags.Public | BindingFlags.Static, null, [typeof(ITextBuffer)], null);
            if (workspaceType is null || asTextContainer is null)
            {
                log?.Warning("chat.codeview.roslyn_missing", "Roslyn's editor services were not found; C# and Visual Basic code blocks are not coloured.");
                return null;
            }

            var getService = typeof(IComponentModel).GetMethod(nameof(IComponentModel.GetService))!.MakeGenericMethod(workspaceType);
            if (getService.Invoke(componentModel, null) is not Workspace visualStudioWorkspace)
            {
                return null;
            }

            var convert = (Func<ITextBuffer, SourceTextContainer>)Delegate.CreateDelegate(typeof(Func<ITextBuffer, SourceTextContainer>), asTextContainer);
            return new RoslynCodeWorkspaces(visualStudioWorkspace.Services.HostServices, convert);
        }
#pragma warning disable CA1031 // Colourisation is optional: any failure leaves the code blocks plain.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log?.Warning("chat.codeview.roslyn_failed", "Preparing Roslyn colourisation for chat code blocks failed.", ("error", ex.GetType().Name), ("detail", ex.Message));
            return null;
        }
    }

    /// <summary>Opens a buffer as a C# or Visual Basic document so Roslyn classifies it. Dispose the result to close it.</summary>
    /// <param name="buffer">The code block's buffer.</param>
    /// <param name="language"><see cref="LanguageNames.CSharp"/> or <see cref="LanguageNames.VisualBasic"/>.</param>
    /// <returns>The workspace holding the document.</returns>
    public IDisposable Open(ITextBuffer buffer, string language)
    {
        var workspace = new ChatCodeWorkspace(_host);
        workspace.Open(_asTextContainer(buffer), language);
        return workspace;
    }

    private static Type? FindType(string typeName, string assemblyName)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
        if (assembly is null)
        {
            try
            {
                assembly = Assembly.Load(assemblyName);
            }
            catch (Exception ex) when (ex is System.IO.FileNotFoundException or System.IO.FileLoadException or BadImageFormatException)
            {
                return null;
            }
        }

        return assembly.GetType(typeName, throwOnError: false);
    }

    /// <summary>A workspace with one project holding one open document: a chat code block's buffer.</summary>
    private sealed class ChatCodeWorkspace : Workspace
    {
        public ChatCodeWorkspace(HostServices host)
            : base(host, "PillionChatCode")
        {
        }

        public void Open(SourceTextContainer container, string language)
        {
            var projectId = ProjectId.CreateNewId("PillionChatCode");
            var documentId = DocumentId.CreateNewId(projectId, "PillionChatCode");
            var name = language == LanguageNames.VisualBasic ? "code.vb" : "code.cs";
            var document = DocumentInfo.Create(
                documentId,
                name,
                loader: TextLoader.From(TextAndVersion.Create(container.CurrentText, VersionStamp.Create())));
            OnProjectAdded(ProjectInfo.Create(projectId, VersionStamp.Create(), "PillionChatCode", "PillionChatCode", language, documents: [document]));
            OnDocumentOpened(documentId, container);
        }

        protected override void Dispose(bool finalize)
        {
            if (!finalize)
            {
                ClearSolution();
            }

            base.Dispose(finalize);
        }
    }
}
