using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Tmds.DBus.Protocol;

namespace Acrux;

/// <summary>
/// The system's high contrast, for the grid's colors (design approved
/// 2026-11-15, "Paletas da grade"): in it, the brand gives way to the user's
/// scheme, as in the WinForms app.
/// - Windows: the contrast theme's colors, read as WinForms' SystemColors
///   reads them (GetSysColor), so a color the user edited counts too. Avalonia
///   says when the theme is on (ContrastPreference).
/// - Linux: there are no system colors to read, so a fixed palette with no hue
///   (black on white, white on black, by the desktop's light or dark). Whether
///   it is on is the desktop portal's "contrast" key, which Avalonia 12 doesn't
///   read (it reads color-scheme and accent-color only).
/// Avalonia's Fluent chrome (buttons, dialogs, the terminal box) has no high
/// contrast palette and keeps its colors: a known gap.
/// </summary>
internal static class HighContrast
{
    /// <summary>A contrast scheme: the system colors the grid draws with.</summary>
    public sealed record Scheme(Color Window, Color WindowText, Color Highlight, Color HighlightText,
        Color GrayText, Color ButtonFace, Color ButtonText);

    // Linux: no hue; the selection swaps the background and the text. The gray
    // is 6.9:1 on white and 8.2:1 on black, for the lines.
    private static readonly Scheme LinuxLight = new(Colors.White, Colors.Black, Colors.Black, Colors.White,
        Color.Parse("#5c5c5c"), Colors.White, Colors.Black);
    private static readonly Scheme LinuxDark = new(Colors.Black, Colors.White, Colors.White, Colors.Black,
        Color.Parse("#a3a3a3"), Colors.Black, Colors.White);

    private static Scheme? _windows;   // the system's scheme while high contrast is on
    private static bool _linux;        // the portal asks for more contrast

    /// <summary>High contrast went on or off, or its colors changed: repaint.</summary>
    public static event Action? Changed;

    public static bool IsOn => _windows is not null || _linux;

    /// <summary>The scheme to draw with in this light or dark, or null while high contrast is off.</summary>
    public static Scheme? For(ThemeVariant? variant) =>
        _windows ?? (_linux ? (variant == ThemeVariant.Dark ? LinuxDark : LinuxLight) : null);

    /// <summary>Reads the system's setting now and follows its changes; once, as the app starts.</summary>
    public static void Start(Application app)
    {
        if (OperatingSystem.IsWindows() && app.PlatformSettings is { } settings)
        {
            ReadWindows(settings);
            settings.ColorValuesChanged += (_, _) => ReadWindows(settings);
        }
        else if (OperatingSystem.IsLinux())
            _ = WatchPortalAsync();
    }

    private static void ReadWindows(IPlatformSettings settings)
    {
        _windows = settings.GetColorValues().ContrastPreference == ColorContrastPreference.High
            ? new Scheme(SystemColor(Window), SystemColor(WindowText), SystemColor(Highlight), SystemColor(HighlightText),
                SystemColor(GrayText), SystemColor(ButtonFace), SystemColor(ButtonText))
            : null;
        Changed?.Invoke();
    }

    // GetSysColor's indexes (winuser.h) and its COLORREF, 0x00BBGGRR
    private const int ButtonFace = 15, ButtonText = 18, GrayText = 17, Highlight = 13, HighlightText = 14, Window = 5, WindowText = 8;

    private static Color SystemColor(int index)
    {
        uint value = GetSysColor(index);
        return Color.FromRgb((byte)value, (byte)(value >> 8), (byte)(value >> 16));
    }

    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);

    private const string Portal = "org.freedesktop.portal.Desktop", PortalPath = "/org/freedesktop/portal/desktop",
        Settings = "org.freedesktop.portal.Settings", Appearance = "org.freedesktop.appearance";

    /// <summary>
    /// The portal's "contrast" (0: no preference, 1: higher), now and on each
    /// change. No session bus, no portal or an older one: no high contrast.
    /// </summary>
    private static async Task WatchPortalAsync()
    {
        try
        {
            var bus = DBusConnection.Session;
            await ReadPortalAsync(bus);
            // any setting that changes: the contrast is read again, a call of a few milliseconds
            await bus.WatchSignalAsync(Portal, PortalPath, Settings, "SettingChanged",
                (Notification change) => { if (change.Exception is null && !change.IsCompletion) _ = ReadPortalAsync(bus); },
                ObserverFlags.None, emitOnCapturedContext: false, null);
        }
        catch (Exception) { }
    }

    // the writer can't live across an await: the message is made here, and sent there
    private static MessageBuffer ContrastQuery(DBusConnection bus)
    {
        using var writer = bus.GetMessageWriter();
        writer.WriteMethodCallHeader(Portal, PortalPath, Settings, "ReadOne", "ss", MessageFlags.None);
        writer.WriteString(Appearance);
        writer.WriteString("contrast");
        return writer.CreateMessage();
    }

    private static async Task ReadPortalAsync(DBusConnection bus)
    {
        bool on;
        try
        {
            on = await bus.CallMethodAsync(ContrastQuery(bus),
                (Message message, object? _) => message.GetBodyReader().ReadVariantValue().GetUInt32() == 1, null);
        }
        catch (Exception)
        {
            on = false;   // a portal without the key answers with an error
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (on == _linux) return;
            _linux = on;
            Changed?.Invoke();
        });
    }
}
