using System;
using System.Windows;

namespace PeripheralMonitor.Client.Wpf;

public partial class App : Application
{
    public App()
    {
        AppDomain.CurrentDomain.FirstChanceException += (sender, args) =>
        {
            if (args.Exception is System.Windows.Markup.XamlParseException ||
                args.Exception.Source == "PresentationFramework")
            {
                var inner = args.Exception;
                while (inner.InnerException != null)
                    inner = inner.InnerException;

                Console.WriteLine($"🔍 XAML FirstChance: {inner.Message}");
                Console.WriteLine($"📍 Source: {inner.Source}");
                Console.WriteLine(inner.StackTrace);
            }
        };

        DispatcherUnhandledException += (sender, args) =>
        {
            var inner = args.Exception;
            while (inner.InnerException != null)
                inner = inner.InnerException;

            Console.WriteLine($"❌ UI Exception: {args.Exception.Message}");
            Console.WriteLine($"🔍 Inner Cause: {inner.Message}");
            Console.WriteLine($"📍 Source: {inner.Source}");
            Console.WriteLine(inner.StackTrace);
            args.Handled = true;
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Console.WriteLine("✅ WPF Dashboard starting...");
        Console.WriteLine($"Working Dir: {Environment.CurrentDirectory}");
        Console.WriteLine($"Server URL: http://localhost:5000/monitor");
    }
}