using System.Reflection;

namespace Bitrix24.Desktop;

public static class AppRelease
{
    public static string Version { get; } =
        typeof(AppRelease).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "1.0.4";
}
