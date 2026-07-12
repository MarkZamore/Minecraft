using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Minecraft;

public sealed class WorldPlayerProfileService
{
    private readonly AppPaths _paths;
    private readonly Logger? _logger;
    private readonly WorldPlayerManifestService _manifests = new();

    public WorldPlayerProfileService(AppPaths paths, Logger? logger = null)
    {
        _paths = paths;
        _logger = logger;
        ProfileFileTransaction.RecoverPending(paths, logger);
    }

    public void PrepareWorldsForLaunch(string worldsRoot, LocalIdentityContext identity)
    {
        if (!Directory.Exists(worldsRoot))
        {
            return;
        }

        var worldPaths = Directory.EnumerateDirectories(worldsRoot)
            .Where(worldPath => File.Exists(Path.Combine(worldPath, "level.dat")))
            .OrderBy(worldPath => worldPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var failures = new List<string>();
        foreach (var worldPath in worldPaths)
        {
            try
            {
                ValidateWorldForIdentityPreparation(worldPath, identity);
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(worldPath)}: {ex.Message}");
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                "Minecraft was not started because player profiles could not be prepared:\n" +
                string.Join("\n", failures));
        }

        using var transaction = ProfileFileTransaction.Begin(_paths, "Prepare worlds for launch");
        try
        {
            foreach (var worldPath in worldPaths)
            {
                PrepareWorldForIdentity(worldPath, identity, "launch", transaction);
            }
            transaction.Commit();
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Player profile preparation was rolled back: {ex.Message}");
            throw;
        }
    }

