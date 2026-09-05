using System.Windows;
using System.Windows.Controls;

namespace MeshDrive.Tests;

[TestClass]
public sealed class WindowLayoutTests
{
    [STATestMethod]
    public void MainExplorerAndSyncLayoutsLoadWithoutStartingAgentOrShowingWindows()
    {
        var app = Application.Current;
        if (app is null) { var created = new MeshDrive.Windows.App(); created.InitializeComponent(); app = created; }
        Assert.IsNotNull(app.Resources["PanelBrush"]);
        var main = new MeshDrive.Windows.MainWindow();
        var explorer = new MeshDrive.Windows.StorageWindow();
        var sync = new MeshDrive.Windows.SyncWindow();
        try
        {
            foreach (var window in new Window[] { main, explorer, sync })
            {
                Assert.IsFalse(window.IsVisible);
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(window.Width - 48, window.Height - 80));
                content.Arrange(new Rect(0, 0, window.Width - 48, window.Height - 80));
                content.UpdateLayout();
                Assert.IsGreaterThan(0, content.ActualWidth);
                Assert.IsFalse(double.IsInfinity(content.DesiredSize.Height));
            }
            Assert.HasCount(3, ((TabControl)explorer.FindName("Sections")).Items);
            Assert.IsNotNull(sync.FindName("VersionCount"));
            Assert.IsNotNull(main.FindName("PairingSasText"));
        }
        finally { sync.Close(); explorer.Close(); main.Close(); }
    }
}
