using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using OptiscalerClient.Models;
using OptiscalerClient.Views;
using SkiaSharp;

namespace OptiscalerClient.Services;

/// <summary>
/// Game icons taken from the icon embedded in the game's Windows executable. Used raw as the
/// list-mode thumbnail, and as the base of a fallback cover for games with no cover art anywhere:
/// a 600×900 image with the icon centered over a gradient built from the icon's own colors.
/// Pure managed code (PE parsing + SkiaSharp), so it behaves the same on Windows and Linux
/// (Proton/Wine installs ship the same .exe files).
/// </summary>
public sealed partial class GameIconCoverService
{
    private const int CoverWidth = 600;
    private const int CoverHeight = 900;
    private const int MaxIconSize = 256;
    private const int MaxIconDrawSize = 240;
    private const int MaxSearchDepth = 4;
    private const int MaxExecutablesTried = 4;

    /// <summary>Returns <paramref name="iconPath"/> once the extracted icon is cached there as a PNG,
    /// or null when the game has no usable icon (remembered in <paramref name="missMarkerPath"/> so
    /// the executables aren't rescanned on every launch).</summary>
    public string? GetOrExtractIcon(Game game, string iconPath, string missMarkerPath)
    {
        if (File.Exists(iconPath)) return iconPath;
        if (File.Exists(missMarkerPath)) return null;

        try
        {
            using var icon = FromExecutables(game);
            if (icon is null)
            {
                File.WriteAllBytes(missMarkerPath, Array.Empty<byte>());
                DebugWindow.Log(() => $"[Icon] No exe icon found for: \"{game.Name}\"");
                return null;
            }

            using var scaled = icon.Width <= MaxIconSize && icon.Height <= MaxIconSize
                ? icon.Copy()
                : icon.Resize(new SKImageInfo(MaxIconSize, MaxIconSize, SKColorType.Rgba8888, SKAlphaType.Premul), SKFilterQuality.High);
            SavePng(scaled, iconPath);
            DebugWindow.Log(() => $"[Icon] Extracted exe icon for: \"{game.Name}\"");
            return iconPath;
        }
        catch (Exception ex)
        {
            DebugWindow.Log(() => $"[Icon] Icon extraction failed for \"{game.Name}\": {ex.Message}");
            return null;
        }
    }

    /// <summary>Returns <paramref name="outputPath"/> once the icon cover exists there, or null when
    /// the game has no usable icon. Reuses the cached icon from <see cref="GetOrExtractIcon"/>.</summary>
    public string? GetOrCreateCover(Game game, string outputPath, string iconPath, string missMarkerPath)
    {
        if (File.Exists(outputPath)) return outputPath;
        if (GetOrExtractIcon(game, iconPath, missMarkerPath) is null) return null;

        try
        {
            using var icon = SKBitmap.Decode(iconPath);
            if (icon is null) return null;
            using var cover = Compose(icon);
            SavePng(cover, outputPath);
            DebugWindow.Log(() => $"[Cover] Generated icon cover for: \"{game.Name}\"");
            return outputPath;
        }
        catch (Exception ex)
        {
            DebugWindow.Log(() => $"[Cover] Icon cover failed for \"{game.Name}\": {ex.Message}");
            return null;
        }
    }

