using System;
using System.IO;
using FluentValidation;
using Moq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download.Clients.Bandcamp;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.Test.Download.Clients.Bandcamp
{
    public class BandcampPathGuardFixture
    {
        private readonly Logger _logger = LogManager.GetCurrentClassLogger();

        [Fact]
        public void EnsureWritable_EmptyPath_ReturnsRequiredMessage()
        {
            var disk = new Mock<IDiskProvider>();
            var error = BandcampPathGuard.EnsureWritable(disk.Object, "  ", _logger);

            Assert.NotNull(error);
            Assert.Contains("required", error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("/downloads", error);
        }

        [Fact]
        public void EnsureWritable_CreateFolderFails_ReturnsNotCreatableMessage()
        {
            var disk = new Mock<IDiskProvider>();
            disk.Setup(d => d.FolderExists("/downloads/downloads")).Returns(false);
            disk.Setup(d => d.CreateFolder("/downloads/downloads"))
                .Throws(new UnauthorizedAccessException("Permission denied"));

            var error = BandcampPathGuard.EnsureWritable(disk.Object, "/downloads/downloads", _logger);

            Assert.NotNull(error);
            Assert.Contains("not creatable", error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("/downloads/downloads", error);
            Assert.Contains("Permission denied", error);
        }

        [Fact]
        public void EnsureWritable_FolderNotWritable_ReturnsNotWritableMessage()
        {
            var disk = new Mock<IDiskProvider>();
            disk.Setup(d => d.FolderExists("/downloads/downloads")).Returns(true);
            disk.Setup(d => d.FolderWritable("/downloads/downloads")).Returns(false);

            var error = BandcampPathGuard.EnsureWritable(disk.Object, "/downloads/downloads", _logger);

            Assert.NotNull(error);
            Assert.StartsWith("Download Path not writable:", error);
            Assert.Contains("/downloads/downloads", error);
        }

        [Fact]
        public void EnsureWritable_WriteProbeFails_ReturnsNotWritableWithException()
        {
            var disk = new Mock<IDiskProvider>();
            disk.Setup(d => d.FolderExists("/downloads/bandcamp")).Returns(true);
            disk.Setup(d => d.FolderWritable("/downloads/bandcamp")).Returns(true);
            disk.Setup(d => d.CreateFolder(It.Is<string>(p => p.Contains(".bandcamp-write-test-", StringComparison.Ordinal))))
                .Throws(new IOException("Read-only file system"));

            var error = BandcampPathGuard.EnsureWritable(disk.Object, "/downloads/bandcamp", _logger);

            Assert.NotNull(error);
            Assert.StartsWith("Download Path not writable:", error);
            Assert.Contains("Read-only file system", error);
        }

        [Fact]
        public void EnsureWritable_HappyPath_CreatesMissingFolderAndProbesWrite()
        {
            var disk = new Mock<IDiskProvider>();
            disk.Setup(d => d.FolderExists("/downloads/bandcamp")).Returns(false);
            disk.Setup(d => d.FolderWritable("/downloads/bandcamp")).Returns(true);
            string? createdProbe = null;
            disk.Setup(d => d.CreateFolder(It.IsAny<string>()))
                .Callback<string>(p =>
                {
                    if (p == "/downloads/bandcamp")
                    {
                        disk.Setup(d => d.FolderExists("/downloads/bandcamp")).Returns(true);
                    }
                    else
                    {
                        createdProbe = p;
                    }
                });

            var error = BandcampPathGuard.EnsureWritable(disk.Object, " /downloads/bandcamp ", _logger);

            Assert.Null(error);
            disk.Verify(d => d.CreateFolder("/downloads/bandcamp"), Times.Once);
            Assert.NotNull(createdProbe);
            Assert.Contains(".bandcamp-write-test-", createdProbe);
            disk.Verify(d => d.DeleteFolder(createdProbe!, true), Times.Once);
        }

        [Theory]
        [InlineData("/downloads/downloads")]
        [InlineData("/downloads/downloads/")]
        [InlineData("/data/downloads/downloads")]
        public void SuspiciousPathWarning_DoubledDownloads_ReturnsWarning(string path)
        {
            var warning = BandcampPathGuard.SuspiciousPathWarning(path);

            Assert.NotNull(warning);
            Assert.Equal("DownloadPath", warning!.PropertyName);
            Assert.Equal(Severity.Warning, warning.Severity);
            Assert.Contains("doubled", warning.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("/downloads/bandcamp", warning.ErrorMessage);
        }

        [Theory]
        [InlineData("/downloads/bandcamp")]
        [InlineData("/downloads")]
        [InlineData("/data/media/downloads")]
        public void SuspiciousPathWarning_NormalPaths_ReturnsNull(string path)
        {
            Assert.Null(BandcampPathGuard.SuspiciousPathWarning(path));
        }

        [Fact]
        public void FormatDownloadFailure_EmptyMessage_UsesUnknownError()
        {
            var message = BandcampPathGuard.FormatDownloadFailure("file_download", "  ");

            Assert.Equal("Bandcamp download failed (file_download): unknown error", message);
        }

        [Fact]
        public void FormatDownloadFailure_PreservesPhaseAndMessage()
        {
            var message = BandcampPathGuard.FormatDownloadFailure(
                "extraction",
                "Download Path not writable: '/downloads/downloads/x'");

            Assert.Equal(
                "Bandcamp download failed (extraction): Download Path not writable: '/downloads/downloads/x'",
                message);
        }
    }
}
