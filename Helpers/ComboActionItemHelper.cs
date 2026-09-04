using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OptiscalerClient.Helpers;

/// <summary>
/// Builds an "action" ComboBoxItem — "+ New Profile" / "Manage versions…" / "New / Import…" /
/// a "Custom" value entry — styled distinct from a plain value to pick. Applies the ActionOption
/// XAML class (pill-shaped, accent-tinted — see App.axaml) plus a small outlined badge so it
/// reads as a button rather than just another row in the dropdown.
/// </summary>
public static class ComboActionItemHelper
{
    /// <param name="glyph">Badge glyph. Defaults to "+" (add/new). Pass a FluentSystemIcons
    /// codepoint together with <paramref name="glyphFontFamily"/> for a different meaning
    /// (e.g. the pencil glyph for a "Custom" value entry).</param>
    /// <param name="glyphFontFamily">Icon font for <paramref name="glyph"/>, e.g. the app's
    /// FontIcons resource. Left null for the plain "+" text glyph.</param>
    public static ComboBoxItem Build(Control owner, string text, object tag, string glyph = "+", FontFamily? glyphFontFamily = null)
    {
        var accent = owner.FindResource("BrAccent") as IBrush ?? Brushes.MediumPurple;

        var glyphText = new TextBlock
        {
            Text = glyph,
            FontSize = glyphFontFamily != null ? 9 : 11,
            FontWeight = FontWeight.Bold,
            Foreground = accent,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -1, 0, 0)
        };
        if (glyphFontFamily != null)
            glyphText.FontFamily = glyphFontFamily;

        // The glyph's own -1 top margin centers it inside the circle (font metrics render it
        // slightly low otherwise) — that's independent of the circle's position relative to the
        // row's text, which is handled by nudging the whole badge (circle + glyph) down together.
        var badge = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            BorderBrush = accent,
            BorderThickness = new Thickness(1.5),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
            Child = glyphText
        };

        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(badge);
        stack.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });

        var item = new ComboBoxItem { Content = stack, Tag = tag };
        item.Classes.Add("ActionOption");
        return item;
    }
}
