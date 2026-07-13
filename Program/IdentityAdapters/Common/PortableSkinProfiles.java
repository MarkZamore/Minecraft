package minecraft.portable.identity;

import java.lang.reflect.Constructor;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Base64;
import java.util.Collections;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;

public final class PortableSkinProfiles {
    private static volatile long registryModified = Long.MIN_VALUE;
    private static volatile Map<UUID, SkinEntry> entries = Collections.emptyMap();

    private PortableSkinProfiles() {
    }

    public static void apply(Object profile) {
        if (profile == null) {
            return;
        }

        try {
            UUID id = (UUID) PortableIdentityReflection.invoke(profile, "getId");
            if (id == null) {
                return;
            }
            SkinEntry entry = loadEntries().get(id);
            if (entry == null) {
                return;
            }

            String name = (String) PortableIdentityReflection.invoke(profile, "getName");
            String textureJson = createTextureJson(id, name, entry);
            String encoded = Base64.getEncoder().encodeToString(textureJson.getBytes(StandardCharsets.UTF_8));
            ClassLoader loader = profile.getClass().getClassLoader();
            Class<?> propertyType = Class.forName("com.mojang.authlib.properties.Property", true, loader);
            Object property = createProperty(propertyType, encoded);
            Object properties = PortableIdentityReflection.invoke(profile, "getProperties");
            invokeCompatible(properties, "removeAll", "textures");
            invokeCompatible(properties, "put", "textures", property);
        } catch (ReflectiveOperationException exception) {
            throw new IllegalStateException("Portable skin could not be attached to GameProfile.", exception);
        }
    }

    private static Map<UUID, SkinEntry> loadEntries() {
        String configuredPath = System.getProperty("minecraft.portable.skin.registry", "").trim();
        if (configuredPath.isEmpty()) {
            return Collections.emptyMap();
        }

        try {
            Path path = Path.of(configuredPath);
            long modified = Files.exists(path) ? Files.getLastModifiedTime(path).toMillis() : -1L;
            if (modified == registryModified) {
                return entries;
            }

            Map<UUID, SkinEntry> loaded = new HashMap<>();
            if (modified >= 0) {
                for (String line : Files.readAllLines(path, StandardCharsets.UTF_8)) {
                    String[] fields = line.split("\\|", 4);
                    if (fields.length != 4) {
                        continue;
                    }
                    try {
                        UUID id = UUID.fromString(fields[0]);
                        String model = "slim".equalsIgnoreCase(fields[2]) ? "slim" : "classic";
                        if (fields[1].matches("[0-9A-Fa-f]{64}") && fields[3].startsWith("http://127.0.0.1:")) {
                            loaded.put(id, new SkinEntry(fields[3], model));
                        }
                    } catch (IllegalArgumentException ignored) {
                        // Ignore an incomplete line while the launcher refreshes the registry.
                    }
                }
            }
            entries = Collections.unmodifiableMap(loaded);
            registryModified = modified;
            return entries;
        } catch (Exception exception) {
            System.err.println("[PortableIdentity] Skin registry could not be read: " + exception.getMessage());
            return entries;
        }
    }

    private static Object createProperty(Class<?> propertyType, String encoded)
        throws ReflectiveOperationException {
        for (Constructor<?> constructor : propertyType.getConstructors()) {
            Class<?>[] parameters = constructor.getParameterTypes();
            if (parameters.length == 2 && parameters[0] == String.class && parameters[1] == String.class) {
                return constructor.newInstance("textures", encoded);
            }
            if (parameters.length == 3 && parameters[0] == String.class && parameters[1] == String.class &&
                parameters[2] == String.class) {
                return constructor.newInstance("textures", encoded, null);
            }
        }
        throw new NoSuchMethodException(propertyType.getName() + " texture constructor");
    }

    private static Object invokeCompatible(Object target, String name, Object... arguments)
        throws ReflectiveOperationException {
        for (Method method : target.getClass().getMethods()) {
            if (!method.getName().equals(name) || method.getParameterCount() != arguments.length) {
                continue;
            }
            Class<?>[] parameterTypes = method.getParameterTypes();
            boolean compatible = true;
            for (int index = 0; index < arguments.length; index++) {
                if (arguments[index] != null && !parameterTypes[index].isAssignableFrom(arguments[index].getClass())) {
                    compatible = false;
                    break;
                }
            }
            if (compatible) {
                method.setAccessible(true);
                return method.invoke(target, arguments);
            }
        }
        throw new NoSuchMethodException(target.getClass().getName() + "." + name);
    }

    private static String createTextureJson(UUID id, String name, SkinEntry entry) {
        String metadata = "slim".equals(entry.model()) ? ",\"metadata\":{\"model\":\"slim\"}" : "";
        return "{\"timestamp\":" + System.currentTimeMillis() +
            ",\"profileId\":\"" + id.toString().replace("-", "") +
            "\",\"profileName\":\"" + escapeJson(name == null ? "" : name) +
            "\",\"textures\":{\"SKIN\":{\"url\":\"" + escapeJson(entry.url()) + "\"" + metadata + "}}}";
    }

    private static String escapeJson(String value) {
        StringBuilder output = new StringBuilder(value.length() + 16);
        for (int index = 0; index < value.length(); index++) {
            char character = value.charAt(index);
            switch (character) {
                case '\\' -> output.append("\\\\");
                case '"' -> output.append("\\\"");
                case '\b' -> output.append("\\b");
                case '\f' -> output.append("\\f");
                case '\n' -> output.append("\\n");
                case '\r' -> output.append("\\r");
                case '\t' -> output.append("\\t");
                default -> {
                    if (character < 0x20) {
                        output.append(String.format("\\u%04x", (int) character));
                    } else {
                        output.append(character);
                    }
                }
            }
        }
        return output.toString();
    }

    private record SkinEntry(String url, String model) {
    }
}