    public string? PrepareWorldForOutgoingTransfer(string worldPath, LocalIdentityContext identity)
    {
        ValidateWorldForIdentityPreparation(worldPath, identity);
        using var transaction = ProfileFileTransaction.Begin(_paths, "Prepare outgoing world transfer");
        try
        {
            var levelPath = GetLevelPath(worldPath);
            if (!File.Exists(levelPath)) return null;

            var level = NbtFile.Read(levelPath);
            var data = level.Root.GetCompound("Data");
            var player = data?.GetCompound("Player");
            var exported = player is null ? null : ExportPlayerToPlayerData(worldPath, player, transaction);
            if (player is not null && exported is null)
            {
                throw new InvalidDataException("The current host profile could not be exported; transfer was cancelled.");
            }
            if (data?.Remove("Player") == true)
            {
                WriteLevel(level, levelPath, transaction);
                _logger?.Info($"Removed level.dat Player from transfer archive source for {Path.GetFileName(worldPath)}.");
            }

            WriteManifest(worldPath, identity, transaction);
            _manifests.Validate(worldPath);
            transaction.Commit();

            return exported;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public void PrepareReceivedWorldForIdentity(string worldPath, LocalIdentityContext identity)
    {
        PrepareSingleWorldForIdentity(worldPath, identity, "received world");
    }

    public void PrepareWorldForLaunch(string worldPath, LocalIdentityContext identity)
    {
        PrepareSingleWorldForIdentity(worldPath, identity, "launch");
    }

    public void EnsureCanonicalProfileForIdentity(string worldPath, LocalIdentityContext identity)
    {
        ValidateWorldForIdentityPreparation(worldPath, identity);
        using var transaction = ProfileFileTransaction.Begin(_paths, "Migrate canonical player profile");
        var canonicalUuid = GetCanonicalIdentityUuid(identity);
        try
        {
            foreach (var legacyProfile in FindPlayerDataProfilesByFreshness(
                         worldPath,
                         GetIdentityCandidateUuids(identity).Where(uuid => uuid != canonicalUuid)))
            {
                MigrateProfileFiles(worldPath, legacyProfile.Uuid, canonicalUuid, transaction);
            }
            WriteManifest(worldPath, identity, transaction);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public void MigrateOfflineNicknameProfiles(
        string worldsRoot,
        string previousName,
        string newName,
        string portableIdentityId)
    {
        if (!Directory.Exists(worldsRoot)) return;
        var sourceUuid = CreateOfflinePlayerUuid(previousName);
        var targetUuid = CreateOfflinePlayerUuid(newName);
        if (sourceUuid == targetUuid) return;
        var worlds = Directory.EnumerateDirectories(worldsRoot)
            .Where(path => File.Exists(GetLevelPath(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var conflicts = new List<string>();
        var validationFailures = new List<string>();
        foreach (var world in worlds)
        {
            try
            {
                WorldAccessGuard.EnsureClosed(world);
                var level = NbtFile.Read(GetLevelPath(world));
                var levelPlayer = level.Root.GetCompound("Data")?.GetCompound("Player");
                if (levelPlayer?.GetUuid() == sourceUuid)
                {
                    ValidatePlayerCompound(levelPlayer, Path.GetFileName(world));
                }
                foreach (var sourcePath in EnumerateUuidFiles(world, sourceUuid))
                {
                    if (IsPrimaryPlayerDataFile(world, sourcePath, sourceUuid))
                    {
                        ValidatePlayerDataFile(sourcePath, sourceUuid);
                    }
                }
                var hasSourceProfile = EnumerateUuidFiles(world, sourceUuid).Any();
                var levelUsesSource = levelPlayer?.GetUuid() == sourceUuid;
                if ((hasSourceProfile || levelUsesSource) && EnumerateUuidFiles(world, targetUuid).Any())
                {
                    conflicts.Add(Path.GetFileName(world));
                }
            }
            catch (Exception ex)
            {
                validationFailures.Add($"{Path.GetFileName(world)}: {ex.Message}");
            }
        }
        if (validationFailures.Count > 0)
        {
            throw new InvalidOperationException(
                "Nickname profile migration could not be validated:\n" + string.Join("\n", validationFailures));
        }
        if (conflicts.Count > 0)
        {
            throw new InvalidOperationException(
                "The new nickname already has a separate profile in these worlds:\n" + string.Join("\n", conflicts));
        }

        using var transaction = ProfileFileTransaction.Begin(_paths, "Migrate offline nickname profiles");
        try
        {
            foreach (var world in worlds)
            {
                var levelPath = GetLevelPath(world);
                var level = NbtFile.Read(levelPath);
                var data = level.Root.GetCompound("Data");
                var levelPlayer = data?.GetCompound("Player");
                var hasSourceProfile = EnumerateUuidFiles(world, sourceUuid).Any();
                var levelUsesSource = levelPlayer?.GetUuid() == sourceUuid;
                if (!hasSourceProfile && !levelUsesSource) continue;

                if (levelUsesSource) ExportPlayerToPlayerData(world, levelPlayer!, transaction);
                MoveUuidFiles(world, sourceUuid, targetUuid, transaction);
                if (levelUsesSource)
                {
                    levelPlayer!.SetUuid(targetUuid);
                    WriteLevel(level, levelPath, transaction);
                }
                WriteManifest(world, new LocalIdentityContext
                {
                    IdentityId = portableIdentityId,
                    IdentityName = newName,
                    MinecraftUuid = targetUuid.ToString("D")
                }, transaction);
            }
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    [SuppressMessage("Security", "CA5351", Justification = "Minecraft's OfflinePlayer UUID protocol explicitly requires MD5.")]
    public static Guid CreateOfflinePlayerUuid(string playerName)
    {
        var normalizedName = string.IsNullOrWhiteSpace(playerName) ? "Player" : playerName.Trim();
        var seed = Encoding.UTF8.GetBytes("OfflinePlayer:" + normalizedName);
        var bytes = MD5.HashData(seed).AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return Guid.ParseExact(Convert.ToHexString(bytes).ToLowerInvariant(), "N");
    }

    private void PrepareSingleWorldForIdentity(string worldPath, LocalIdentityContext identity, string reason)
    {
        ValidateWorldForIdentityPreparation(worldPath, identity);
        using var transaction = ProfileFileTransaction.Begin(_paths, $"Prepare world for {reason}");
        try
        {
            PrepareWorldForIdentity(worldPath, identity, reason, transaction);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private void PrepareWorldForIdentity(
        string worldPath,
        LocalIdentityContext identity,
        string reason,
        ProfileFileTransaction transaction)
    {
        WorldAccessGuard.EnsureClosed(worldPath);
        var levelPath = GetLevelPath(worldPath);
        if (!File.Exists(levelPath))
        {
            return;
        }

        var canonicalUuid = GetCanonicalIdentityUuid(identity);
        var candidateUuids = GetIdentityCandidateUuids(identity);
        var level = NbtFile.Read(levelPath);
        var data = level.Root.GetCompound("Data");
        var currentPlayer = data?.GetCompound("Player");
        if (currentPlayer is not null)
        {
            ExportPlayerToPlayerData(worldPath, currentPlayer, transaction);
        }

        foreach (var legacyProfile in FindPlayerDataProfilesByFreshness(
                     worldPath,
                     candidateUuids.Where(uuid => uuid != canonicalUuid)))
        {
            MigrateProfileFiles(worldPath, legacyProfile.Uuid, canonicalUuid, transaction);
        }

        var selectedProfile = FindPlayerDataProfile(worldPath, new[] { canonicalUuid });
        if (selectedProfile is not null)
        {
            var playerData = NbtFile.Read(selectedProfile.Path);
            var player = playerData.Root.Clone();
            player.SetUuid(canonicalUuid);

            data ??= EnsureDataCompound(level);
            data.Set("Player", player);
            WriteLevel(level, levelPath, transaction);
            WriteManifest(worldPath, identity, transaction);
            _logger?.Info($"World {Path.GetFileName(worldPath)} prepared for {reason} with nickname profile {FormatUuid(canonicalUuid)}.");
            return;
        }

        if (data?.Remove("Player") == true)
        {
            WriteLevel(level, levelPath, transaction);
            _logger?.Info($"World {Path.GetFileName(worldPath)} has no matching player profile for {reason}; level.dat Player tag removed.");
        }
        WriteManifest(worldPath, identity, transaction);
    }

    public Guid? ReadLevelPlayerUuid(string worldPath)
    {
        var levelPath = GetLevelPath(worldPath);
        if (!File.Exists(levelPath))
        {
            return null;
        }

        var level = NbtFile.Read(levelPath);
        return level.Root.GetCompound("Data")?.GetCompound("Player")?.GetUuid();
    }

    public Guid? ReadPlayerDataUuid(string playerDataPath)
    {
        if (!File.Exists(playerDataPath))
        {
            return null;
        }

        return NbtFile.Read(playerDataPath).Root.GetUuid();
    }

    private string? ExportPlayerToPlayerData(
        string worldPath,
        NbtCompoundTag player,
        ProfileFileTransaction transaction)
    {
        var playerUuid = player.GetUuid();
        if (playerUuid is null)
        {
            _logger?.Warn($"level.dat Player in {Path.GetFileName(worldPath)} has no UUID; profile export skipped.");
            return null;
        }

        var playerDataDir = Path.Combine(worldPath, "playerdata");
        Directory.CreateDirectory(playerDataDir);

        var destinationPath = GetPlayerDataPath(worldPath, playerUuid.Value);
        transaction.Track(destinationPath);
        var playerCopy = player.Clone();
        playerCopy.SetUuid(playerUuid.Value);
        new NbtFile(string.Empty, playerCopy).Write(destinationPath);
        var verifiedUuid = ReadPlayerDataUuid(destinationPath);
        if (verifiedUuid != playerUuid)
        {
            throw new InvalidDataException($"Exported player profile failed UUID validation: {Path.GetFileName(destinationPath)}");
        }

        _logger?.Info($"Exported level.dat Player to playerdata/{Path.GetFileName(destinationPath)}.");
        return destinationPath;
    }

    private void ValidateWorldForIdentityPreparation(string worldPath, LocalIdentityContext identity)
    {
        WorldAccessGuard.EnsureClosed(worldPath);
        var levelPath = GetLevelPath(worldPath);
        if (!File.Exists(levelPath)) return;

        var level = NbtFile.Read(levelPath);
        var currentPlayer = level.Root.GetCompound("Data")?.GetCompound("Player");
        if (currentPlayer is not null)
        {
            ValidatePlayerCompound(currentPlayer, Path.GetFileName(worldPath));
        }

        foreach (var uuid in GetIdentityCandidateUuids(identity))
        {
            var profilePath = GetPlayerDataPath(worldPath, uuid);
            if (File.Exists(profilePath)) ValidatePlayerDataFile(profilePath, uuid);
        }

        var manifestPath = Path.Combine(worldPath, WorldPlayerManifestService.ManifestFileName);
        if (File.Exists(manifestPath)) _ = _manifests.Read(worldPath);
    }

    private static void ValidatePlayerCompound(NbtCompoundTag player, string worldName)
    {
        if (player.GetUuid() is null)
        {
            throw new InvalidDataException($"level.dat Player in {worldName} has no supported UUID tag.");
        }
    }

    private static void ValidatePlayerDataFile(string path, Guid expectedUuid)
    {
        var uuid = NbtFile.Read(path).Root.GetUuid();
        if (uuid != expectedUuid)
        {
            throw new InvalidDataException(
                $"Player profile {Path.GetFileName(path)} contains UUID {uuid?.ToString("D") ?? "missing"} instead of {expectedUuid:D}.");
        }
    }

    private void WriteLevel(NbtFile level, string levelPath, ProfileFileTransaction transaction)
    {
        transaction.Track(levelPath);
        transaction.Track(Path.Combine(Path.GetDirectoryName(levelPath)!, "level.dat_old"));
        level.Write(levelPath);
        _ = NbtFile.Read(levelPath);
    }

    private void WriteManifest(
        string worldPath,
        LocalIdentityContext identity,
        ProfileFileTransaction transaction)
    {
        transaction.Track(Path.Combine(worldPath, WorldPlayerManifestService.ManifestFileName));
        _manifests.Write(worldPath, identity);
        _manifests.Validate(worldPath);
    }

    private static NbtCompoundTag EnsureDataCompound(NbtFile level)
    {
        var data = level.Root.GetCompound("Data");
        if (data is not null)
        {
            return data;
        }

        data = new NbtCompoundTag();
        level.Root.Set("Data", data);
        return data;
    }

    private static string GetLevelPath(string worldPath) => Path.Combine(worldPath, "level.dat");

    private static string GetPlayerDataPath(string worldPath, Guid uuid)
    {
        return Path.Combine(worldPath, "playerdata", $"{FormatUuid(uuid)}.dat");
    }

    private static List<Guid> GetIdentityCandidateUuids(LocalIdentityContext identity)
    {
        var candidates = new List<Guid> { GetCanonicalIdentityUuid(identity) };

        foreach (var legacyIdentityId in identity.LegacyIdentityIds)
        {
            if (Guid.TryParse(legacyIdentityId, out var legacyUuid))
            {
                candidates.Add(legacyUuid);
            }
        }

        return candidates.Distinct().ToList();
    }

    private static Guid GetCanonicalIdentityUuid(LocalIdentityContext identity)
    {
        if (Guid.TryParse(identity.MinecraftUuid, out var minecraftUuid))
        {
            return minecraftUuid;
        }

        if (Guid.TryParse(identity.IdentityId, out var identityUuid))
        {
            return identityUuid;
        }

        return CreateOfflinePlayerUuid(identity.IdentityName);
    }

    private void MigrateProfileFiles(
        string worldPath,
        Guid sourceUuid,
        Guid targetUuid,
        ProfileFileTransaction transaction)
    {
        if (sourceUuid == targetUuid)
        {
            return;
        }

        var sourceProfilePath = GetPlayerDataPath(worldPath, sourceUuid);
        var targetProfilePath = GetPlayerDataPath(worldPath, targetUuid);
        if (!File.Exists(sourceProfilePath))
        {
            return;
        }

        var createdCanonicalProfile = false;
        if (!File.Exists(targetProfilePath))
        {
            var sourceProfile = NbtFile.Read(sourceProfilePath).Root.Clone();
            sourceProfile.SetUuid(targetUuid);
            Directory.CreateDirectory(Path.GetDirectoryName(targetProfilePath)!);
            transaction.Track(targetProfilePath);
            new NbtFile(string.Empty, sourceProfile).Write(targetProfilePath);
            createdCanonicalProfile = true;
        }

        foreach (var directoryName in new[] { "playerdata", "stats", "advancements" })
        {
            CopyUuidSidecarFiles(worldPath, directoryName, sourceUuid, targetUuid, targetProfilePath, transaction);
        }

        if (createdCanonicalProfile)
        {
            _logger?.Info($"Migrated player profile {FormatUuid(sourceUuid)} to nickname UUID {FormatUuid(targetUuid)}.");
        }
    }

    private static void CopyUuidSidecarFiles(
        string worldPath,
        string directoryName,
        Guid sourceUuid,
        Guid targetUuid,
        string canonicalProfilePath,
        ProfileFileTransaction transaction)
    {
        var directory = Path.Combine(worldPath, directoryName);
        if (!Directory.Exists(directory))
        {
            return;
        }

        var sourcePrefix = FormatUuid(sourceUuid);
        var targetPrefix = FormatUuid(targetUuid);
        foreach (var sourcePath in Directory.EnumerateFiles(directory, sourcePrefix + ".*", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(sourcePath, GetPlayerDataPath(worldPath, sourceUuid), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sourceName = Path.GetFileName(sourcePath);
            var suffix = sourceName[sourcePrefix.Length..];
            var targetPath = Path.Combine(directory, targetPrefix + suffix);
            if (string.Equals(targetPath, canonicalProfilePath, StringComparison.OrdinalIgnoreCase) || File.Exists(targetPath))
            {
                continue;
            }

            transaction.Track(targetPath);
            File.Copy(sourcePath, targetPath, overwrite: false);
        }
    }

    private static bool IsPrimaryPlayerDataFile(string worldPath, string path, Guid uuid)
    {
        return string.Equals(path, GetPlayerDataPath(worldPath, uuid), StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateUuidFiles(string worldPath, Guid uuid)
    {
        var prefix = FormatUuid(uuid);
        foreach (var directoryName in new[] { "playerdata", "stats", "advancements" })
        {
            var directory = Path.Combine(worldPath, directoryName);
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, prefix + ".*", SearchOption.TopDirectoryOnly))
            {
                yield return path;
            }
        }
    }

    private static void MoveUuidFiles(
        string worldPath,
        Guid sourceUuid,
        Guid targetUuid,
        ProfileFileTransaction transaction)
    {
        var sourcePrefix = FormatUuid(sourceUuid);
        var targetPrefix = FormatUuid(targetUuid);
        var createdTargets = new List<string>();
        try
        {
            foreach (var sourcePath in EnumerateUuidFiles(worldPath, sourceUuid).ToArray())
            {
                var sourceName = Path.GetFileName(sourcePath);
                var targetPath = Path.Combine(Path.GetDirectoryName(sourcePath)!, targetPrefix + sourceName[sourcePrefix.Length..]);
                if (File.Exists(targetPath)) throw new IOException($"Target profile file already exists: {targetPath}");
                transaction.Track(sourcePath);
                transaction.Track(targetPath);
                if (sourcePath.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Path.GetDirectoryName(sourcePath), Path.Combine(worldPath, "playerdata"), StringComparison.OrdinalIgnoreCase))
                {
                    var profile = NbtFile.Read(sourcePath);
                    profile.Root.SetUuid(targetUuid);
                    profile.Write(targetPath);
                }
                else
                {
                    File.Copy(sourcePath, targetPath, overwrite: false);
                }
                createdTargets.Add(targetPath);
            }
            foreach (var sourcePath in EnumerateUuidFiles(worldPath, sourceUuid).ToArray()) File.Delete(sourcePath);
        }
        catch
        {
            foreach (var target in createdTargets)
            {
                if (File.Exists(target)) File.Delete(target);
            }
            throw;
        }
    }

    private static PlayerDataProfile? FindPlayerDataProfile(string worldPath, IEnumerable<Guid> candidateUuids)
    {
        foreach (var uuid in candidateUuids)
        {
            var path = GetPlayerDataPath(worldPath, uuid);
            if (File.Exists(path))
            {
                return new PlayerDataProfile(uuid, path);
            }
        }

        return null;
    }

    private static IEnumerable<PlayerDataProfile> FindPlayerDataProfilesByFreshness(string worldPath, IEnumerable<Guid> candidateUuids)
    {
        return candidateUuids
            .Select(uuid => new PlayerDataProfile(uuid, GetPlayerDataPath(worldPath, uuid)))
            .Where(profile => File.Exists(profile.Path))
            .OrderByDescending(profile => File.GetLastWriteTimeUtc(profile.Path));
    }

    private static string FormatUuid(Guid uuid) => uuid.ToString("D").ToLowerInvariant();

    public WorldPlayersManifest ValidatePlayerManifest(string worldPath) => _manifests.Validate(worldPath);

    public string GetPlayerManifestHash(string worldPath) => _manifests.HashManifest(worldPath);

    private sealed record PlayerDataProfile(Guid Uuid, string Path);
}

internal sealed class NbtFile
{
    private const byte CompoundType = 10;

    public NbtFile(string rootName, NbtCompoundTag root)
    {
        RootName = rootName;
        Root = root;
    }

    public string RootName { get; }
    public NbtCompoundTag Root { get; }

    public static NbtFile Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
        {
            using var input = new MemoryStream(bytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            bytes = output.ToArray();
        }

        var reader = new NbtReader(bytes);
        var type = reader.ReadByte();
        if (type != CompoundType)
        {
            throw new InvalidDataException("NBT root tag is not a compound.");
        }

        var rootName = reader.ReadString();
        var root = NbtCompoundTag.ReadPayload(reader);
        return new NbtFile(rootName, root);
    }

    public void Write(string path)
    {
        using var raw = new MemoryStream();
        var writer = new NbtWriter(raw);
        writer.WriteByte(CompoundType);
        writer.WriteString(RootName);
        Root.WritePayload(writer);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
                {
                    raw.Position = 0;
                    raw.CopyTo(gzip);
                }
                output.Flush(flushToDisk: true);
            }

            _ = Read(temporaryPath);
            if (File.Exists(fullPath))
            {
                var backupPath = string.Equals(
                    Path.GetFileName(fullPath),
                    "level.dat",
                    StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(directory, "level.dat_old")
                    : null;
                File.Replace(temporaryPath, fullPath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }
            _ = Read(fullPath);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}

internal abstract class NbtTag
{
    protected NbtTag(byte type)
    {
        Type = type;
    }

    public byte Type { get; }
    public abstract NbtTag CloneTag();
    public abstract void WritePayload(NbtWriter writer);

    public static NbtTag ReadPayload(NbtReader reader, byte type)
    {
        return type switch
        {
            1 => new NbtByteTag(reader.ReadByte()),
            2 => new NbtShortTag(reader.ReadInt16()),
            3 => new NbtIntTag(reader.ReadInt32()),
            4 => new NbtLongTag(reader.ReadInt64()),
            5 => new NbtFloatTag(reader.ReadSingle()),
            6 => new NbtDoubleTag(reader.ReadDouble()),
            7 => new NbtByteArrayTag(reader.ReadByteArray()),
            8 => new NbtStringTag(reader.ReadString()),
            9 => NbtListTag.ReadPayload(reader),
            10 => NbtCompoundTag.ReadPayload(reader),
            11 => new NbtIntArrayTag(reader.ReadIntArray()),
            12 => new NbtLongArrayTag(reader.ReadLongArray()),
            _ => throw new InvalidDataException($"Unsupported NBT tag type {type}.")
        };
    }
}

internal sealed class NbtByteTag : NbtTag
{
    public NbtByteTag(byte value) : base(1) => Value = value;
    public byte Value { get; }
    public override NbtTag CloneTag() => new NbtByteTag(Value);
    public override void WritePayload(NbtWriter writer) => writer.WriteByte(Value);
}

internal sealed class NbtShortTag : NbtTag
{
    public NbtShortTag(short value) : base(2) => Value = value;
    public short Value { get; }
    public override NbtTag CloneTag() => new NbtShortTag(Value);
    public override void WritePayload(NbtWriter writer) => writer.WriteInt16(Value);
}

internal sealed class NbtIntTag : NbtTag
{
    public NbtIntTag(int value) : base(3) => Value = value;
    public int Value { get; }
    public override NbtTag CloneTag() => new NbtIntTag(Value);
    public override void WritePayload(NbtWriter writer) => writer.WriteInt32(Value);
}

internal sealed class NbtLongTag : NbtTag
{
    public NbtLongTag(long value) : base(4) => Value = value;
    public long Value { get; }
    public override NbtTag CloneTag() => new NbtLongTag(Value);
    public override void WritePayload(NbtWriter writer) => writer.WriteInt64(Value);
}

internal sealed class NbtFloatTag : NbtTag
{
    public NbtFloatTag(float value) : base(5) => Value = value;
    public float Value { get; }
    public override NbtTag CloneTag() => new NbtFloatTag(Value);
    public override void WritePayload(NbtWriter writer) => writer.WriteSingle(Value);
}

internal sealed class NbtDoubleTag : NbtTag
{
    public NbtDoubleTag(double value) : base(6) => Value = value;
    public double Value { get; }
    public override NbtTag CloneTag() => new NbtDoubleTag(Value);
    public override void WritePayload(NbtWriter writer) => writer.WriteDouble(Value);
}

internal sealed class NbtByteArrayTag : NbtTag
{
    public NbtByteArrayTag(byte[] value) : base(7) => Value = value;
    public byte[] Value { get; }
    public override NbtTag CloneTag() => new NbtByteArrayTag(Value.ToArray());
    public override void WritePayload(NbtWriter writer) => writer.WriteByteArray(Value);
}

internal sealed class NbtStringTag : NbtTag
{
    public NbtStringTag(string value) : base(8) => Value = value;
    public string Value { get; }
    public override NbtTag CloneTag() => new NbtStringTag(Value);
    public override void WritePayload(NbtWriter writer) => writer.WriteString(Value);
}

internal sealed class NbtListTag : NbtTag
{
    public NbtListTag(byte elementType, IEnumerable<NbtTag>? items = null) : base(9)
    {
        ElementType = elementType;
        Items = items?.ToList() ?? new List<NbtTag>();
    }

    public byte ElementType { get; }
    public List<NbtTag> Items { get; }

    public static NbtListTag ReadPayload(NbtReader reader)
    {
        var elementType = reader.ReadByte();
        var count = reader.ReadInt32();
        if (count < 0)
        {
            throw new InvalidDataException("NBT list length is negative.");
        }

        var items = new List<NbtTag>(count);
        for (var i = 0; i < count; i++)
        {
            items.Add(ReadPayload(reader, elementType));
        }

        return new NbtListTag(elementType, items);
    }

    public override NbtTag CloneTag()
    {
        return new NbtListTag(ElementType, Items.Select(item => item.CloneTag()));
    }

    public override void WritePayload(NbtWriter writer)
    {
        writer.WriteByte(ElementType);
        writer.WriteInt32(Items.Count);
        foreach (var item in Items)
        {
            if (item.Type != ElementType)
            {
                throw new InvalidDataException("NBT list contains an item with a mismatched type.");
            }

            item.WritePayload(writer);
        }
    }
}

internal sealed class NbtCompoundTag : NbtTag
{
    private readonly List<NbtNamedTag> _tags = new();

    public NbtCompoundTag() : base(10)
    {
    }

    public IEnumerable<NbtNamedTag> Tags => _tags;

    public static NbtCompoundTag ReadPayload(NbtReader reader)
    {
        var compound = new NbtCompoundTag();
        while (true)
        {
            var type = reader.ReadByte();
            if (type == 0)
            {
                return compound;
            }

            var name = reader.ReadString();
            compound._tags.Add(new NbtNamedTag(name, ReadPayload(reader, type)));
        }
    }

    public NbtCompoundTag Clone()
    {
        var clone = new NbtCompoundTag();
        foreach (var tag in _tags)
        {
            clone._tags.Add(new NbtNamedTag(tag.Name, tag.Tag.CloneTag()));
        }

        return clone;
    }

    public override NbtTag CloneTag() => Clone();

    public NbtCompoundTag? GetCompound(string name)
    {
        return _tags.FirstOrDefault(tag => string.Equals(tag.Name, name, StringComparison.Ordinal))?.Tag as NbtCompoundTag;
    }

    public Guid? GetUuid()
    {
        var uuidTag = _tags.FirstOrDefault(tag => string.Equals(tag.Name, "UUID", StringComparison.Ordinal))?.Tag as NbtIntArrayTag;
        var uuid = uuidTag?.ToUuid();
        if (uuid is not null) return uuid;
        var most = _tags.FirstOrDefault(tag => string.Equals(tag.Name, "UUIDMost", StringComparison.Ordinal))?.Tag as NbtLongTag;
        var least = _tags.FirstOrDefault(tag => string.Equals(tag.Name, "UUIDLeast", StringComparison.Ordinal))?.Tag as NbtLongTag;
        if (most is null || least is null) return null;
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt64BigEndian(bytes[..8], most.Value);
        BinaryPrimitives.WriteInt64BigEndian(bytes[8..], least.Value);
        return Guid.ParseExact(Convert.ToHexString(bytes), "N");
    }

    public void SetUuid(Guid uuid)
    {
        var usesLegacyLongs = _tags.Any(tag => string.Equals(tag.Name, "UUIDMost", StringComparison.Ordinal)) &&
                              _tags.Any(tag => string.Equals(tag.Name, "UUIDLeast", StringComparison.Ordinal));
        if (!usesLegacyLongs)
        {
            Set("UUID", NbtIntArrayTag.FromUuid(uuid));
            return;
        }

        Span<byte> bytes = stackalloc byte[16];
        var hex = uuid.ToString("N");
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = Convert.ToByte(hex.Substring(index * 2, 2), 16);
        }
        Set("UUIDMost", new NbtLongTag(BinaryPrimitives.ReadInt64BigEndian(bytes[..8])));
        Set("UUIDLeast", new NbtLongTag(BinaryPrimitives.ReadInt64BigEndian(bytes[8..])));
    }

    public bool Remove(string name)
    {
        var removed = false;
        for (var index = _tags.Count - 1; index >= 0; index--)
        {
            if (!string.Equals(_tags[index].Name, name, StringComparison.Ordinal))
            {
                continue;
            }

            _tags.RemoveAt(index);
            removed = true;
        }

        return removed;
    }

    public void Set(string name, NbtTag tag)
    {
        for (var index = 0; index < _tags.Count; index++)
        {
            if (string.Equals(_tags[index].Name, name, StringComparison.Ordinal))
            {
                _tags[index] = new NbtNamedTag(name, tag);
                return;
            }
        }

        _tags.Add(new NbtNamedTag(name, tag));
    }

    public override void WritePayload(NbtWriter writer)
    {
        foreach (var namedTag in _tags)
        {
            writer.WriteByte(namedTag.Tag.Type);
            writer.WriteString(namedTag.Name);
            namedTag.Tag.WritePayload(writer);
        }

        writer.WriteByte(0);
    }
}

internal sealed record NbtNamedTag(string Name, NbtTag Tag);

internal sealed class NbtIntArrayTag : NbtTag
{
    public NbtIntArrayTag(int[] value) : base(11) => Value = value;
    public int[] Value { get; }

    public static NbtIntArrayTag FromUuid(Guid uuid)
    {
        var hex = uuid.ToString("N");
        Span<byte> bytes = stackalloc byte[16];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        var values = new int[4];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(i * 4, 4));
        }

        return new NbtIntArrayTag(values);
    }

    public Guid? ToUuid()
    {
        if (Value.Length != 4)
        {
            return null;
        }

        Span<byte> bytes = stackalloc byte[16];
        for (var i = 0; i < Value.Length; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(bytes.Slice(i * 4, 4), Value[i]);
        }

        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return Guid.ParseExact(hex, "N");
    }

    public override NbtTag CloneTag() => new NbtIntArrayTag(Value.ToArray());
    public override void WritePayload(NbtWriter writer) => writer.WriteIntArray(Value);
}

internal sealed class NbtLongArrayTag : NbtTag
{
    public NbtLongArrayTag(long[] value) : base(12) => Value = value;
    public long[] Value { get; }
    public override NbtTag CloneTag() => new NbtLongArrayTag(Value.ToArray());
    public override void WritePayload(NbtWriter writer) => writer.WriteLongArray(Value);
}

internal sealed class NbtReader
{
    private readonly byte[] _bytes;
    private int _position;

    public NbtReader(byte[] bytes)
    {
        _bytes = bytes;
    }

    public byte ReadByte()
    {
        EnsureAvailable(1);
        return _bytes[_position++];
    }

    public short ReadInt16()
    {
        EnsureAvailable(2);
        var value = BinaryPrimitives.ReadInt16BigEndian(_bytes.AsSpan(_position, 2));
        _position += 2;
        return value;
    }

    public int ReadInt32()
    {
        EnsureAvailable(4);
        var value = BinaryPrimitives.ReadInt32BigEndian(_bytes.AsSpan(_position, 4));
        _position += 4;
        return value;
    }

    public long ReadInt64()
    {
        EnsureAvailable(8);
        var value = BinaryPrimitives.ReadInt64BigEndian(_bytes.AsSpan(_position, 8));
        _position += 8;
        return value;
    }

    public float ReadSingle()
    {
        var value = BitConverter.Int32BitsToSingle(ReadInt32());
        return value;
    }

    public double ReadDouble()
    {
        var value = BitConverter.Int64BitsToDouble(ReadInt64());
        return value;
    }

    public string ReadString()
    {
        var length = (ushort)ReadInt16();
        EnsureAvailable(length);
        var value = Encoding.UTF8.GetString(_bytes, _position, length);
        _position += length;
        return value;
    }

    public byte[] ReadByteArray()
    {
        var length = ReadInt32();
        if (length < 0)
        {
            throw new InvalidDataException("NBT byte array length is negative.");
        }

        EnsureAvailable(length);
        var value = _bytes.AsSpan(_position, length).ToArray();
        _position += length;
        return value;
    }

    public int[] ReadIntArray()
    {
        var length = ReadInt32();
        if (length < 0)
        {
            throw new InvalidDataException("NBT int array length is negative.");
        }

        var value = new int[length];
        for (var i = 0; i < length; i++)
        {
            value[i] = ReadInt32();
        }

        return value;
    }

    public long[] ReadLongArray()
    {
        var length = ReadInt32();
        if (length < 0)
        {
            throw new InvalidDataException("NBT long array length is negative.");
        }

        var value = new long[length];
        for (var i = 0; i < length; i++)
        {
            value[i] = ReadInt64();
        }

        return value;
    }

    private void EnsureAvailable(int count)
    {
        if (_position + count > _bytes.Length)
        {
            throw new EndOfStreamException("Unexpected end of NBT data.");
        }
    }
}

internal sealed class NbtWriter
{
    private readonly Stream _stream;

    public NbtWriter(Stream stream)
    {
        _stream = stream;
    }

    public void WriteByte(byte value)
    {
        _stream.WriteByte(value);
    }

    public void WriteInt16(short value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(bytes, value);
        _stream.Write(bytes);
    }

    public void WriteInt32(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        _stream.Write(bytes);
    }

    public void WriteInt64(long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        _stream.Write(bytes);
    }

    public void WriteSingle(float value)
    {
        WriteInt32(BitConverter.SingleToInt32Bits(value));
    }

    public void WriteDouble(double value)
    {
        WriteInt64(BitConverter.DoubleToInt64Bits(value));
    }

    public void WriteString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > ushort.MaxValue)
        {
            throw new InvalidDataException("NBT string is too long.");
        }

        WriteInt16(unchecked((short)bytes.Length));
        _stream.Write(bytes);
    }

    public void WriteByteArray(byte[] value)
    {
        WriteInt32(value.Length);
        _stream.Write(value);
    }

    public void WriteIntArray(int[] value)
    {
        WriteInt32(value.Length);
        foreach (var item in value)
        {
            WriteInt32(item);
        }
    }

    public void WriteLongArray(long[] value)
    {
        WriteInt32(value.Length);
        foreach (var item in value)
        {
            WriteInt64(item);
        }
    }
}
