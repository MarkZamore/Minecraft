using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Minecraft;

public partial class VoiceSettingsWindow : Window
{
    private const double BaseWidth = 430;
    private const double BaseHeight = 390;
    private readonly MainWindow _owner;
    private bool _isApplyingState;
    private bool _isCapturingPttBinding;

    public VoiceSettingsWindow(MainWindow owner, double ownerScale)
    {
        _owner = owner;
        InitializeComponent();
        Owner = owner;
        ApplyOwnerScale(ownerScale);
    }

    public void ApplyState(
        IReadOnlyList<VoiceAudioDevice> inputDevices,
        IReadOnlyList<VoiceAudioDevice> outputDevices,
        string selectedInputDeviceId,
        string selectedOutputDeviceId,
        string pttMode,
        string pttBindingDisplayName,
        double inputVolume,
        double outputVolume)
    {
        _isApplyingState = true;
        try
        {
            InputDeviceComboBox.ItemsSource = inputDevices;
            OutputDeviceComboBox.ItemsSource = outputDevices;
            InputDeviceComboBox.SelectedItem = inputDevices.FirstOrDefault(device =>
                string.Equals(device.Id, selectedInputDeviceId, StringComparison.OrdinalIgnoreCase)) ??
                (inputDevices.Count > 0 ? inputDevices[0] : null);
            OutputDeviceComboBox.SelectedItem = outputDevices.FirstOrDefault(device =>
                string.Equals(device.Id, selectedOutputDeviceId, StringComparison.OrdinalIgnoreCase)) ??
                (outputDevices.Count > 0 ? outputDevices[0] : null);

            SelectPttMode(pttMode);
            PttBindingButton.Content = _isCapturingPttBinding
                ? "Нажмите клавишу или боковую кнопку мыши..."
                : pttBindingDisplayName;

            InputVolumeSlider.Value = Math.Clamp(inputVolume, 0d, 2d);
            OutputVolumeSlider.Value = Math.Clamp(outputVolume, 0d, 2d);
            InputVolumeText.Text = FormatPercent(InputVolumeSlider.Value);
            OutputVolumeText.Text = FormatPercent(OutputVolumeSlider.Value);
        }
        finally
        {
            _isApplyingState = false;
        }
    }

    private void ApplyOwnerScale(double ownerScale)
    {
        var scale = Math.Clamp(ownerScale, 0.7, 1.8);
        Width = BaseWidth * scale;
        Height = BaseHeight * scale;
        MinWidth = 330 * scale;
        MinHeight = 300 * scale;
    }

    private void SelectPttMode(string pttMode)
    {
        foreach (var item in PttModeComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, pttMode, StringComparison.OrdinalIgnoreCase))
            {
                PttModeComboBox.SelectedItem = item;
                return;
            }
        }

        PttModeComboBox.SelectedIndex = 0;
    }

    private static string FormatPercent(double value) => $"{Math.Round(value * 100d)}%";

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _owner.RefreshVoiceSettingsWindow(this);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _owner.ClearVoiceSettingsWindow(this);
    }

    private void InputDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingState || InputDeviceComboBox.SelectedItem is not VoiceAudioDevice device)
        {
            return;
        }

        _owner.SetVoiceInputDevice(device);
    }

    private void OutputDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingState || OutputDeviceComboBox.SelectedItem is not VoiceAudioDevice device)
        {
            return;
        }

        _owner.SetVoiceOutputDevice(device);
    }

    private void PttModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingState || PttModeComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        _owner.SetVoicePttMode((item.Tag as string) ?? "Off");
    }

    private void PttBindingButton_Click(object sender, RoutedEventArgs e)
    {
        _isCapturingPttBinding = true;
        PttBindingButton.Content = "Нажмите клавишу или боковую кнопку мыши...";
        Keyboard.Focus(PttBindingButton);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isCapturingPttBinding)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.None or Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return;
        }

        _isCapturingPttBinding = false;
        _owner.SetVoicePushToTalkBinding($"Key:{key}");
        e.Handled = true;
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isCapturingPttBinding)
        {
            return;
        }

        if (e.ChangedButton is not (MouseButton.XButton1 or MouseButton.XButton2))
        {
            return;
        }

        _isCapturingPttBinding = false;
        _owner.SetVoicePushToTalkBinding(e.ChangedButton == MouseButton.XButton1 ? "Mouse:XButton1" : "Mouse:XButton2");
        e.Handled = true;
    }

    private void InputVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isApplyingState)
        {
            return;
        }

        InputVolumeText.Text = FormatPercent(e.NewValue);
        _owner.SetVoiceInputVolume(e.NewValue);
    }

    private void OutputVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isApplyingState)
        {
            return;
        }

        OutputVolumeText.Text = FormatPercent(e.NewValue);
        _owner.SetVoiceOutputVolume(e.NewValue);
    }
}
