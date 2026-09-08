using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
namespace EdgeDock.Controls;
public sealed partial class PcShortcutsWidget : UserControl
{
 public PcShortcutsWidget() => InitializeComponent();
 private static async void Open(string value) => await Launcher.LaunchUriAsync(new Uri(value));
 private void Sound_Click(object s, RoutedEventArgs e) => Open("ms-settings:sound");
 private void Display_Click(object s, RoutedEventArgs e) => Open("ms-settings:display");
 private void Bluetooth_Click(object s, RoutedEventArgs e) => Open("ms-settings:bluetooth");
 private void Network_Click(object s, RoutedEventArgs e) => Open("ms-settings:network");
}
