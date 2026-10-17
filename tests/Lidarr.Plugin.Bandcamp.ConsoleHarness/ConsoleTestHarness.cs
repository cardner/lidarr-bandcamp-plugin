using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Core.Download.Clients.Bandcamp;
using NzbDrone.Core.Http.Bandcamp;

namespace Lidarr.Plugin.Bandcamp.ConsoleHarness
{
    /// <summary>
    /// Live smoke harness against real Bandcamp (never run in CI without a secret).
    ///
    /// Env (preferred):
    ///   BANDCAMP_IDENTITY_COOKIE  — bare identity value or full Cookie header
    ///   BANDCAMP_COOKIES          — legacy alias for the same value
    ///
    /// Optional:
    ///   BANDCAMP_LIVE_DOWNLOAD=1  — also download the first owned item to ./live-artifacts
    ///   BANDCAMP_LIVE_FORMAT      — format key (default: flac)
    ///
    /// Usage:
    ///   export BANDCAMP_IDENTITY_COOKIE='…'
    ///   dotnet run --project tests/Lidarr.Plugin.Bandcamp.ConsoleHarness
    /// </summary>
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var config = new NLog.Config.LoggingConfiguration();
            var consoleTarget = new NLog.Targets.ConsoleTarget("console")
            {
                Layout = "${time} [${level:uppercase=true}] ${message} ${exception:format=tostring}"
            };
            config.AddTarget(consoleTarget);
            config.AddRule(LogLevel.Info, LogLevel.Fatal, consoleTarget);
            LogManager.Configuration = config;

            var logger = LogManager.GetCurrentClassLogger();
            var cookies = ResolveCookies(args);

            if (string.IsNullOrWhiteSpace(cookies))
            {
                PrintUsage();
                return 1;
            }

            Console.WriteLine("=== Bandcamp live smoke (collection auth → list → optional download) ===");
            Console.WriteLine($"Cookie input length: {cookies.Trim().Length} chars (value not printed)");
            Console.WriteLine();

            using var native = new BandcampNativeHttp(logger);
            // Native-backed client: browser UA never touches Lidarr ManagedHttpDispatcher.
            var http = BandcampHttpClient.ForNative(native, logger);
            var api = new BandcampApiClient(http, logger);

            var probe = await api.ProbeAuthAsync(cookies).ConfigureAwait(false);
            if (!probe.Success || probe.FanId == null)
            {
                Console.WriteLine("AUTH FAILED");
                Console.WriteLine(probe.Message);
                return 2;
            }

            Console.WriteLine($"AUTH OK — fan_id={probe.FanId}");
            var normalized = probe.NormalizedCookies;

            var items = await api.GetDownloadableCollectionAsync(normalized, probe.FanId.Value, maxPages: 2)
                .ConfigureAwait(false);

            Console.WriteLine($"COLLECTION — {items.Count} downloadable item(s) on first page(s)");
            foreach (var item in items.Take(10))
            {
                Console.WriteLine($"  - {item.BandName} / {item.Title} ({item.ItemType}:{item.ItemId})");
                if (!string.IsNullOrWhiteSpace(item.ItemUrl))
                {
                    Console.WriteLine($"      {item.ItemUrl}");
                }
            }

            if (items.Count == 0)
            {
                Console.WriteLine("No downloadable purchases found. Auth worked; collection may be empty.");
                return 3;
            }

            var doDownload = string.Equals(
                Environment.GetEnvironmentVariable("BANDCAMP_LIVE_DOWNLOAD"),
                "1",
                StringComparison.OrdinalIgnoreCase);

            if (!doDownload)
            {
                Console.WriteLine();
                Console.WriteLine("Skip download (set BANDCAMP_LIVE_DOWNLOAD=1 to fetch the first owned release).");
                Console.WriteLine("=== Smoke OK (auth + collection list) ===");
                return 0;
            }

