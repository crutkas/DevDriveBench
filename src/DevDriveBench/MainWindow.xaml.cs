using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace DevDriveBench;

/// <summary>Hosts the single benchmark page; sized for a 1080p stage and toggles full screen on F11.</summary>
public sealed partial class MainWindow : Window
{
    private const int WidthDip = 1600;
    private const int HeightDip = 1000;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");

        IntPtr hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        double scale = GetDpiForWindow(hwnd) / 96.0;
        DisplayArea area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        int width = Math.Min((int)(WidthDip * scale), area.WorkArea.Width);
        int height = Math.Min((int)(HeightDip * scale), area.WorkArea.Height);
        AppWindow.MoveAndResize(new RectInt32(
            area.WorkArea.X + ((area.WorkArea.Width - width) / 2),
            area.WorkArea.Y + ((area.WorkArea.Height - height) / 2),
            width,
            height));

        RootFrame.Navigate(typeof(MainPage));
        AppWindow.Closing += OnClosing;
    }

    private bool _closing;

    /// <summary>Kept fixtures (clones, caches) are always deleted: hide the window, clean up, then exit.</summary>
    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closing || RootFrame.Content is not MainPage page || !page.ViewModel.NeedsShutdown)
        {
            return;
        }

        args.Cancel = true;
        _closing = true;
        sender.Hide();
        try
        {
            await page.ViewModel.ShutdownAsync();
        }
        finally
        {
            Application.Current.Exit();
        }
    }

    /// <summary>Switches between the overlapped window and full screen (stage mode).</summary>
    public void ToggleFullScreen()
    {
        bool full = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
        AppWindow.SetPresenter(full ? AppWindowPresenterKind.Overlapped : AppWindowPresenterKind.FullScreen);
        AppTitleBar.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
    }
}
