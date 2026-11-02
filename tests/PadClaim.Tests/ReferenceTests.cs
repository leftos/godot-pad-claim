using System.Reflection;

namespace PadClaim.Tests;

/// <summary>Pins that neither the core nor its tests pull in the Godot engine, read from each assembly's own metadata.</summary>
public sealed class ReferenceTests
{
    /// <summary>The core is engine-free, so it runs anywhere the framework does.</summary>
    [Fact]
    public void CoreReferencesNoGodotAssembly() => Assert.Empty(GodotReferences(typeof(PadClaim.AssemblyMarker).Assembly));

    /// <summary>The tests are engine-free too, so they run in the test host rather than inside the engine.</summary>
    [Fact]
    public void TestsReferenceNoGodotAssembly() => Assert.Empty(GodotReferences(typeof(ReferenceTests).Assembly));

    private static string[] GodotReferences(Assembly assembly) =>
        [
            .. assembly
                .GetReferencedAssemblies()
                .Select(reference => reference.Name)
                .OfType<string>()
                .Where(name => name.StartsWith("Godot", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal),
        ];
}
