using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Glint;

/// <summary>The small sound panel that drops down from Skyline's volume icon: the main volume, then each app's.</summary>
public partial class VolumeFlyout : BarFlyout
{
    const int MaxApps = 6;

    readonly Action _onChanged;
    bool _syncing;

    public VolumeFlyout(Action onChanged)
    {
        InitializeComponent();
        _onChanged = onChanged;
    }

    protected override FrameworkElement Card => CardBorder;

    protected override void Refresh()
    {
        Sync();
        BuildApps();
    }

    /// <summary>Reads the current level and mute state into the controls.</summary>
    public void Sync()
    {
        _syncing = true;
        int level = SystemControl.Volume ?? 0;
        bool muted = SystemControl.Muted == true;
        VolumeSlider.Value = level;
        VolumeSlider.Opacity = muted ? 0.45 : 1;
        PercentText.Text = level + "%";
        MuteIcon.Text = muted ? "" : level == 0 ? "" : level <= 33 ? "" : level <= 66 ? "" : "";
        MuteButton.ToolTip = muted ? "Unmute" : "Mute";
        DeviceText.Text = SystemControl.OutputDeviceName ?? "";
        _syncing = false;
    }

    void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        SystemControl.SetVolume((int)Math.Round(e.NewValue)); // also unmutes, like dragging the system slider
        Sync();
        _onChanged();
    }

    void OnMuteClick(object sender, MouseButtonEventArgs e)
    {
        SystemControl.SetMute(SystemControl.Muted != true);
        Sync();
        _onChanged();
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left or Key.Down: VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 2); break;
            case Key.Right or Key.Up: VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + 2); break;
            default: return;
        }
        e.Handled = true;
    }

    // ---------- apps ----------

    /// <summary>One row per app with sound open, then resizes the panel to fit them.</summary>
    void BuildApps()
    {
        AppsPanel.Children.Clear();
        var apps = SystemControl.AppVolumes().Take(MaxApps).ToList();
        foreach (var app in apps) AppsPanel.Children.Add(AppRow(app));
        AppsSection.Visibility = apps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        Root.Measure(new Size(Width, double.PositiveInfinity));
        Height = Math.Ceiling(Root.DesiredSize.Height);
    }

    FrameworkElement AppRow(SystemControl.AppVolume app)
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });

        // The app's icon doubles as its mute button, like the master one.
        var icon = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Cursor = Cursors.Hand,
            Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Bottom,
        };
        icon.SetResourceReference(Border.BackgroundProperty, "FlyHover");
        icon.Child = AppIcon(app);
        grid.Children.Add(icon);

        var middle = new StackPanel { Margin = new Thickness(12, 0, 4, 0) };
        var name = new TextBlock { Text = app.Name, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 0, 0, -2) };
        name.SetResourceReference(TextBlock.ForegroundProperty, "FlySubtle");
        var slider = new Slider
        {
            Minimum = 0, Maximum = 100, SmallChange = 2, LargeChange = 10, IsMoveToPointEnabled = true,
            Style = (Style)FindResource("GlassSlider"), Value = app.Volume,
        };
        middle.Children.Add(name);
        middle.Children.Add(slider);
        Grid.SetColumn(middle, 1);
        grid.Children.Add(middle);

        var percent = new TextBlock
        {
            Text = app.Volume + "%", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 4), FontWeight = FontWeights.SemiBold,
        };
        System.Windows.Documents.Typography.SetNumeralAlignment(percent, FontNumeralAlignment.Tabular);
        percent.SetResourceReference(TextBlock.ForegroundProperty, "FlyText");
        Grid.SetColumn(percent, 2);
        grid.Children.Add(percent);

        void ShowMuted(bool muted)
        {
            icon.Child.Opacity = muted ? 0.35 : 1;
            slider.Opacity = muted ? 0.45 : 1;
            icon.ToolTip = (muted ? "Unmute " : "Mute ") + app.Name;
        }
        ShowMuted(app.Muted);

        slider.ValueChanged += (_, e) =>
        {
            int level = (int)Math.Round(e.NewValue);
            app.SetVolume(level); // also unmutes it
            percent.Text = level + "%";
            ShowMuted(app.Muted);
        };
        icon.MouseLeftButtonUp += (_, _) =>
        {
            app.SetMute(!app.Muted);
            ShowMuted(app.Muted);
        };
        return grid;
    }

    static UIElement AppIcon(SystemControl.AppVolume app)
    {
        if (app.ExePath == null)
        {
            var glyph = new TextBlock
            {
                Text = app.IsSystem ? "" : "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "FlyText");
            return glyph;
        }
        // The launcher's icon loader fetches it off the UI thread and caches it; the image fills in when ready.
        var item = new ResultItem { IconKey = app.ExePath, IconIsFile = true };
        var image = new Image { Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        image.SetBinding(Image.SourceProperty, new Binding(nameof(ResultItem.Icon)) { Source = item });
        IconLoader.Load(item);
        return image;
    }
}
