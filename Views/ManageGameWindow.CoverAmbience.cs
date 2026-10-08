using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace OptiscalerClient.Views
{
    // ───────────── Cover ambience ─────────────
    // Pure visuals, so it lives in the view. A soft glow tinted by the cover sits behind it and
    // fades into the frame background. The blur is free: the cover is downscaled once to a few
    // pixels and the Image stretches it back up with high-quality interpolation, so there is no
    // per-frame BlurEffect cost (matters on Linux software rendering). It follows ImgGameCover's
    // Source, so every path that sets or clears the cover updates it without extra calls.
    public partial class ManageGameWindow
    {
        private static readonly PixelSize CoverAmbienceSampleSize = new(16, 22);

        // Glow size relative to the cover; the frame clips whatever spills past its edges.
        private const double CoverAmbienceScaleX = 2.2;
        private const double CoverAmbienceScaleY = 1.8;

        // Bright covers get less opacity so the title and install path stay readable over them.
        private const double CoverAmbienceMaxOpacity = 0.72;
        private const double CoverAmbienceMinOpacity = 0.4;

        private Rect _coverAmbienceLastRect;

        private void InitializeCoverAmbience()
        {
            if (this.FindControl<Image>("ImgGameCover") is { } imgGameCover)
            {
                imgGameCover.PropertyChanged += (_, e) =>
                {
                    if (e.Property == Image.SourceProperty)
                        UpdateCoverAmbience(imgGameCover.Source as Bitmap);
                };
            }

            // The cover moves/resizes with the responsive layout (sidebar collapse, fill-screen).
            LayoutUpdated += (_, _) => PositionCoverAmbience();
        }

        private void UpdateCoverAmbience(Bitmap? cover)
        {
            var imgAmbience = this.FindControl<Image>("ImgCoverAmbience");
            if (imgAmbience == null) return;

            if (cover == null)
            {
                imgAmbience.Opacity = 0;
                return;
            }

            try
            {
                var sample = cover.CreateScaledBitmap(CoverAmbienceSampleSize, BitmapInterpolationMode.HighQuality);
                imgAmbience.Source = sample;
                imgAmbience.Opacity = GetCoverAmbienceOpacity(sample);
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[ManageGame] Cover ambience failed: {ex.Message}");
                imgAmbience.Opacity = 0;
            }
        }

        private void PositionCoverAmbience()
        {
            var cover = this.FindControl<Border>("BdGameCover");
            var canvas = this.FindControl<Canvas>("CanvasCoverAmbience");
            var imgAmbience = this.FindControl<Image>("ImgCoverAmbience");
            if (cover == null || canvas == null || imgAmbience == null) return;

            var origin = cover.TranslatePoint(default, canvas);
            if (origin == null) return;

            var size = cover.Bounds.Size;
            double width = size.Width * CoverAmbienceScaleX;
            double height = size.Height * CoverAmbienceScaleY;
            var rect = new Rect(
                origin.Value.X + (size.Width - width) / 2,
                origin.Value.Y + (size.Height - height) / 2,
                width, height);

            if (rect == _coverAmbienceLastRect) return;
            _coverAmbienceLastRect = rect;

            Canvas.SetLeft(imgAmbience, rect.X);
            Canvas.SetTop(imgAmbience, rect.Y);
            imgAmbience.Width = rect.Width;
            imgAmbience.Height = rect.Height;
        }

        /// <summary>
        /// Maps the sample's mean brightness (HSV value, so it doesn't depend on the platform's
        /// RGBA/BGRA byte order) to an opacity: dark covers glow more, bright ones less.
        /// </summary>
        private static double GetCoverAmbienceOpacity(Bitmap sample)
        {
            var px = sample.PixelSize;
            int stride = px.Width * 4;
            var buffer = new byte[stride * px.Height];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                sample.CopyPixels(new PixelRect(px), handle.AddrOfPinnedObject(), buffer.Length, stride);
            }
            catch
            {
                return (CoverAmbienceMaxOpacity + CoverAmbienceMinOpacity) / 2;
            }
            finally
            {
                handle.Free();
            }

            double sum = 0;
            for (int i = 0; i < buffer.Length; i += 4)
                sum += Math.Max(buffer[i], Math.Max(buffer[i + 1], buffer[i + 2])) / 255.0;

            double brightness = sum / (px.Width * px.Height);
            return CoverAmbienceMaxOpacity - (CoverAmbienceMaxOpacity - CoverAmbienceMinOpacity) * brightness;
        }
    }
}