    private static void SavePng(SKBitmap bitmap, string path)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var tmp = path + ".tmp";
        using (var file = File.Create(tmp)) data.SaveTo(file);
        File.Move(tmp, path, overwrite: true);
    }

    // ───────────────────────── Composition ─────────────────────────

    private static SKBitmap Compose(SKBitmap icon)
    {
        var (primary, secondary) = ExtractPalette(icon);

        var bitmap = new SKBitmap(CoverWidth, CoverHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        var bounds = new SKRect(0, 0, CoverWidth, CoverHeight);

        // 1. Diagonal gradient between the two icon colors, kept dark so it sits well in the app.
        using (var paint = new SKPaint())
        {
            paint.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(CoverWidth, CoverHeight),
                new[] { Tone(primary, 0.52f), Tone(secondary, 0.30f), Tone(secondary, 0.12f) },
                new[] { 0f, 0.55f, 1f }, SKShaderTileMode.Clamp);
            canvas.DrawRect(bounds, paint);
        }

        // 2. The icon itself, hugely enlarged and blurred: a soft wash of its real colors.
        var iconSize = Math.Min(MaxIconDrawSize, Math.Max(icon.Width, icon.Height) * 3);
        var center = new SKPoint(CoverWidth / 2f, CoverHeight * 0.44f);
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium, Color = SKColors.White.WithAlpha(150) })
        {
            paint.ImageFilter = SKImageFilter.CreateBlur(70, 70);
            var wash = CoverWidth * 1.1f;
            canvas.DrawBitmap(icon, SKRect.Create(center.X - wash / 2, center.Y - wash / 2, wash, wash), paint);
        }

        // 3. Glow behind the icon + darkening towards the bottom (where the grid card puts its text).
        using (var paint = new SKPaint())
        {
            paint.Shader = SKShader.CreateRadialGradient(center, iconSize * 1.3f,
                new[] { Tone(primary, 0.70f).WithAlpha(70), SKColors.Transparent }, null, SKShaderTileMode.Clamp);
            canvas.DrawRect(bounds, paint);
        }
        using (var paint = new SKPaint())
        {
            paint.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, CoverHeight * 0.55f), new SKPoint(0, CoverHeight),
                new[] { SKColors.Transparent, new SKColor(0, 0, 0, 150) }, null, SKShaderTileMode.Clamp);
            canvas.DrawRect(bounds, paint);
        }

        // 4. The icon, with a soft drop shadow.
        var iconRect = SKRect.Create(center.X - iconSize / 2f, center.Y - iconSize / 2f, iconSize, iconSize);
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
        {
            paint.ImageFilter = SKImageFilter.CreateDropShadow(0, 10, 18, 18, new SKColor(0, 0, 0, 140));
            canvas.DrawBitmap(icon, iconRect, paint);
        }

        return bitmap;
    }

    /// <summary>Two dominant colors of the icon: pixels are bucketed by hue (vivid pixels weigh
    /// more), the heaviest bucket wins, and the second color is the heaviest bucket with a clearly
    /// different hue — or a hue-shifted variant of the first when the icon is single-colored.</summary>
    private static (SKColor Primary, SKColor Secondary) ExtractPalette(SKBitmap icon)
    {
        const int bins = 12;
        var weight = new double[bins + 1];          // last bin: neutrals (greys, near black/white)
        var r = new double[bins + 1];
        var g = new double[bins + 1];
        var b = new double[bins + 1];

        using var small = icon.Resize(new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Unpremul), SKFilterQuality.Medium)
                          ?? icon.Copy();
        for (var y = 0; y < small.Height; y++)
        for (var x = 0; x < small.Width; x++)
        {
            var c = small.GetPixel(x, y);
            if (c.Alpha < 128) continue;
            c.ToHsv(out var h, out var s, out var v);          // h: 0–360, s/v: 0–100
            var neutral = s < 18 || v < 18;
            var bin = neutral ? bins : (int)(h / 360f * bins) % bins;
            var w = neutral ? 0.25 : 1 + s / 100 * 3;
            weight[bin] += w;
            r[bin] += c.Red * w;
            g[bin] += c.Green * w;
            b[bin] += c.Blue * w;
        }

        SKColor Average(int i) => new((byte)(r[i] / weight[i]), (byte)(g[i] / weight[i]), (byte)(b[i] / weight[i]));

        var best = Enumerable.Range(0, bins + 1).OrderByDescending(i => weight[i]).First();
        if (weight[best] <= 0) return (new SKColor(0x3A, 0x3A, 0x5A), new SKColor(0x1A, 0x1A, 0x2E));
        var primary = Average(best);

        var second = Enumerable.Range(0, bins)
            .Where(i => i != best && weight[i] > weight[best] * 0.08
                        && (best == bins || Math.Min(Math.Abs(i - best), bins - Math.Abs(i - best)) >= 2))
            .OrderByDescending(i => weight[i])
            .Cast<int?>()
            .FirstOrDefault();

        if (second is { } i2) return (primary, Average(i2));

        primary.ToHsv(out var ph, out var ps, out var pv);
        return (primary, SKColor.FromHsv((ph + 28) % 360, Math.Min(100, ps + 10), pv));
    }

    /// <summary>Same hue, saturation capped and value forced into a dark band.</summary>
    private static SKColor Tone(SKColor color, float value)
    {
        color.ToHsv(out var h, out var s, out _);
        return SKColor.FromHsv(h, Math.Min(s, 75), value * 100);
    }

    // ───────────────────────── Executable ─────────────────────────

    [GeneratedRegex(@"unins|crash|redist|vc_?redist|dxsetup|directx|dotnet|setup|install|launcher|helper|report|anticheat|easyanticheat|battleye|prereq|cefprocess|webhelper|update|sqlmetal|editor|server|benchmark|config", RegexOptions.IgnoreCase)]
    private static partial Regex NonGameExecutable();

    private static SKBitmap? FromExecutables(Game game)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(game.ExecutablePath) && File.Exists(game.ExecutablePath)
            && game.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            candidates.Add(game.ExecutablePath);
        if (Directory.Exists(game.InstallPath))
            candidates.AddRange(RankExecutables(game)
                .Where(p => !string.Equals(p, game.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                .Take(MaxExecutablesTried));

        foreach (var exe in candidates)
        {
            var bitmap = PeIconReader.TryReadLargestIcon(exe);
            if (bitmap is not null) return bitmap;
        }
        return null;
    }

    /// <summary>Candidate executables, most likely "the game" first: name resembling the game's,
    /// then shallower (the install root holds the real launcher), then larger. The largest .exe is
    /// often wrong (tools, editors), and Unreal games keep the icon on the small root stub rather
    /// than on *-Shipping.exe.</summary>
    private static IEnumerable<string> RankExecutables(Game game)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = MaxSearchDepth,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        var tokens = Normalize(game.Name).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3)
            .ToArray();
        var compactName = Normalize(game.Name).Replace(" ", "");

        return Directory.EnumerateFiles(game.InstallPath, "*.exe", options)
            .Where(path => !NonGameExecutable().IsMatch(Path.GetFileNameWithoutExtension(path)))
            .Select(path =>
            {
                var stem = Normalize(Path.GetFileNameWithoutExtension(path)).Replace(" ", "");
                var depth = Path.GetRelativePath(game.InstallPath, path).Count(c => c == Path.DirectorySeparatorChar);
                var score = tokens.Count(t => stem.Contains(t, StringComparison.Ordinal)) * 50
                    + (stem.Length >= 3 && compactName.Contains(stem, StringComparison.Ordinal) ? 60 : 0)
                    - depth * 15
                    - (stem.Contains("shipping", StringComparison.Ordinal) ? 20 : 0);
                return (path, score, size: SafeLength(path));
            })
            .OrderByDescending(c => c.score)
            .ThenByDescending(c => c.size)
            .Select(c => c.path);
    }

    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        return sb.ToString();
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }
}

