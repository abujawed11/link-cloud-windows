using System.Configuration;
using System.Data;
using System.Windows;
using LibVLCSharp.Shared;

namespace LinkCloudPlayer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Core.Initialize(); // loads libvlc from the VideoLAN.LibVLC.Windows package; must run once before any LibVLC instance
    }
}
