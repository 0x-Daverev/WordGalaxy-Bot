using System.Reflection;

namespace FunnyProject
{
    // Version of this build, from Directory.Build.props (e.g. "1.0.0", or
    // "1.0.0-ci.42" for CI builds of main). Full adds the commit when the SDK
    // knows it ("1.0.0+3f2a1c9…").
    public static class BotVersion
    {
        public static string Full { get; } =
            typeof(BotVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(BotVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        public static string Current { get; } = Full.Split('+')[0];
    }
}
