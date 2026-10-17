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
        /// Publish this repository to GitHub, then set this URL so Lidarr's
        /// System → Plugins installer can pull releases from it.
        /// </summary>
        public override string GithubUrl => "https://github.com/cursor-project/lidarr-plugin-bandcamp-library";
    }
}
