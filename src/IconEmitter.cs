using System.IO;
using System.Text;

namespace MsTeamsLocal;

/// <summary>
/// Writes the static SVG icon files referenced by the manifest. These are only the
/// default/idle images shown before the plugin sets live images at runtime, plus the
/// category icon. Kept DRY by reusing <see cref="IconRenderer"/>.
/// </summary>
public static class IconEmitter
{
    public static void EmitAll(string pluginRoot)
    {
        // Idle snapshot: meeting active so the action icons render their normal glyph.
        var idle = new TeamsSnapshot
        {
            Initialized = true,
            TeamsRunning = true,
            MeetingActive = true,
        };

        foreach (var d in ActionCatalog.All)
        {
            var dir = Path.Combine(pluginRoot, "imgs", "actions", d.Id);
            Directory.CreateDirectory(dir);
            var svg = IconRenderer.BuildSvg(d, idle);
            File.WriteAllText(Path.Combine(dir, "icon.svg"), svg, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "key.svg"), svg, new UTF8Encoding(false));
        }

        var pluginDir = Path.Combine(pluginRoot, "imgs", "plugin");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "category-icon.svg"), CategoryIcon(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(pluginDir, "marketplace.svg"), CategoryIcon(), new UTF8Encoding(false));
    }

    private static string CategoryIcon()
    {
        // Simple Teams-purple rounded tile with a white microphone glyph.
        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"288\" height=\"288\" viewBox=\"0 0 288 288\">");
        sb.Append("<rect x=\"24\" y=\"24\" width=\"240\" height=\"240\" rx=\"56\" fill=\"#4B53BC\"/>");
        sb.Append("<g transform=\"translate(72,72) scale(0.5625)\" fill=\"#FFFFFF\">");
        sb.Append("<path d=\"M80,128V64a48,48,0,0,1,96,0v64a48,48,0,0,1-96,0Zm128,0a8,8,0,0,0-16,0,64,64,0,0,1-128,0,8,8,0,0,0-16,0,80.11,80.11,0,0,0,72,79.6V240a8,8,0,0,0,16,0V207.6A80.11,80.11,0,0,0,208,128Z\"/>");
        sb.Append("</g></svg>");
        return sb.ToString();
    }
}
