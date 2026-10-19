using ChordPort.Api.Domain;
namespace ChordPort.Api.Services;

public interface ICatalogAdapter { IReadOnlyList<Song> Search(SearchMode mode, string? query); }
public sealed class CatalogService : ICatalogAdapter
{
    private static readonly Song[] Catalog = [
        new("song-1", "Midnight Signals", "The Northline", "Night Drive", 233),
        new("song-2", "Paper Planes", "Rae Sol", "Small Hours", 201),
        new("song-3", "After the Rain", "Cedar Echo", "Weathered", 248),
        new("song-4", "Daylight Static", "The Northline", "Night Drive", 217)
    ];
    public IReadOnlyList<Song> Search(SearchMode mode, string? query)
    {
        var term = (query ?? string.Empty).Trim();
        if (term.Length == 0) return Catalog;
        return Catalog.Where(song => (mode switch {
            SearchMode.Songs => song.Title, SearchMode.Artists => song.Artist, SearchMode.Albums => song.Album, _ => string.Empty
        }).Contains(term, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    public Song? Find(string id) => Catalog.FirstOrDefault(song => song.Id == id);
}
