using System.Reflection;
using Xunit;

namespace SpeechRevolutions.Tests;

/// <summary>
/// The User-Agent is load-bearing: HttpClient sends none of its own and the edge
/// rejects a request without one with a bare 403, so a wrong value is not cosmetic.
/// It used to be a literal, which is how the Python SDK shipped 0.2.1 announcing
/// itself as 0.2.0. These pin it to the assembly so the two cannot diverge again.
/// </summary>
public class VersionTests
{
    private static string PackageVersion()
    {
        var asm = typeof(SpeechRevolutionsClient).Assembly;
        var informational = asm
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational.Substring(0, plus) : informational;
        }
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static string UserAgent() =>
        (string)typeof(SpeechRevolutionsClient)
            .GetField("UserAgent", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    [Fact]
    public void UserAgentCarriesTheAssemblyVersion()
    {
        Assert.Equal($"speechrevolutions-csharp/{PackageVersion()}", UserAgent());
    }

    [Fact]
    public void UserAgentIsNotAPlaceholder()
    {
        var ua = UserAgent();
        Assert.StartsWith("speechrevolutions-csharp/", ua);
        // A missing assembly version degrades to 0.0.0; that must never ship.
        Assert.DoesNotContain("0.0.0", ua);
        // Build metadata belongs in the assembly, not on the wire.
        Assert.DoesNotContain("+", ua);
    }
}
