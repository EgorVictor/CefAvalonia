using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace CefSharp.Avalonia;

/// <summary>
/// Draws the off-screen rendered frame (Linux OSR). The owning <see cref="WebView"/>
/// blits pixels from the native host's shared memory segment into a
/// <see cref="WriteableBitmap"/>, which this control stretches to its bounds.
/// </summary>
internal sealed class OsrSurface : Control
{
    private WriteableBitmap? _bitmap;

    public WriteableBitmap? Bitmap
    {
        get => _bitmap;
        set
        {
            _bitmap = value;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        WriteableBitmap? bitmap = _bitmap;
        if (bitmap == null) return;

        var source = new Rect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        var dest = new Rect(Bounds.Size);
        context.DrawImage(bitmap, source, dest);
    }
}
