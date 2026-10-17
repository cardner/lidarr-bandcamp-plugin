using System;
using System.IO;
using System.Linq;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;

namespace NzbDrone.Core.Download.Clients.Bandcamp
{
    /// <summary>
    /// Shared download-path checks for Test() and Download() so Queue/Test show
    /// actionable errors (especially Docker mounts like /downloads vs /downloads/downloads).
    /// </summary>
    internal static class BandcampPathGuard
    {
        public static string Normalize(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim();
        }

        /// <summary>
        /// Ensures the configured download root exists and is writable.
        /// Returns a user-facing error message, or null on success.
        /// </summary>
        public static string? EnsureWritable(IDiskProvider diskProvider, string downloadPath, Logger logger)
        {
            var path = Normalize(downloadPath);
            if (string.IsNullOrEmpty(path))
            {
                return "Download Path is required. In Docker use a path on a mounted volume (e.g. /downloads/bandcamp), not a host path.";
            }

            try
            {
                if (!diskProvider.FolderExists(path))
                {
                    logger.Info("Bandcamp download client: Creating missing download path '{0}'", path);
                    diskProvider.CreateFolder(path);
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Bandcamp download client: Failed to create download path '{0}'", path);
                return $"Download Path not creatable: '{path}' ({ex.Message}). " +
                       "In linuxserver/hotio Lidarr, the volume is usually mounted at /downloads — " +
                       "use /downloads/bandcamp (not /downloads/downloads unless you intentionally nested it).";
            }

            if (!diskProvider.FolderExists(path))
            {
                return $"Download Path does not exist: '{path}'. Check the container mount (often /downloads).";
            }

            if (!diskProvider.FolderWritable(path))
            {
                logger.Error("Bandcamp download client: Download Path '{0}' is not writable", path);
                return $"Download Path not writable: '{path}'. Fix volume permissions (PUID/PGID) or pick a folder under the mounted /downloads volume.";
            }

            // Prove we can create a child directory (matches grab-time album folders).
            var probe = Path.Combine(path, $".bandcamp-write-test-{Guid.NewGuid():N}");
            try
            {
                diskProvider.CreateFolder(probe);
                diskProvider.DeleteFolder(probe, true);
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Bandcamp download client: Write probe failed under '{0}'", path);
                return $"Download Path not writable: '{path}' ({ex.Message}).";
            }

            return null;
        }

        public static ValidationFailure? SuspiciousPathWarning(string downloadPath)
        {
            var path = Normalize(downloadPath).Replace('\\', '/').TrimEnd('/');
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            // Flag doubled segment: /downloads/downloads or /data/downloads/downloads
            if (parts.Length >= 2 &&
                string.Equals(parts[^1], parts[^2], StringComparison.OrdinalIgnoreCase) &&
                parts[^1].Equals("downloads", StringComparison.OrdinalIgnoreCase))
            {
                return new ValidationFailure(
                    "DownloadPath",
                    $"Download Path looks doubled ('{path}'). " +
                    "linuxserver/hotio usually mount the library at /downloads — prefer /downloads/bandcamp. " +
                    "Only keep /downloads/downloads if that folder exists inside the container on purpose.")
                {
                    Severity = FluentValidation.Severity.Warning
                };
            }

            return null;
        }

        public static string FormatDownloadFailure(string phase, string message)
        {
            var cleanPhase = string.IsNullOrWhiteSpace(phase) ? "unknown" : phase.Trim();
            var cleanMessage = string.IsNullOrWhiteSpace(message) ? "unknown error" : message.Trim();
            return $"Bandcamp download failed ({cleanPhase}): {cleanMessage}";
        }

        public static string FormatDownloadFailure(string phase, Exception exception)
        {
            if (exception == null)
            {
                return FormatDownloadFailure(phase, "unknown error");
            }

            // Prefer Message; if empty (rare), include exception type so Queue is never blank.
            var detail = string.IsNullOrWhiteSpace(exception.Message)
                ? $"{exception.GetType().Name}: unknown error"
                : exception.Message.Trim();

            // Unwrap one level of AggregateException for a clearer Queue tooltip.
            if (exception is AggregateException aggregate &&
                aggregate.InnerExceptions.Count == 1 &&
                aggregate.InnerExceptions[0] != null)
            {
                return FormatDownloadFailure(phase, aggregate.InnerExceptions[0]);
            }

            return FormatDownloadFailure(phase, detail);
        }
    }
}
