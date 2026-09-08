using System.Threading;
using System.Windows;
using System.Windows.Threading;
using VoidClip.Audio;
using VoidClip.Models;
using VoidClip.Services;

namespace VoidClip;

/// <summary>Shared, app-lifetime services.</summary>
public static class Core
{
    public static AppSettings Settings { get; set; }
    public static LibraryService Library { get; } = new();
    public static CaptureService Capture { get; } = new();
    public static PlaybackEngine Playback { get; } = new();
    public static HotkeyService Hotkeys { get; } = new();
}

public partial class App : Application
{
    private Mutex _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        // one instance only — two capture engines on the same device is a bad time
        _single = new Mutex(true, "VoidClip.SingleInstance.9F3A", out var isNew);
        if (!isNew)
        {
            MessageBox.Show("VoidClip is already running.\n\nLook for it in the system tray.",
                "VoidClip", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        AppPaths.EnsureRoot();
        AppPaths.Log("─── VoidClip starting ───");

        // headless diagnostic: VoidClip.exe --selftest [outputFile]
        if (e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            Core.Settings = SettingsService.Load();
            var outFile = e.Args.FirstOrDefault(a => !a.StartsWith("--"))
                          ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "voidclip-selftest.txt");
            System.IO.File.WriteAllText(outFile, SelfTest.Run());
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            AppPaths.Log("FATAL: " + ex.ExceptionObject);

        Core.Settings = SettingsService.Load();
        Core.Library.Load(Core.Settings.ClipsFolder);

        base.OnStartup(e);
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppPaths.Log("UI exception: " + e.Exception);
        MessageBox.Show(
            "Something went wrong:\n\n" + e.Exception.Message +
            "\n\nDetails were written to:\n" + AppPaths.LogFile,
            "VoidClip", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Core.Hotkeys.Dispose();
            Core.Capture.Dispose();
            Core.Playback.Dispose();
            Core.Library.Save();
            if (Core.Settings != null) SettingsService.Save(Core.Settings);
        }
        catch { }
        AppPaths.Log("─── VoidClip exit ───");
        _single?.ReleaseMutex();
        base.OnExit(e);
    }
}
