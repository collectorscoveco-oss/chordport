using ChordPort.Api.Domain;
namespace ChordPort.Api.Services;
public sealed class SettingsService
{
    private static readonly HashSet<string> AllowedModes = ["Manual", "Review before import"];
    private Settings _current = new("Music", "Manual");
    public Settings Get() => _current;
    public ValidationResult Update(Settings next)
    {
        if (string.IsNullOrWhiteSpace(next.LibraryRoot)) return new(false, "Library root is required.");
        if (!AllowedModes.Contains(next.ImportMode)) return new(false, "Import mode is not supported.");
        _current = next with { LibraryRoot = next.LibraryRoot.Trim() }; return new(true, null);
    }
}
