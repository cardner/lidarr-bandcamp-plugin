namespace NzbDrone.Core.Plugins
{
    /// <summary>
    /// Lidarr plugin: Bandcamp purchase library indexer, download client, and Import List.
    /// Indexes the authenticated fan collection, imports owned albums into Lidarr, and downloads releases.
    /// </summary>
    public class BandcampPlugin : Plugin
    {
        public override string Name => "Bandcamp Library";
        public override string Owner => "cursor-project";
        /// <summary>
        /// Public GitHub mirror for Lidarr System → Plugins (Origin is source of truth;
        /// sync with scripts/mirror-to-github.sh — see docs/github-mirror.md).
        /// </summary>
        public override string GithubUrl => "https://github.com/cardner/lidarr-bandcamp-plugin";
    }
}
