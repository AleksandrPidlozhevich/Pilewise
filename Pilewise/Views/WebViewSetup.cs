using Avalonia.Controls;
using Avalonia.Platform;
using Pilewise.Services;
using System;
using System.IO;

namespace Pilewise.Views;

internal static class WebViewSetup
{
    private static readonly string UserDataFolder = Path.Combine(AppPaths.DataDirectory, "WebView2");

    public static void Configure(NativeWebView webView)
    {
        if (!OperatingSystem.IsWindows()) return;

        Directory.CreateDirectory(UserDataFolder);
        webView.EnvironmentRequested += (_, args) =>
        {
            if (args is WindowsWebView2EnvironmentRequestedEventArgs win)
                win.UserDataFolder = UserDataFolder;
        };
    }
}