namespace Pillion.ModelGateway;

// Pillion.ModelGateway puts every model provider behind one gateway used by Pillion Host:
//   - provider adapters for cloud APIs and local runtimes,
//   - routing of requests by TaskClass and Profile, with offline fallbacks,
//   - streaming, cancellation and token accounting,
//   - the implementation behind IModelService.
// None of that is implemented yet; this placeholder keeps the project building.

/// <summary>Placeholder type that identifies the Pillion.ModelGateway assembly until real services are added.</summary>
public static class ModelGatewayAssemblyMarker
{
    /// <summary>Gets the name of this assembly.</summary>
    public static string AssemblyName => typeof(ModelGatewayAssemblyMarker).Assembly.GetName().Name ?? string.Empty;
}
