namespace NzbDrone.Core.ImportLists.Bandcamp
{
    /// <summary>
    /// Cycle-free MusicBrainz/SkyHook album lookup for the Bandcamp Import List.
    /// Must not depend on <c>ISearchForNewAlbum</c> / <c>SkyHookProxy</c> (that pulls
    /// MetadataProfileService → ImportListFactory → BandcampImportList).
    /// </summary>
    public interface IBandcampMetadataAlbumSearch
    {
        BandcampMetadataAlbumMatch? FindAlbum(string albumTitle, string artistName, string? metadataSourceBaseUrl);
    }

    public sealed class BandcampMetadataAlbumMatch
    {
        public string AlbumMusicBrainzId { get; init; } = "";
        public string AlbumTitle { get; init; } = "";
        public string ArtistMusicBrainzId { get; init; } = "";
        public string ArtistName { get; init; } = "";
        public System.DateTime? ReleaseDate { get; init; }
    }
}
