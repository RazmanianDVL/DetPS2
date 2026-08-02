using System;
using Avalonia;

namespace DetPS2.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Unit-level Soft-GS → Avalonia pack / V-flip contract (no window, no games).
        //   detps2-desktop --present-selftest
        foreach (string a in args)
        {
            if (string.Equals(a, "--present-selftest", StringComparison.OrdinalIgnoreCase))
            {
                string? err = SoftGsAvaloniaBlit.SelfTest();
                if (err != null)
                {
                    Console.Error.WriteLine("PRESENT-SELFTEST FAIL: " + err);
                    return 1;
                }
                Console.WriteLine("PRESENT-SELFTEST OK Soft-GS 0xAARRGGBB → Bgra8888 + V-flip");
                return 0;
            }
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
