using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Minecraft;

internal sealed class IdentityAdapterMappingService
{
    private const string LoginListener = "net/minecraft/server/network/ServerLoginPacketListenerImpl";
    private const string HelloPacket = "net/minecraft/network/protocol/login/ServerboundHelloPacket";
    private const string MinecraftServer = "net/minecraft/server/MinecraftServer";
    private const string Connection = "net/minecraft/network/Connection";
    private const string PlayerList = "net/minecraft/server/players/PlayerList";
    private const string Component = "net/minecraft/network/chat/Component";
    private const string PlayerInfo = "net/minecraft/client/multiplayer/PlayerInfo";
    private const string PlayerSkin = "net/minecraft/client/resources/PlayerSkin";

    public IdentityAdapterConfiguration Build(PreparedRuntime runtime)
    {
        var mappingPath = FindTsrg2Mappings(runtime.RuntimeRoot);
        if (mappingPath is null)
        {
            throw Unsupported(runtime.Descriptor, "runtime mappings were not found");
        }

        var mappings = Tsrg2Mappings.Read(mappingPath);
        var listener = mappings.RequireClass(LoginListener);
        var packet = mappings.RequireClass(HelloPacket);
        var server = mappings.RequireClass(MinecraftServer);
        var connection = mappings.RequireClass(Connection);
        var playerList = mappings.RequireClass(PlayerList);
        var component = mappings.RequireClass(Component);
        var playerInfo = mappings.RequireClass(PlayerInfo);
        var playerSkin = mappings.RequireClass(PlayerSkin);

        var hello = listener.RequireMethod("handleHello", descriptor => descriptor.Contains($"L{packet.LeftName};", StringComparison.Ordinal));
        var verify = listener.RequireMethod("verifyLoginAndFinishConnectionSetup", descriptor => descriptor.Contains("Lcom/mojang/authlib/GameProfile;", StringComparison.Ordinal));
        var skinLookup = playerInfo.RequireMethod(
            "createSkinLookup",
            descriptor => descriptor.StartsWith("(Lcom/mojang/authlib/GameProfile;)", StringComparison.Ordinal));
        var skinSelection = playerInfo.RequireMethod(
            "lambda$createSkinLookup$2",
            descriptor => descriptor.StartsWith("(Ljava/util/concurrent/CompletableFuture;", StringComparison.Ordinal));
        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["loginClasses"] = JoinAliases(LoginListener, listener.LeftName),
            ["packetClasses"] = JoinAliases(HelloPacket, packet.LeftName),
            ["serverClasses"] = JoinAliases(MinecraftServer, server.LeftName),
            ["connectionClasses"] = JoinAliases(Connection, connection.LeftName),
            ["playerListClasses"] = JoinAliases(PlayerList, playerList.LeftName),
            ["helloMethods"] = JoinAliases("handleHello", hello.LeftName),
            ["helloDescriptors"] = JoinAliases(
                "(Lnet/minecraft/network/protocol/login/ServerboundHelloPacket;)V",
                hello.LeftDescriptor),
            ["verifyMethods"] = JoinAliases("verifyLoginAndFinishConnectionSetup", verify.LeftName),
            ["verifyDescriptors"] = JoinAliases("(Lcom/mojang/authlib/GameProfile;)V", verify.LeftDescriptor),
            ["serverFields"] = JoinAliases("server", listener.RequireField("server")),
            ["connectionFields"] = JoinAliases("connection", listener.RequireField("connection")),
            ["requestedUsernameFields"] = JoinAliases("requestedUsername", listener.RequireField("requestedUsername")),
            ["packetNameMethods"] = JoinAliases("name", packet.RequireMethod("name", descriptor => descriptor == "()Ljava/lang/String;").LeftName),
            ["packetUuidMethods"] = JoinAliases("profileId", packet.RequireMethod("profileId", descriptor => descriptor == "()Ljava/util/UUID;").LeftName),
            ["memoryConnectionMethods"] = JoinAliases("isMemoryConnection", connection.RequireMethod("isMemoryConnection", descriptor => descriptor == "()Z").LeftName),
            ["startVerificationMethods"] = JoinAliases("startClientVerification", listener.RequireMethod("startClientVerification", descriptor => descriptor.Contains("Lcom/mojang/authlib/GameProfile;", StringComparison.Ordinal)).LeftName),
            ["playerListMethods"] = JoinAliases("getPlayerList", server.RequireMethod("getPlayerList", descriptor => descriptor.StartsWith("()L", StringComparison.Ordinal)).LeftName),
            ["getPlayerMethods"] = JoinAliases("getPlayer", playerList.RequireMethod("getPlayer", descriptor => descriptor.StartsWith("(Ljava/util/UUID;)", StringComparison.Ordinal)).LeftName),
            ["componentClasses"] = JoinAliases(Component.Replace('/', '.'), component.LeftName.Replace('/', '.')),
            ["componentLiteralMethods"] = JoinAliases("literal", component.RequireMethod("literal", descriptor => descriptor.StartsWith("(Ljava/lang/String;)", StringComparison.Ordinal)).LeftName),
            ["disconnectMethods"] = JoinAliases("disconnect", listener.RequireMethod("disconnect", descriptor => descriptor.EndsWith(")V", StringComparison.Ordinal)).LeftName),
            ["playerInfoClasses"] = JoinAliases(PlayerInfo, playerInfo.LeftName),
            ["skinLookupMethods"] = JoinAliases("createSkinLookup", skinLookup.LeftName),
            ["skinLookupDescriptors"] = JoinAliases(
                "(Lcom/mojang/authlib/GameProfile;)Ljava/util/function/Supplier;",
                skinLookup.LeftDescriptor),
            ["skinSelectionMethods"] = JoinAliases("lambda$createSkinLookup$2", skinSelection.LeftName),
            ["skinSelectionDescriptors"] = JoinAliases(
                "(Ljava/util/concurrent/CompletableFuture;Lnet/minecraft/client/resources/PlayerSkin;Z)Lnet/minecraft/client/resources/PlayerSkin;",
                skinSelection.LeftDescriptor),
            ["skinTextureUrlMethods"] = JoinAliases(
                "textureUrl",
                playerSkin.RequireMethod("textureUrl", descriptor => descriptor == "()Ljava/lang/String;").LeftName),
            ["skinSecureMethods"] = JoinAliases(
                "secure",
                playerSkin.RequireMethod("secure", descriptor => descriptor == "()Z").LeftName)
        };

