using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using AudioPlayer.Services;

namespace AudioPlayer.Views;

public partial class EnvironmentWindow : Window
{
    public EnvironmentWindow(EnvironmentReport report) { InitializeComponent(); DataContext = report; }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void OpenLink(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception ex) { MessageBox.Show(this, ex.Message, "无法打开浏览器"); }
        e.Handled = true;
    }
}
