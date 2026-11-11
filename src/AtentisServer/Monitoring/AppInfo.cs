using System.Reflection;

namespace AtentisServer;

public static class AppInfo
{
    public static readonly string Version =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString() ?? "unknown";
}
