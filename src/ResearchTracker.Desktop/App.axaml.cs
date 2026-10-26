using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ResearchTracker.Persistence;

namespace ResearchTracker.Desktop;
public partial class App : Application
{
    public static string DataRoot => WorkspaceLease.DefaultRoot;
    private FileStream? instanceLock;
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        var accent = ((Avalonia.Media.ISolidColorBrush)DesignTokens.Brush("Accent")).Color;
        SukiUI.SukiTheme.GetInstance().ChangeColorTheme(new SukiUI.Models.SukiColorTheme("Work Tracker", accent, accent));
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                Directory.CreateDirectory(DataRoot);
                instanceLock = WorkspaceLease.Acquire(DataRoot);
                desktop.Exit += (_, _) => instanceLock.Dispose();
                desktop.MainWindow = new MainWindow();
            }
            catch (Exception ex)
            {
                instanceLock?.Dispose();
                desktop.MainWindow = new Window { Title = "Work Tracker — storage error", Width = 650, Height = 280, Content = new TextBlock { Text = "Could not open the local workspace. Your database has not been replaced.\n\n" + ex.Message + "\n\nIf another Work Tracker window is open, use that window. Otherwise set RESEARCH_TRACKER_DATA to a writable local folder to retry.", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(24) } };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
