using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Minecraft;

public static class PackManifestService
{
    public const int CurrentSchemaVersion = 1;
    public const string ManifestFileName = "portable-pack.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool HasManifest(string packDirectory) =>
        Directory.Exists(packDirectory) && File.Exists(Path.Combine(packDirectory, ManifestFileName));

    public static PackRuntimeDescriptor Load(string packDirectory)
    {
        var packRoot = Path.GetFullPath(packDirectory);
        var manifestPath = Path.Combine(packRoot, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                $"Pack manifest is missing. Expected: {manifestPath}",
                manifestPath);
        }

        PackManifestDto manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PackManifestDto>(File.ReadAllText(manifestPath), JsonOptions)
                ?? throw new InvalidDataException("Pack manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Pack manifest is not valid JSON: {manifestPath}", ex);
        }

        if (manifest.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported pack manifest schemaVersion {manifest.SchemaVersion}; expected {CurrentSchemaVersion}.");
        }

        var minecraftVersion = ValidateIdentifier(manifest.MinecraftVersion, "minecraftVersion", allowSpaces: false);
        if (string.Equals(minecraftVersion, "latest", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("minecraftVersion must be an exact version, not latest.");
        }
        var loaderName = manifest.Loader?.Type?.Trim().ToLowerInvariant() ?? "";
        var loaderKind = loaderName switch
        {
            "vanilla" => PackLoaderKind.Vanilla,
            "forge" => PackLoaderKind.Forge,
            "neoforge" => PackLoaderKind.NeoForge,
            "fabric" => PackLoaderKind.Fabric,
            "quilt" => PackLoaderKind.Quilt,
            _ => throw new InvalidDataException(
                "loader.type must be one of: vanilla, forge, neoforge, fabric, quilt.")
        };

        string? loaderVersion = null;
        if (loaderKind == PackLoaderKind.Vanilla)
        {
            if (!string.IsNullOrWhiteSpace(manifest.Loader?.Version))
            {
                throw new InvalidDataException("loader.version must be omitted for a vanilla pack.");
            }
        }
        else
        {
            loaderVersion = ValidateIdentifier(manifest.Loader?.Version, "loader.version", allowSpaces: false);
            if (string.Equals(loaderVersion, "latest", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("loader.version must be an exact version, not latest.");
            }
        }

        var clientJar = ValidateClientJarName(manifest.ClientJar);
        var canonical = $"{CurrentSchemaVersion}\n{minecraftVersion}\n{loaderKind.ToString().ToLowerInvariant()}\n{loaderVersion}\n{clientJar}";
        var descriptorHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new PackRuntimeDescriptor(
            CurrentSchemaVersion,
            minecraftVersion,
            new PackLoaderDescriptor(loaderKind, loaderVersion),
            clientJar,
            descriptorHash);
    }

    public static string ResolveClientJarPath(string packDirectory, PackRuntimeDescriptor descriptor)
    {
        var packRoot = Path.GetFullPath(packDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(packRoot, descriptor.ClientJar));
        var parent = Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(parent, packRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("clientJar must point to a file directly in the pack root.");
        }

        return path;
    }

    private static string ValidateClientJarName(string? value)
    {
        var name = value?.Trim() ?? "";
        if (name.Length == 0 ||
            !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("clientJar must be the name of one .jar file in the pack root.");
        }

        return name;
    }

    private static string ValidateIdentifier(string? value, string propertyName, bool allowSpaces)
    {
        var result = value?.Trim() ?? "";
        if (result.Length is < 1 or > 128 ||
            result is "." or ".." ||
            result.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            result.Contains('/') ||
            result.Contains('\\') ||
            result.Any(char.IsControl) ||
            (!allowSpaces && result.Any(char.IsWhiteSpace)))
        {
            throw new InvalidDataException($"{propertyName} is invalid.");
        }

        return result;
    }

    private sealed class PackManifestDto
    {
        public int SchemaVersion { get; set; }
        public string? MinecraftVersion { get; set; }
        public LoaderManifestDto? Loader { get; set; }
        public string? ClientJar { get; set; }
    }

    private sealed class LoaderManifestDto
    {
        public string? Type { get; set; }
        public string? Version { get; set; }
    }
}