/// <summary>
/// Minimal PE resource reader: finds the first RT_GROUP_ICON, picks its largest image and returns
/// it decoded. Only seeks to the headers and the few resource entries it needs, so a 500 MB
/// executable costs a handful of small reads, not a full load.
/// </summary>
internal static class PeIconReader
{
    private const int RtIcon = 3;
    private const int RtGroupIcon = 14;

    public static SKBitmap? TryReadLargestIcon(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
            using var reader = new BinaryReader(stream);

            if (reader.ReadUInt16() != 0x5A4D) return null;                // "MZ"
            stream.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550) return null;           // "PE\0\0"

            stream.Position += 2;                                         // Machine
            var sectionCount = reader.ReadUInt16();
            stream.Position += 12;                                        // timestamp, symbols
            var optionalHeaderSize = reader.ReadUInt16();
            stream.Position += 2;                                         // characteristics

            var optionalHeader = stream.Position;
            var magic = reader.ReadUInt16();
            var dataDirectories = optionalHeader + (magic == 0x20B ? 112 : 96);
            stream.Position = dataDirectories + 2 * 8;                    // IMAGE_DIRECTORY_ENTRY_RESOURCE
            var resourceRva = reader.ReadUInt32();
            if (resourceRva == 0) return null;

            var sections = new List<(uint Va, uint Size, uint Raw)>();
            stream.Position = optionalHeader + optionalHeaderSize;
            for (var i = 0; i < sectionCount; i++)
            {
                stream.Position += 8;                                     // name
                var virtualSize = reader.ReadUInt32();
                var virtualAddress = reader.ReadUInt32();
                var rawSize = reader.ReadUInt32();
                var rawPointer = reader.ReadUInt32();
                stream.Position += 16;
                sections.Add((virtualAddress, Math.Max(virtualSize, rawSize), rawPointer));
            }

