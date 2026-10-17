using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Pos.Desktop;

public partial class StartupSplashWindow : Window
{
    private static readonly TimeSpan MinimumSplashDuration = TimeSpan.FromSeconds(5);
    private readonly Stopwatch _visibleFor = new();

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
        var remaining = MinimumSplashDuration - _visibleFor.Elapsed;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _visibleFor.Restart();
        var logo = new BitmapImage();
        logo.BeginInit();
        logo.UriSource = new Uri("pack://application:,,,/Pos.Desktop;component/Assets/balanzapro-logo.png", UriKind.Absolute);
        logo.CacheOption = BitmapCacheOption.OnLoad;
        logo.EndInit();
        logo.Freeze();

        var split = logo.PixelHeight / 2;
        ScalePart.Source = new CroppedBitmap(logo, new Int32Rect(0, 0, logo.PixelWidth, split));
        RegisterPart.Source = new CroppedBitmap(logo, new Int32Rect(0, split, logo.PixelWidth, logo.PixelHeight - split));

        var joinDuration = TimeSpan.FromMilliseconds(1300);
        var easing = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.12 };
        BalanceTranslateTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(-180, 0, joinDuration) { EasingFunction = easing });
        RegisterTranslateTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(180, 0, joinDuration) { EasingFunction = easing, BeginTime = TimeSpan.FromMilliseconds(120) });
        ScalePart.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(650)) { EasingFunction = easing });
        RegisterPart.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(650))
        {
            EasingFunction = easing,
            BeginTime = TimeSpan.FromMilliseconds(120)
        });

        var pulse = new DoubleAnimation(1, 1.035, TimeSpan.FromMilliseconds(750))
        {
            BeginTime = TimeSpan.FromMilliseconds(1500),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        LogoScaleTransform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, pulse);
        LogoScaleTransform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, pulse.Clone());
    }
}
