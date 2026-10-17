using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Pos.Desktop;

public partial class StartupSplashWindow : Window
{
    private readonly Stopwatch _visibleFor = Stopwatch.StartNew();

    public StartupSplashWindow()
    {
        InitializeComponent();
    }

    public void SetStatus(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetStatus(message));
            return;
        }
        StartupStatus.Text = message;
    }

    public async Task KeepVisibleForAnimationAsync()
    {
        var remaining = TimeSpan.FromMilliseconds(1200) - _visibleFor.Elapsed;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var logo = new BitmapImage();
        logo.BeginInit();
        logo.UriSource = new Uri("pack://application:,,,/Pos.Desktop;component/Assets/balanzapro-logo.png", UriKind.Absolute);
        logo.CacheOption = BitmapCacheOption.OnLoad;
        logo.EndInit();
        logo.Freeze();

        var split = logo.PixelHeight / 2;
        ScalePart.Source = new CroppedBitmap(logo, new Int32Rect(0, 0, logo.PixelWidth, split));
        RegisterPart.Source = new CroppedBitmap(logo, new Int32Rect(0, split, logo.PixelWidth, logo.PixelHeight - split));

        var duration = TimeSpan.FromMilliseconds(850);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        ScaleTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(-150, 0, duration) { EasingFunction = easing });
        RegisterTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(150, 0, duration) { EasingFunction = easing, BeginTime = TimeSpan.FromMilliseconds(100) });
        ScalePart.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500)) { EasingFunction = easing });
        RegisterPart.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500))
        {
            EasingFunction = easing,
            BeginTime = TimeSpan.FromMilliseconds(100)
        });
    }
}
