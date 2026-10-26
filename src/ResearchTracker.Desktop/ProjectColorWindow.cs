using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ResearchTracker.Core;

namespace ResearchTracker.Desktop;

public sealed class ProjectColorWindow : Window
{
    public ProjectColorWindow(string initial)
    {
        Title = "Project color"; Width = 440; Height = 560; MinHeight = 540; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto"), Margin = new Thickness(20) }; Content = grid;
        var picker = new ColorView { Name = "ProjectColorBoard", Color = Color.Parse(initial), ColorModel = ColorModel.Rgba, IsAlphaEnabled = false, IsAlphaVisible = false, IsColorPaletteVisible = false, IsColorPreviewVisible = false, IsAccentColorsVisible = false, IsColorSpectrumVisible = true, IsColorSpectrumSliderVisible = true, IsColorComponentsVisible = true, IsComponentSliderVisible = true, IsComponentTextInputVisible = true, IsHexInputVisible = true };
        grid.Children.Add(new ScrollViewer { Content = picker });
        var rgb = new StackPanel { Spacing = 6, Margin = new Thickness(0, 10, 0, 0) }; Grid.SetRow(rgb, 1); grid.Children.Add(rgb);
        var sliders = new List<Slider>(); var numbers = new List<NumericUpDown>(); var syncing = false;
        foreach (var channel in new[] { "R", "G", "B" })
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*,8,80") }; row.Children.Add(new TextBlock { Text = channel, VerticalAlignment = VerticalAlignment.Center });
            var slider = new Slider { Name = "ProjectRgb" + channel, Minimum = 0, Maximum = 255, TickFrequency = 1, IsSnapToTickEnabled = true }; var number = new NumericUpDown { Minimum = 0, Maximum = 255, Increment = 1, FormatString = "0" };
            Grid.SetColumn(slider, 1); Grid.SetColumn(number, 3); row.Children.Add(slider); row.Children.Add(number); sliders.Add(slider); numbers.Add(number); rgb.Children.Add(row);
        }
        void Sync()
        {
            syncing = true; var channels = new[] { picker.Color.R, picker.Color.G, picker.Color.B };
            for (var i = 0; i < 3; i++) { sliders[i].Value = channels[i]; numbers[i].Value = channels[i]; } syncing = false;
        }
        picker.ColorChanged += (_, _) => Sync();
        for (var i = 0; i < 3; i++)
        {
            var channel = i;
            sliders[i].ValueChanged += (_, _) => { if (!syncing) picker.Color = Color.FromRgb((byte)sliders[0].Value, (byte)sliders[1].Value, (byte)sliders[2].Value); };
            numbers[i].ValueChanged += (_, _) => { if (!syncing && numbers[channel].Value is { } value) sliders[channel].Value = (double)value; };
        }
        Sync();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) }; Grid.SetRow(buttons, 2); grid.Children.Add(buttons);
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => Close(); buttons.Children.Add(cancel);
        var save = new Button { Name = "SaveProjectColor", Content = "Use color", Classes = { "accent" } }; save.Click += (_, _) => Close($"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}"); buttons.Children.Add(save);
    }
}

public sealed class NewProjectWindow : Window
{
    public NewProjectWindow(string initialColor, string initialName = "")
    {
        Title = "New project"; Width = 460; Height = 410; CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 10 }; Content = panel; var selectedColor = initialColor;
        panel.Children.Add(new TextBlock { Text = "Project name", Classes = { "label" } });
        var name = new TextBox { Name = "NewProjectTitle", Text = initialName, PlaceholderText = "Project name" }; panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = "Description", Classes = { "label" } });
        var description = new TextBox { Name = "NewProjectDescription", AcceptsReturn = true, MinHeight = 64, VerticalContentAlignment = VerticalAlignment.Top, TextWrapping = TextWrapping.Wrap }; panel.Children.Add(description);
        panel.Children.Add(new TextBlock { Text = "Project color", Classes = { "label" } });
        var presets = new WrapPanel { Name = "NewProjectColorPresets", Orientation = Orientation.Horizontal }; panel.Children.Add(presets);
        var color = new Button { Name = "NewProjectColor", Content = "More colors…", BorderBrush = new SolidColorBrush(Color.Parse(selectedColor)), BorderThickness = new Thickness(4, 1, 1, 1) };
        void UpdatePresets() { foreach (var swatch in presets.Children.OfType<Button>()) { swatch.BorderBrush = DesignTokens.Brush((string)swatch.Tag! == selectedColor ? "TextPrimary" : "Border"); swatch.BorderThickness = new Thickness((string)swatch.Tag! == selectedColor ? 2 : 1); } }
        foreach (var hex in GraphColors.ProjectPalette)
        {
            var swatch = new Button { Tag = hex, Width = 26, Height = 24, MinHeight = 24, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 0), Background = new SolidColorBrush(Color.Parse(hex)) };
            ToolTip.SetTip(swatch, hex); Avalonia.Automation.AutomationProperties.SetName(swatch, "Project color " + hex);
            swatch.Click += (_, _) => { selectedColor = hex; color.BorderBrush = new SolidColorBrush(Color.Parse(hex)); UpdatePresets(); }; presets.Children.Add(swatch);
        }
        UpdatePresets(); panel.Children.Add(color);
        color.Click += async (_, _) => { var selected = await new ProjectColorWindow(selectedColor).ShowDialog<string?>(this); if (selected != null) { selectedColor = selected; color.BorderBrush = new SolidColorBrush(Color.Parse(selected)); UpdatePresets(); } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 }; panel.Children.Add(buttons);
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => Close(); buttons.Children.Add(cancel);
        var create = new Button { Name = "CreateProject", Content = "Create project", IsEnabled = !string.IsNullOrWhiteSpace(name.Text), Classes = { "accent" } }; buttons.Children.Add(create);
        name.TextChanged += (_, _) => create.IsEnabled = !string.IsNullOrWhiteSpace(name.Text);
        void Create() { if (!string.IsNullOrWhiteSpace(name.Text)) Close(new Project { Title = name.Text.Trim(), Summary = description.Text?.Trim() ?? "", ColorHex = selectedColor }); }
        create.Click += (_, _) => Create(); name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Create(); e.Handled = true; } }; Opened += (_, _) => name.Focus();
    }
}
