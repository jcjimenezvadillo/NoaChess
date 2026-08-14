using System.Windows;
using NoaChess.GUI.Wpf.Theme;

namespace NoaChess.GUI.Wpf
{
    // Interaction logic for App.xaml
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Every window in the application gets a title bar that follows the
            // Windows theme. Registered once as a class handler rather than
            // added to each of the nine windows by hand, so a window added later
            // cannot forget it and show up with a white bar on a dark desktop.
            EventManager.RegisterClassHandler(
                typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, _) =>
                {
                    if (sender is Window window)
                        WindowChrome.FollowSystemTheme(window);
                }));
        }
    }
}
