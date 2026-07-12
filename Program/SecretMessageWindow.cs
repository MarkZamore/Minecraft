using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Minecraft;

public sealed class SecretMessageWindow : Window
{
    private const double MinBaseWidth = 320;
    private const double MinBaseHeight = 190;

    public SecretMessageWindow(double ownerScale)
    {
        Title = SecretMessage.Title;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var baseSize = CalculateInitialSize(SecretMessage.Text);
        var scale = Math.Clamp(ownerScale, 0.7, 1.8);
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
            Width = baseSize.Width - 28,
            Height = baseSize.Height - 28,
            Margin = new Thickness(14)
        };

        var brush = CreateAnimatedRgbBrush();
        var message = new TextBlock
        {
            Text = SecretMessage.Text.Trim(),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = brush
        };
        layout.Children.Add(message);
        root.Child = layout;
        Content = root;
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

    private static (double Width, double Height) CalculateInitialSize(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        var lineCount = Math.Max(1, lines.Length);
        var longestLine = Math.Max(1, lines.Max(line => line.Trim().Length));

        var width = Math.Clamp(160 + longestLine * 9, MinBaseWidth, SystemParameters.WorkArea.Width * 0.9);
        var height = Math.Clamp(150 + lineCount * 26, MinBaseHeight, SystemParameters.WorkArea.Height * 0.9);
        return (width, height);
    }
}
