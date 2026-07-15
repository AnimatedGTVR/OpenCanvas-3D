using Godot;
using System;

namespace OpenCanvas3D;

// Owns the active UI color palette and broadcasts changes so the ribbon/side
// panel can re-skin themselves live, without a full rebuild. A plain C#
// struct rather than a Godot Theme resource: the app's UI is built entirely
// in code (no authored Control tree with theme_type_variation wiring), so a
// struct that factory methods read directly is far less migration work than
// retrofitting Godot's resource-based theming onto procedural UI.
public partial class ThemeManager : Node
{
    public readonly struct Palette
    {
        public required Color TitleBarBg { get; init; }
        public required Color RibbonBg { get; init; }
        public required Color OptionsRowBg { get; init; }
        public required Color PanelBg { get; init; }
        public required Color AccentBlue { get; init; }
        public required Color AccentBlueTint { get; init; }
        public required Color TileBg { get; init; }
        public required Color TileBgActive { get; init; }
        public required Color TextDark { get; init; }
        public required Color TextMuted { get; init; }
    }

    // Original Paint-3D-inspired light palette — values unchanged from the
    // constants previously hardcoded in RibbonBuilder.cs.
    public static readonly Palette Light = new()
    {
        TitleBarBg = new Color(0.976f, 0.976f, 0.980f),
        RibbonBg = new Color(0.918f, 0.925f, 0.937f),
        OptionsRowBg = new Color(0.965f, 0.968f, 0.973f),
        PanelBg = new Color(1.0f, 1.0f, 1.0f),
        AccentBlue = new Color(0.0f, 0.47f, 0.83f),
        AccentBlueTint = new Color(0.831f, 0.902f, 0.965f),
        TileBg = new Color(0.965f, 0.968f, 0.973f),
        TileBgActive = new Color(0.831f, 0.902f, 0.965f),
        TextDark = new Color(0.157f, 0.165f, 0.184f),
        TextMuted = new Color(0.404f, 0.416f, 0.443f),
    };

    // Neutral charcoal chrome (VSCode-dark-like), with the logo's
    // purple/blue standing in for the light theme's accent blue on
    // active/selected states.
    public static readonly Palette Dark = new()
    {
        TitleBarBg = new Color(0.114f, 0.118f, 0.129f),
        RibbonBg = new Color(0.145f, 0.149f, 0.161f),
        OptionsRowBg = new Color(0.129f, 0.133f, 0.145f),
        PanelBg = new Color(0.161f, 0.165f, 0.180f),
        AccentBlue = new Color(0.545f, 0.361f, 0.965f),
        AccentBlueTint = new Color(0.267f, 0.220f, 0.412f),
        TileBg = new Color(0.192f, 0.196f, 0.212f),
        TileBgActive = new Color(0.267f, 0.220f, 0.412f),
        TextDark = new Color(0.902f, 0.906f, 0.918f),
        TextMuted = new Color(0.616f, 0.627f, 0.655f),
    };

    public Palette Current { get; private set; } = Light;
    public bool IsDark { get; private set; }
    public event Action? Changed;

    public override void _Ready()
    {
        var settings = GetNode<AppSettings>("/root/AppSettings");
        SetDark(settings.DarkTheme, notify: false);
    }

    public void SetDark(bool dark, bool notify = true)
    {
        IsDark = dark;
        Current = dark ? Dark : Light;
        if (notify)
            Changed?.Invoke();
    }
}