        var requiredTargets = new HashSet<string>(StringComparer.Ordinal)
        {
            LoginListener,
            listener.LeftName,
            PlayerInfo,
            playerInfo.LeftName
        };
        var targets = FindRuntimeTargets(runtime, requiredTargets);
        if (targets.Count != requiredTargets.Count)
        {
            var found = targets.Select(target => target.ClassName).ToHashSet(StringComparer.Ordinal);
            var missing = string.Join(", ", requiredTargets.Where(target => !found.Contains(target)));
            throw Unsupported(runtime.Descriptor, $"required runtime classes are absent: {missing}");
        }

        return new IdentityAdapterConfiguration(mappingPath, properties, targets);
    }

    private static string? FindTsrg2Mappings(string runtimeRoot)
    {
        var libraries = Path.Combine(runtimeRoot, "libraries");
        if (!Directory.Exists(libraries)) return null;
        foreach (var path in Directory.EnumerateFiles(libraries, "*mappings*.txt", SearchOption.AllDirectories)
                     .OrderByDescending(path => Path.GetFileName(path).Contains("merged", StringComparison.OrdinalIgnoreCase))
                     .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var reader = new StreamReader(path);
                if (reader.ReadLine()?.StartsWith("tsrg2 ", StringComparison.Ordinal) == true) return path;
            }
            catch (IOException)
            {
            }
        }
        return null;
    }

    private static List<IdentityAdapterTarget> FindRuntimeTargets(
        PreparedRuntime runtime,
        IReadOnlySet<string> requiredTargets)
    {
        var wanted = new HashSet<string>(requiredTargets, StringComparer.Ordinal);
        var candidates = new List<string>();
        var minecraftLibraries = Path.Combine(runtime.RuntimeRoot, "libraries", "net", "minecraft", "client");
        if (Directory.Exists(minecraftLibraries))
        {
            candidates.AddRange(Directory.EnumerateFiles(minecraftLibraries, "*-srg.jar", SearchOption.AllDirectories));
        }
        candidates.Add(runtime.ClientJarPath);
        var libraries = Path.Combine(runtime.RuntimeRoot, "libraries");
        if (Directory.Exists(libraries))
        {
            candidates.AddRange(Directory.EnumerateFiles(libraries, "*.jar", SearchOption.AllDirectories));
        }

        var found = new List<IdentityAdapterTarget>();
        var inspected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in candidates)
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath) || !inspected.Add(fullPath)) continue;
            try
            {
                using var archive = ZipFile.OpenRead(fullPath);
                foreach (var className in wanted.ToArray())
                {
                    if (archive.GetEntry(className + ".class") is null) continue;
                    found.Add(new IdentityAdapterTarget(fullPath, className));
                    wanted.Remove(className);
                }
            }
            catch (InvalidDataException)
            {
            }
            catch (IOException)
            {
            }
            if (wanted.Count == 0) break;
        }
        return found;
    }

    private static string JoinAliases(params string[] aliases) => string.Join(",", aliases
        .Where(alias => !string.IsNullOrWhiteSpace(alias))
        .Distinct(StringComparer.Ordinal));

    private static NotSupportedException Unsupported(PackRuntimeDescriptor descriptor, string reason) => new(
        $"Portable UUID adapter could not be verified for Minecraft {descriptor.MinecraftVersion} " +
        $"{descriptor.Loader.Type} {descriptor.Loader.Version}: {reason}. Update Minecraft.exe before launching this pack.");

    private sealed class Tsrg2Mappings
    {
        private readonly Dictionary<string, Tsrg2Class> _classes;

        private Tsrg2Mappings(Dictionary<string, Tsrg2Class> classes)
        {
            _classes = classes;
        }

        public static Tsrg2Mappings Read(string path)
        {
            var requiredClasses = new HashSet<string>(StringComparer.Ordinal)
            {
                LoginListener,
                HelloPacket,
                MinecraftServer,
                Connection,
                PlayerList,
                Component,
                PlayerInfo,
                PlayerSkin
            };
            var classes = new Dictionary<string, Tsrg2Class>(StringComparer.Ordinal);
            Tsrg2Class? current = null;
            var first = true;
            foreach (var line in File.ReadLines(path))
            {
                if (first)
                {
                    first = false;
                    if (!line.StartsWith("tsrg2 ", StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("Identity mappings are not TSRG2.");
                    }
                    continue;
                }
                if (line.Length == 0) continue;
                if (line[0] != '\t')
                {
                    var classParts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    current = classParts.Length >= 2 && requiredClasses.Contains(classParts[1])
                        ? new Tsrg2Class(classParts[0], classParts[1])
                        : null;
                    if (current is not null) classes[current.RightName] = current;
                    continue;
                }
                if (current is null || line.StartsWith("\t\t", StringComparison.Ordinal)) continue;
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    current.Fields[parts[1]] = parts[0];
                }
                else if (parts.Length >= 3 && parts[1].StartsWith('('))
                {
                    current.Methods.Add(new Tsrg2Method(parts[0], parts[1], parts[2]));
                }
            }
            return new Tsrg2Mappings(classes);
        }

        public Tsrg2Class RequireClass(string rightName)
        {
            return _classes.TryGetValue(rightName, out var mapping)
                ? mapping
                : throw new InvalidDataException($"Required identity mapping class is missing: {rightName}");
        }
    }

    private sealed class Tsrg2Class
    {
        public Tsrg2Class(string leftName, string rightName)
        {
            LeftName = leftName;
            RightName = rightName;
        }

        public string LeftName { get; }
        public string RightName { get; }
        public Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);
        public List<Tsrg2Method> Methods { get; } = [];

        public string RequireField(string rightName) => Fields.TryGetValue(rightName, out var leftName)
            ? leftName
            : throw new InvalidDataException($"Required identity mapping field is missing: {RightName}.{rightName}");

        public Tsrg2Method RequireMethod(string rightName, Func<string, bool> descriptorPredicate)
        {
            return Methods.FirstOrDefault(method => method.RightName == rightName && descriptorPredicate(method.LeftDescriptor))
                ?? throw new InvalidDataException($"Required identity mapping method is missing: {RightName}.{rightName}");
        }
    }

    private sealed record Tsrg2Method(string LeftName, string LeftDescriptor, string RightName);
}

internal sealed record IdentityAdapterConfiguration(
    string MappingPath,
    IReadOnlyDictionary<string, string> Properties,
    IReadOnlyList<IdentityAdapterTarget> Targets);

internal sealed record IdentityAdapterTarget(string JarPath, string ClassName);
