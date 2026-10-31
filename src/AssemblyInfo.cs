using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

// Compiled against publicized game assemblies; CoreCLR honours this attribute so internal game
// members can be used directly (no reflection, no per-call overhead).
[assembly: IgnoresAccessChecksTo("Cosmoteer")]
[assembly: IgnoresAccessChecksTo("HalflingCore")]
[assembly: IgnoresAccessChecksTo("HalflingPlatformWDX")]
[assembly: InternalsVisibleTo("Performance.Tests")]
[assembly: SupportedOSPlatform("windows7.0")]

// The runtime matches this attribute by full name, so it must live in this namespace.
// ReSharper disable once CheckNamespace
namespace System.Runtime.CompilerServices
{
    /// <summary>The runtime reads the assembly name from the attribute's metadata; no property is needed.</summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class IgnoresAccessChecksToAttribute : Attribute
    {
        public IgnoresAccessChecksToAttribute(string assemblyName) => _ = assemblyName;
    }
}
