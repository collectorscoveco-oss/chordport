namespace ChordPort.Api.Services;

public static class RuntimeDataDirectory
{
    public static string Resolve(string? configuredDirectory, string localApplicationData, bool isWindows, string applicationBaseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory)) return configuredDirectory;
        return isWindows ? Path.Combine(localApplicationData, "ChordPort") : applicationBaseDirectory;
    }

    public static string ResolveDefault(string applicationBaseDirectory)
    {
        return Resolve(
            Environment.GetEnvironmentVariable("CHORDPORT_DATA_DIR"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            OperatingSystem.IsWindows(),
            applicationBaseDirectory);
    }
}
