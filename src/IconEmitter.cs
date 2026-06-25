using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MsTeamsLocal;

/// <summary>
/// Writes the static PNG icon files referenced by the manifest (idle action icons,
/// key images, and the category icon). The Elgato packaging tool requires PNG, so the
/// SVG glyphs are rasterized with WPF (already referenced via UseWPF) at the standard
/// Stream Deck sizes. Reuses <see cref="IconRenderer.GetVisual"/> so the static and
/// live icons stay visually identical.
/// </summary>
public static class IconEmitter
{
    private const string CategoryBg = "#4B53BC";
    private const string CategoryFg = "#FFFFFF";

    public static void EmitAll(string pluginRoot)
    {
        // RenderTargetBitmap / DrawingVisual require an STA thread.
        var thread = new Thread(() =>
        {
            try { RenderAll(pluginRoot); }
            catch (Exception ex) { Log.Error("icon emit failed", ex); throw; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
    }

    private static void RenderAll(string pluginRoot)
    {
        // Idle snapshot: meeting active so action icons render their normal glyph.
        var idle = new TeamsSnapshot { Initialized = true, TeamsRunning = true, MeetingActive = true };

        var imgs = Path.Combine(pluginRoot, "imgs");
        if (Directory.Exists(imgs)) Directory.Delete(imgs, recursive: true);

        foreach (var d in ActionCatalog.All)
        {
            var v = IconRenderer.GetVisual(d, idle);
            var dir = Path.Combine(imgs, "actions", d.Id);
            RenderPng(v, 20, Path.Combine(dir, "icon.png"));      // action list icon
            RenderPng(v, 40, Path.Combine(dir, "icon@2x.png"));
            RenderPng(v, 72, Path.Combine(dir, "key.png"));       // default key image
            RenderPng(v, 144, Path.Combine(dir, "key@2x.png"));
        }

        // Category / plugin icon: Teams-purple tile with the white mic glyph.
        var mic = IconRenderer.GetVisual(
            ActionCatalog.All.First(a => a.Kind == ActionKind.ToggleMute), idle);
        var category = new IconRenderer.IconVisual(CategoryBg, CategoryFg, mic.GlyphPath, false);
        var pluginDir = Path.Combine(imgs, "plugin");
        RenderPng(category, 28, Path.Combine(pluginDir, "category-icon.png"));
        RenderPng(category, 56, Path.Combine(pluginDir, "category-icon@2x.png"));
    }

    private static void RenderPng(IconRenderer.IconVisual v, int size, string path)
    {
        double s = size / 144.0; // icons are designed in a 144×144 space
        var bg = ParseColor(v.Background);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(bg), null,
                new Rect(6 * s, 6 * s, 132 * s, 132 * s), 28 * s, 28 * s);

            // "F1 " selects the nonzero fill rule (SVG default) for glyphs with holes.
            // Geometry.Parse returns a frozen geometry, so apply the transform via the
            // drawing context rather than mutating the geometry.
            var geometry = Geometry.Parse("F1 " + v.GlyphPath);
            var transform = new TransformGroup();
            transform.Children.Add(new ScaleTransform(0.32, 0.32)); // 256-space glyph -> 144 space
            transform.Children.Add(new TranslateTransform(31, 31));
            transform.Children.Add(new ScaleTransform(s, s));       // 144 space -> target size

            dc.PushTransform(transform);
            dc.DrawGeometry(new SolidColorBrush(ParseColor(v.Foreground)), null, geometry);
            dc.Pop();

            if (v.HandBadge)
            {
                var pen = new Pen(new SolidColorBrush(bg), 3 * s);
                dc.DrawEllipse(new SolidColorBrush(ParseColor("#FFC53D")), pen,
                    new Point(112 * s, 32 * s), 9 * s, 9 * s);
            }
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static Color ParseColor(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;
}
