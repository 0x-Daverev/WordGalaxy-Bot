using System.Reflection;
using Xunit;

namespace PacketBot.Core.Tests;

// PacketBot.Core must stay loadable on Linux.
public class CorePortabilityTests
{
    private static readonly Assembly Core = typeof(FunnyProject.Api).Assembly;

    [Fact]
    public void CoreIsTheSharedAssembly() => Assert.Equal("PacketBot.Core", Core.GetName().Name);

    [Theory]
    [InlineData("System.Windows.Forms")]
    [InlineData("System.Windows.Forms.Primitives")]
    [InlineData("System.Drawing.Common")]
    [InlineData("System.Security.Cryptography.ProtectedData")]
    [InlineData("Microsoft.VisualBasic.Forms")]
    [InlineData("FunnyProject")]
    [InlineData("PacketBot.Windows")]
    public void CoreDoesNotReferenceWindowsOnlyAssemblies(string forbidden) =>
        Assert.DoesNotContain(Core.GetReferencedAssemblies(), a => a.Name == forbidden);

    [Fact]
    public void CoreTargetsPortableNet8()
    {
        var framework = Core.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>();
        Assert.Equal(".NETCoreApp,Version=v8.0", framework?.FrameworkName);
        Assert.Null(Core.GetCustomAttribute<System.Runtime.Versioning.SupportedOSPlatformAttribute>());
    }

    [Fact]
    public void AllCoreTypesLoad()
    {
        // ReflectionTypeLoadException here would reveal a hidden platform dependency.
        Assert.NotEmpty(Core.GetTypes());
    }

    [Fact]
    public void UserAlertsDefaultDoesNotNeedUi()
    {
        var previous = FunnyProject.Abstractions.UserAlerts.Handler;
        try
        {
            string? seen = null;
            FunnyProject.Abstractions.UserAlerts.Handler = m => seen = m;
            FunnyProject.Abstractions.UserAlerts.Show("prueba");
            Assert.Equal("prueba", seen);
            FunnyProject.Abstractions.UserAlerts.Handler = null!; // resets to console default
            FunnyProject.Abstractions.UserAlerts.Show("prueba");
        }
        finally
        {
            FunnyProject.Abstractions.UserAlerts.Handler = previous;
        }
    }
}
