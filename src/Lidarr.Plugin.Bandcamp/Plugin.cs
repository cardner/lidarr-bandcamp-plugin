namespace NzbDrone.Core.Plugins
{
    /// <summary>
    /// Lidarr plugin: Bandcamp purchase library indexer + download client.
    /// Indexes the authenticated fan collection and downloads owned releases.
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
