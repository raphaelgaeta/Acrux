using System.Reflection;

namespace Acrux;

// ApplicationIcon only brands the .exe: every window with a title bar has to
// set Form.Icon itself. Reads the embedded .ico because ExtractAssociatedIcon
// returns a single 32 px frame, which blurs at title bar size.
internal static class AppIcon
{
    private const string ResourceName = "Acrux.AppIcon.ico";

    public static Icon Value { get; } = Load();

    private static Icon Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource {ResourceName}");
        return new Icon(stream);
    }
}
