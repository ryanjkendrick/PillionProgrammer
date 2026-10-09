namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>
/// The Visual Studio version this build of the tests drives. The sources are compiled twice, because no single version
/// of the test framework supports both: Pillion.VisualStudio.IntegrationTests for Visual Studio 2026 (18.x) and
/// Pillion.VisualStudio.IntegrationTests.VS2022 (which defines IDE_VS2022) for Visual Studio 2022 (17.14 or later).
/// </summary>
internal static class IdeTarget
{
#if IDE_VS2022
    /// <summary>Visual Studio 2022.</summary>
    public const VisualStudioVersion Version = VisualStudioVersion.VS2022;
#else
    /// <summary>Visual Studio 2026.</summary>
    public const VisualStudioVersion Version = VisualStudioVersion.VS18;
#endif
}
