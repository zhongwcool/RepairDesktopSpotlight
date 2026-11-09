using System.Windows;
using System.Windows.Media;

namespace DesktopSpotlightFix.Helpers;

/// <summary>
/// 100% 缩放用 Display（小字更锐利），高于 100% 用 Ideal。窗口跨屏时跟随 DpiChanged。
/// </summary>
public static class DpiTextRenderingHelper
{
    public static void Attach(Window window)
    {
        window.Loaded += (_, _) => Apply(window, VisualTreeHelper.GetDpi(window));
        window.DpiChanged += (_, e) => Apply(window, e.NewDpi);
    }

    private static void Apply(Window window, DpiScale dpi)
    {
        TextOptions.SetTextFormattingMode(
            window,
            dpi.DpiScaleX > 1.0 ? TextFormattingMode.Ideal : TextFormattingMode.Display);
    }
}
