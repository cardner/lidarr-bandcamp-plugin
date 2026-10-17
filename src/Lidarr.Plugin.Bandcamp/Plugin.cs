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
        /// Public GitHub mirror URL for Lidarr System → Plugins.
        /// Origin is source of truth; after Ryan creates the public repo, replace
        /// this with https://github.com/YOUR_GITHUB_USER/lidarr-bandcamp-plugin
        /// (see docs/github-mirror.md). Placeholder 404s until then.
        /// </summary>
        public override string GithubUrl => "https://github.com/cursor-project/lidarr-plugin-bandcamp-library";
    }
}
