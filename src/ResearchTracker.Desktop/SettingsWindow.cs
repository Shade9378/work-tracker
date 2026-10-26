using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ResearchTracker.Core;

namespace ResearchTracker.Desktop;

public sealed class SettingsWindow : Window
{
    public SettingsWindow(AppearancePreferences initial, Action<AppearancePreferences> save)
    {
        Title = "Settings"; Width = 400; Height = 310; CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 12 }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "Appearance", FontSize = DesignTokens.Title, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Theme", Classes = { "label" } });
        var theme = new ComboBox { Name = "ThemeChoice", ItemsSource = new[] { "Dark", "Light" }, SelectedItem = initial.Theme, HorizontalAlignment = HorizontalAlignment.Stretch }; panel.Children.Add(theme);
        var startup = new CheckBox { Name = "StartWithWindows", Content = "Start with Windows", IsChecked = initial.StartWithWindows };
        ToolTip.SetTip(startup, "Open Work Tracker automatically when you sign in to Windows."); panel.Children.Add(startup);
        var error = new TextBlock { Name = "SettingsError", Foreground = DesignTokens.Brush("Warning"), TextWrapping = TextWrapping.Wrap }; panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => Close(); buttons.Children.Add(cancel);
        var apply = new Button { Name = "SaveSettings", Content = "Save", Classes = { "accent" } }; buttons.Children.Add(apply); panel.Children.Add(buttons);
        apply.Click += (_, _) => { try { save(new AppearancePreferences { Theme = theme.SelectedItem as string ?? "Dark", StartWithWindows = startup.IsChecked == true }); Close(); } catch (Exception ex) { error.Text = ex.Message; } };
    }
}
