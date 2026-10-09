using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.Host.InlineEdit;
using Pillion.Host.Rpc;
using Pillion.ModelGateway;
using Pillion.ModelGateway.Editing;

// In the Pillion.Host namespace so AddPillionHost registers the service with one line and no extra using directive.
namespace Pillion.Host;

/// <summary>Registers the inline edit service.</summary>
internal static class InlineEditServiceCollectionExtensions
{
    /// <summary>
    /// Adds the inline edit engine, its prompt builder, the Roslyn syntax validator and the <see cref="IInlineEditService"/>
    /// RPC target (work item 1.8). Needs the Model Gateway and the feature flags registered by <c>AddPillionHost</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddPillionInlineEdit(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(InlineEditTemplates.Default);
        services.TryAddSingleton(sp => new InlineEditPromptBuilder(sp.GetRequiredService<InlineEditTemplates>()));
        services.TryAddSingleton<ISyntaxValidator>(RoslynSyntaxValidator.Instance);
        services.TryAddSingleton<InlineEditConversations>();
        services.TryAddSingleton(sp => new InlineEditEngine(
            sp.GetRequiredService<IModelGateway>(),
            sp.GetRequiredService<InlineEditPromptBuilder>(),
            sp.GetRequiredService<ISyntaxValidator>(),
            sp.GetRequiredService<InlineEditConversations>(),
            sp.GetService<ILogger<InlineEditEngine>>()));
        services.TryAddSingleton(sp => new InlineEditRpcService(sp.GetRequiredService<InlineEditEngine>(), sp.GetService<IFeatureFlags>()));
        services.AddRpcTarget<IInlineEditService>(sp => sp.GetRequiredService<InlineEditRpcService>());
        return services;
    }
}
