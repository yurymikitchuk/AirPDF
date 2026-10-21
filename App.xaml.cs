using System.IO;
using System.Windows;
using System.Windows.Threading;
using AirPDF.Services;

namespace AirPDF;

public partial class App : Application
{
    /// <summary>
    /// PDF path from "Open with" / shell launch: AirPDF.exe "C:\file.pdf".
    /// </summary>
    public static string? StartupFilePath { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "AirPDF — ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        StartupFilePath = ResolveStartupPdfPath(e.Args);
        ThemeService.Initialize(ThemeService.Parse(AppSettingsStore.Load().Theme));

        base.OnStartup(e);

        // Load PDFium and warm the thread pool while the first frame paints.
        ThreadPool.GetMinThreads(out var worker, out var io);
        ThreadPool.SetMinThreads(Math.Max(worker, 4), Math.Max(io, 4));
        _ = Task.Run(PdfRenderer.Warmup);
    }

    private static string? ResolveStartupPdfPath(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg))
                continue;

            var candidate = arg.Trim().Trim('"');
            if (candidate.StartsWith('-') || candidate.StartsWith('/'))
                continue;

            try
            {
                var fullPath = Path.GetFullPath(candidate);
                if (File.Exists(fullPath) &&
                    fullPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    return fullPath;
            }
            catch
            {
                // Ignore malformed shell arguments.
            }
        }

        return null;
    }
}
