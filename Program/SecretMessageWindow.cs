using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Minecraft;

public sealed class SecretMessageWindow : Window
{
    private const double MinBaseWidth = 320;
    private const double MinBaseHeight = 190;
    private const double MessageFontSize = 48;
    private const double ContentMargin = 14;
    private const double ContentGap = 10;
    private readonly Image _gifImage;
    private readonly IReadOnlyList<BitmapFrame> _gifFrames;
    private readonly IReadOnlyList<TimeSpan> _gifFrameDelays;
    private readonly DispatcherTimer _gifTimer = new(DispatcherPriority.Render);
    private int _gifFrameIndex;

    public SecretMessageWindow(double ownerScale)
    {
        Title = SecretMessage.Title;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var text = SecretMessage.Text.Trim();
        var animation = LoadGifAnimation();
        _gifFrames = animation.Frames;
        _gifFrameDelays = animation.Delays;

        var messageSize = MeasureMessage(text);
        var maxContentWidth = Math.Max(1, SystemParameters.WorkArea.Width * 0.9 - ContentMargin * 2);
        var contentWidth = Math.Min(Math.Ceiling(messageSize.Width), maxContentWidth);
        var gifHeight = contentWidth * animation.PixelHeight / animation.PixelWidth;
        var contentHeight = Math.Ceiling(messageSize.Height) + ContentGap + gifHeight;
        var baseSize = CalculateInitialSize(contentWidth, contentHeight);
        var scale = Math.Clamp(ownerScale, 0.5, 2.5);
        Width = Math.Min(baseSize.Width * scale, SystemParameters.WorkArea.Width * 0.9);
        Height = Math.Min(baseSize.Height * scale, SystemParameters.WorkArea.Height * 0.9);
        MinWidth = MinBaseWidth * scale;
        MinHeight = MinBaseHeight * scale;
        MaxWidth = SystemParameters.WorkArea.Width * 0.9;
        MaxHeight = SystemParameters.WorkArea.Height * 0.9;

        var root = new Viewbox
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.Both
        };
        var layout = new Grid
        {
            Width = baseSize.Width - ContentMargin * 2,
            Height = baseSize.Height - ContentMargin * 2,
            Margin = new Thickness(ContentMargin)
        };

        var brush = CreateAnimatedRgbBrush();
        var message = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.NoWrap,
            TextAlignment = TextAlignment.Center,
            Width = contentWidth,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = MessageFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = brush
        };

        _gifImage = new Image
        {
            Width = contentWidth,
            Height = gifHeight,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, ContentGap, 0, 0),
            Source = _gifFrames[0]
        };
        RenderOptions.SetBitmapScalingMode(_gifImage, BitmapScalingMode.HighQuality);

        var content = new StackPanel
        {
            Width = contentWidth,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(message);
        content.Children.Add(_gifImage);
        layout.Children.Add(content);
        root.Child = layout;
        Content = root;

        _gifTimer.Tick += GifTimer_Tick;
        Loaded += (_, _) => StartGifAnimation();
        Closed += (_, _) => _gifTimer.Stop();
    }

    private void StartGifAnimation()
    {
        _gifTimer.Stop();
        _gifFrameIndex = 0;
        _gifImage.Source = _gifFrames[0];
        if (_gifFrames.Count < 2) return;
        _gifTimer.Interval = _gifFrameDelays[0];
        _gifTimer.Start();
    }

    private void GifTimer_Tick(object? sender, EventArgs e)
    {
        _gifFrameIndex = (_gifFrameIndex + 1) % _gifFrames.Count;
        _gifImage.Source = _gifFrames[_gifFrameIndex];
        _gifTimer.Interval = _gifFrameDelays[_gifFrameIndex];
    }

    private static LinearGradientBrush CreateAnimatedRgbBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            GradientStops =
            {
                new GradientStop(Colors.Red, 0),
                new GradientStop(Colors.Lime, 0.33),
                new GradientStop(Colors.Blue, 0.66),
                new GradientStop(Colors.Red, 1)
            }
        };

        brush.BeginAnimation(LinearGradientBrush.StartPointProperty, new PointAnimation
        {
            From = new Point(-1, 0),
            To = new Point(1, 0),
            Duration = TimeSpan.FromSeconds(2.5),
            RepeatBehavior = RepeatBehavior.Forever
        });
        brush.BeginAnimation(LinearGradientBrush.EndPointProperty, new PointAnimation
        {
            From = new Point(0, 0),
            To = new Point(2, 0),
            Duration = TimeSpan.FromSeconds(2.5),
            RepeatBehavior = RepeatBehavior.Forever
        });

        return brush;
    }

    private static GifAnimation LoadGifAnimation()
    {
        using var stream = typeof(SecretMessageWindow).Assembly.GetManifestResourceStream("Minecraft.SecretMessage.gif")
            ?? throw new InvalidDataException("Embedded secret GIF was not found.");
        var decoder = new GifBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidDataException("Embedded secret GIF contains no frames.");
        }

        var frames = decoder.Frames.ToArray();
        var delays = frames.Select(GetFrameDelay).ToArray();
        return new GifAnimation(frames, delays, frames[0].PixelWidth, frames[0].PixelHeight);
    }

    private static TimeSpan GetFrameDelay(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata &&
                metadata.GetQuery("/grctlext/Delay") is object rawDelay)
            {
                var centiseconds = Convert.ToInt32(rawDelay, CultureInfo.InvariantCulture);
                if (centiseconds > 0)
                {
                    return TimeSpan.FromMilliseconds(Math.Max(20, centiseconds * 10));
                }
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidCastException or FormatException)
        {
        }

        return TimeSpan.FromMilliseconds(100);
    }

    private static Size MeasureMessage(string text)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            MessageFontSize,
            Brushes.Black,
            1d);
        return new Size(
            Math.Max(1, formatted.WidthIncludingTrailingWhitespace),
            Math.Max(1, formatted.Height));
    }

    private static (double Width, double Height) CalculateInitialSize(double contentWidth, double contentHeight)
    {
        var width = Math.Clamp(
            contentWidth + ContentMargin * 2,
            MinBaseWidth,
            SystemParameters.WorkArea.Width * 0.9);
        var height = Math.Clamp(
            contentHeight + ContentMargin * 2,
            MinBaseHeight,
            SystemParameters.WorkArea.Height * 0.9);
        return (width, height);
    }

    private sealed record GifAnimation(
        IReadOnlyList<BitmapFrame> Frames,
        IReadOnlyList<TimeSpan> Delays,
        double PixelWidth,
        double PixelHeight);
}