            var target = items.First(i => !string.IsNullOrWhiteSpace(i.DownloadPageUrl));
            var format = Environment.GetEnvironmentVariable("BANDCAMP_LIVE_FORMAT");
            if (string.IsNullOrWhiteSpace(format))
            {
                format = "flac";
            }

            var outDir = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "live-artifacts"));
            Directory.CreateDirectory(outDir);

            Console.WriteLine();
            Console.WriteLine($"DOWNLOAD — {target.BandName} / {target.Title} format={format}");
            Console.WriteLine($"Output dir: {outDir}");

            var page = await api.GetDownloadPageDataAsync(normalized, target.DownloadPageUrl!).ConfigureAwait(false);
            if (page == null || page.DownloadItems.Count == 0)
            {
                Console.WriteLine("Could not parse download page data.");
                return 4;
            }

            var downloadItem = page.DownloadItems.FirstOrDefault(i => i.ItemId == target.ItemId)
                               ?? page.DownloadItems.First();

            if (!downloadItem.DownloadUrls.TryGetValue(format!, out var formatUrl) ||
                string.IsNullOrWhiteSpace(formatUrl))
            {
                format = downloadItem.DownloadUrls.Keys.FirstOrDefault() ?? format;
                downloadItem.DownloadUrls.TryGetValue(format!, out formatUrl);
            }

            if (string.IsNullOrWhiteSpace(formatUrl))
            {
                Console.WriteLine("No download URL for any format on this purchase.");
                return 5;
            }

            var resolved = await api.ResolveStatdownloadUrlAsync(normalized, formatUrl!, format!).ConfigureAwait(false);
            byte[]? payload = null;
            string? fileUrl = null;

            if (resolved?.DirectResponse?.ResponseData is { Length: > 0 } directBytes)
            {
                payload = directBytes;
            }
            else
            {
                fileUrl = resolved?.DownloadUrl ?? formatUrl!;
                Console.WriteLine($"Resolved file URL host: {new Uri(fileUrl).Host}");
                var bytes = await native.GetBytesAsync(fileUrl, normalized).ConfigureAwait(false);
                if (bytes.RawBytes == null || bytes.RawBytes.Length == 0)
                {
                    Console.WriteLine($"Download failed with HTTP {bytes.StatusCode}");
                    return 6;
                }

                payload = bytes.RawBytes;
            }

            var safeName = string.Join("_", $"{target.BandName}-{target.Title}".Split(Path.GetInvalidFileNameChars()));
            var path = Path.Combine(outDir, $"{safeName}.zip");
            await File.WriteAllBytesAsync(path, payload!).ConfigureAwait(false);

            Console.WriteLine($"Wrote {payload!.Length} bytes → {path}");
            Console.WriteLine("=== Smoke OK (auth + collection + download) ===");
            return 0;
        }

        private static string ResolveCookies(string[] args)
        {
            if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
            {
                return args[0];
            }

            return Environment.GetEnvironmentVariable("BANDCAMP_IDENTITY_COOKIE")
                   ?? Environment.GetEnvironmentVariable("BANDCAMP_COOKIES")
                   ?? string.Empty;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Bandcamp live smoke harness");
            Console.WriteLine();
            Console.WriteLine("Set one of:");
            Console.WriteLine("  BANDCAMP_IDENTITY_COOKIE   preferred (bare identity or Cookie header)");
            Console.WriteLine("  BANDCAMP_COOKIES           legacy alias");
            Console.WriteLine("Or pass the cookie as argv[0].");
            Console.WriteLine();
            Console.WriteLine("Optional:");
            Console.WriteLine("  BANDCAMP_LIVE_DOWNLOAD=1   download first owned release");
            Console.WriteLine("  BANDCAMP_LIVE_FORMAT=flac  format key");
            Console.WriteLine();
            Console.WriteLine("Example:");
            Console.WriteLine("  export BANDCAMP_IDENTITY_COOKIE='…from browser DevTools…'");
            Console.WriteLine("  dotnet run --project tests/Lidarr.Plugin.Bandcamp.ConsoleHarness");
        }
    }
}
