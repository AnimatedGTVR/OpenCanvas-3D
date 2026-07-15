using Godot;
using System.Collections.Generic;

namespace OpenCanvas3D;

// Loads and caches the app's icon PNGs. Icons are full-color gradient art
// matching the logo, not flat/monochrome, so they can't be runtime-tinted
// the way a single-color icon can — each one exists as two separate
// pre-baked variants (one legible on light chrome, one on dark chrome),
// and Get() picks the right one for the currently active theme.
public static class IconSet
{
    private static readonly Dictionary<string, Texture2D> _lightCache = new();
    private static readonly Dictionary<string, Texture2D> _darkCache = new();

    public static Texture2D? Get(string name, bool dark)
    {
        var cache = dark ? _darkCache : _lightCache;
        if (cache.TryGetValue(name, out var cached))
            return cached;

        var path = $"res://assets/icons/{(dark ? "dark" : "light")}/{name}.png";
        var texture = ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
        cache[name] = texture!;
        return texture;
    }
}
