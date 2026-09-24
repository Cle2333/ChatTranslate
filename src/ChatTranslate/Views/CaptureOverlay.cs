using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ChatTranslate.Views;

/// <summary>
/// 全屏框选层：把传入的冻结截图铺满虚拟桌面，用户拖拽出矩形后返回其像素坐标。
///
/// <para><b>为什么先把图铺出来</b>：框选期间屏幕内容被"冻住"，即便底层应用仍在刷新，
/// 用户框的也是同一张图，不会出现框选区域与最终裁剪结果不一致。</para>
///
/// <para><b>DPI 前提</b>：窗口 Left/Top/Width/Height 是 DIP，而截图像素是物理像素，
/// 两者比值即缩放因子。所有显示器缩放比一致时换算精确；混合 DPI 场景下
/// 需要按显示器分别换算（当前实现未覆盖）。</para>
/// </summary>
public sealed class CaptureOverlay : Window
{
    private readonly BitmapSource _screenshot;
    private readonly double _scale;
    private readonly Canvas _canvas;
    private readonly Border _selection;

    private Point _origin;
    private bool _dragging;

    private Int32Rect? _result;

    private CaptureOverlay(BitmapSource screenshot, double scale)
    {
        _screenshot = screenshot;
        _scale = scale;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Black;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        Cursor = Cursors.Cross;

        var root = new Grid();

        // 冻结的截图铺满整个虚拟桌面
        root.Children.Add(new Image
        {
            Source = screenshot,
            Stretch = Stretch.Fill,
            Opacity = 0.82,
        });

        _canvas = new Canvas { Background = Brushes.Transparent };

        // 选区：亮边 + 半透明填充，且在选区内不再叠暗色蒙层（用透明矩形"挖洞"）
        _selection = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xF4)),
            BorderThickness = new Thickness(1.5),
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0x0A, 0x84, 0xF4)),
            Visibility = Visibility.Collapsed,
        };
        _canvas.Children.Add(_selection);

        root.Children.Add(_canvas);
        Content = root;

        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        KeyDown += OnKeyDown;
    }

    /// <summary>
    /// 弹出框选层，返回选中区域的图像像素坐标；用户取消返回 null。
    /// </summary>
    /// <param name="screenshot">已抓取的整屏位图。</param>
    public static Int32Rect? PickRegion(BitmapSource screenshot)
    {
        // 虚拟桌面尺寸（DIP）与位图尺寸（物理像素）之比
        var dipWidth = SystemParameters.VirtualScreenWidth;
        var scale = dipWidth > 0 ? screenshot.PixelWidth / dipWidth : 1.0;

        var overlay = new CaptureOverlay(screenshot, scale);
        overlay.ShowDialog();
        return overlay._result;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _origin = e.GetPosition(_canvas);
        _dragging = true;

        Canvas.SetLeft(_selection, _origin.X);
        Canvas.SetTop(_selection, _origin.Y);
        _selection.Width = 0;
        _selection.Height = 0;
        _selection.Visibility = Visibility.Visible;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var current = e.GetPosition(_canvas);

        var x = Math.Min(_origin.X, current.X);
        var y = Math.Min(_origin.Y, current.Y);
        var w = Math.Abs(current.X - _origin.X);
        var h = Math.Abs(current.Y - _origin.Y);

        Canvas.SetLeft(_selection, x);
        Canvas.SetTop(_selection, y);
        _selection.Width = w;
        _selection.Height = h;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;

        // 过小的框选视为误触，不返回结果
        if (_selection.Width < 6 || _selection.Height < 6)
        {
            return;
        }

        var x = Canvas.GetLeft(_selection) * _scale;
        var y = Canvas.GetTop(_selection) * _scale;
        var w = _selection.Width * _scale;
        var h = _selection.Height * _scale;

        _result = new Int32Rect(
            (int)Math.Round(x),
            (int)Math.Round(y),
            Math.Max(1, (int)Math.Round(w)),
            Math.Max(1, (int)Math.Round(h)));

        Close();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _result = null;
            Close();
        }
    }
}
