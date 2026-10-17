namespace CryptinSDK.Compat;

/// <summary>
/// Finds tests/fixtures/compat/.
///
/// There are two layouts and both have to work. Inside Krypt the fixtures are
/// two levels up, in the repository that can regenerate them; inside
/// lake8dev/cryptin-sdk they are a synchronised copy under this project. So the
/// search prefers the local copy and then walks up, which resolves either layout
/// without a flag — and, while both exist, uses the copy the SHA-256 check keeps
/// honest.
/// </summary>
internal static class FixtureLocator
{
    private const string EnvVar = "CRYPTIN_COMPAT_FIXTURES";
    private const string ManifestName = "manifest.json";

    /// <summary>
    /// In order: the command-line argument, then <c>CRYPTIN_COMPAT_FIXTURES</c>,
    /// then the nearest <c>tests/fixtures/compat/</c> at or above this
    /// executable. The nearest one is the local copy when there is one, which is
    /// what makes the split layout work with no configuration.
    /// </summary>
    public static DirectoryInfo Locate(string[] args)
    {
        if (args.Length > 0)
            return Verify(args[0], "argomento da riga di comando");

        var fromEnv = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return Verify(fromEnv, $"variabile d'ambiente {EnvVar}");

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "fixtures", "compat");
            if (File.Exists(Path.Combine(candidate, ManifestName)))
                return new DirectoryInfo(candidate);
        }

        throw new DirectoryNotFoundException(
            $"Non trovo tests/fixtures/compat/ risalendo da {AppContext.BaseDirectory}. " +
            $"Passa il percorso come argomento, o mettilo in {EnvVar}.");
    }

    private static DirectoryInfo Verify(string path, string source)
    {
        var dir = new DirectoryInfo(path);
        if (!File.Exists(Path.Combine(dir.FullName, ManifestName)))
            throw new DirectoryNotFoundException(
                $"{source}: {dir.FullName} non contiene {ManifestName}.");
        return dir;
    }
}