            long ToOffset(uint rva)
            {
                foreach (var s in sections)
                    if (rva >= s.Va && rva < s.Va + s.Size) return rva - s.Va + s.Raw;
                return -1;
            }

            var root = ToOffset(resourceRva);
            if (root < 0) return null;

            var groupData = FindResource(reader, root, RtGroupIcon, id: null);
            if (groupData is not { } group) return null;
            var groupBytes = ReadData(reader, group, ToOffset);
            if (groupBytes is null || groupBytes.Length < 6) return null;

            // GRPICONDIR: reserved, type, count; then 14-byte GRPICONDIRENTRY each.
            var count = BitConverter.ToUInt16(groupBytes, 4);
            var best = -1;
            var bestScore = -1;
            for (var i = 0; i < count && 6 + (i + 1) * 14 <= groupBytes.Length; i++)
            {
                var e = 6 + i * 14;
                var width = groupBytes[e] == 0 ? 256 : groupBytes[e];
                var bits = BitConverter.ToUInt16(groupBytes, e + 6);
                var score = width * 64 + bits;
                if (score > bestScore) { bestScore = score; best = e; }
            }
            if (best < 0) return null;

            var iconId = BitConverter.ToUInt16(groupBytes, best + 12);
            var iconData = FindResource(reader, root, RtIcon, iconId);
            if (iconData is not { } icon) return null;
            var imageBytes = ReadData(reader, icon, ToOffset);
            if (imageBytes is null) return null;

            // Large icons are stored as raw PNG; smaller ones as a DIB, which Skia decodes once it's
            // wrapped in a one-image .ico container.
            var isPng = imageBytes.Length > 8 && imageBytes[0] == 0x89 && imageBytes[1] == 0x50;
            return SKBitmap.Decode(isPng ? imageBytes : WrapInIco(groupBytes, best, imageBytes));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Walks type → name/id → language and returns the data entry (RVA, size).</summary>
    private static (uint Rva, uint Size)? FindResource(BinaryReader reader, long root, int type, int? id)
    {
        var typeDir = FindEntry(reader, root, root, type);
        if (typeDir is null) return null;
        var nameDir = FindEntry(reader, root, typeDir.Value, id);
        if (nameDir is null) return null;
        var langEntry = FindEntry(reader, root, nameDir.Value, id: null, wantData: true);
        if (langEntry is null) return null;

        reader.BaseStream.Position = langEntry.Value;
        return (reader.ReadUInt32(), reader.ReadUInt32());
    }

    /// <summary>Returns the absolute offset of the matching entry's target (a subdirectory, or a
    /// data entry when <paramref name="wantData"/>). A null id takes the first entry.</summary>
    private static long? FindEntry(BinaryReader reader, long root, long directory, int? id, bool wantData = false)
    {
        var stream = reader.BaseStream;
        stream.Position = directory + 12;
        var named = reader.ReadUInt16();
        var ids = reader.ReadUInt16();

        for (var i = 0; i < named + ids; i++)
        {
            var nameOrId = reader.ReadUInt32();
            var offset = reader.ReadUInt32();
            var isSubdirectory = (offset & 0x80000000) != 0;
            if (isSubdirectory == wantData) continue;

            var matches = id is null || ((nameOrId & 0x80000000) == 0 && nameOrId == id);
            if (matches) return root + (offset & 0x7FFFFFFF);
        }
        return null;
    }

    private static byte[]? ReadData(BinaryReader reader, (uint Rva, uint Size) data, Func<uint, long> toOffset)
    {
        var offset = toOffset(data.Rva);
        if (offset < 0 || data.Size is 0 or > 4 * 1024 * 1024) return null;
        reader.BaseStream.Position = offset;
        return reader.ReadBytes((int)data.Size);
    }

    private static byte[] WrapInIco(byte[] group, int entry, byte[] image)
    {
        using var ms = new MemoryStream(22 + image.Length);
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)1);   // ICONDIR, one image
        w.Write(group, entry, 12);                                     // width … bytesInRes
        w.Write(22u);                                                  // image offset
        w.Write(image);
        return ms.ToArray();
    }
}
