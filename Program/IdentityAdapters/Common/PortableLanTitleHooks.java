package minecraft.portable.identity;

import java.nio.charset.StandardCharsets;
import java.util.Base64;

public final class PortableLanTitleHooks {
    private PortableLanTitleHooks() {
    }

    public static Object resolveWorldTitle(Object lanServer, Object fallbackTitle) {
        try {
            String motd = (String) PortableIdentityReflection.invoke(
                lanServer,
                aliases("lanMotdMethods", "getMotd", "a"));
            String[] metadata = decodeMetadata(motd);
            if (metadata == null || metadata[0].isBlank()) {
                return fallbackTitle;
            }

            Class<?> componentType = loadClass(
                lanServer.getClass().getClassLoader(),
                aliases("componentClasses", "net.minecraft.network.chat.Component", "wz"));
            return PortableIdentityReflection.invokeStatic(
                componentType,
                new Class<?>[] { String.class },
                new Object[] { metadata[0] },
                aliases("componentLiteralMethods", "literal", "b"));
        } catch (ReflectiveOperationException exception) {
            return fallbackTitle;
        }
    }

    public static String resolveSubtitle(String motd) {
        String[] metadata = decodeMetadata(motd);
        return metadata == null || metadata[1].isBlank() ? motd : metadata[1];
    }

    private static String[] decodeMetadata(String motd) {
        if (motd == null || !motd.startsWith("MinecraftPortable:")) {
            return null;
        }
        String[] parts = motd.split(":", 3);
        if (parts.length != 3) {
            return null;
        }
        try {
            Base64.Decoder decoder = Base64.getUrlDecoder();
            return new String[] {
                new String(decoder.decode(parts[1]), StandardCharsets.UTF_8),
                new String(decoder.decode(parts[2]), StandardCharsets.UTF_8)
            };
        } catch (IllegalArgumentException exception) {
            return null;
        }
    }

    private static Class<?> loadClass(ClassLoader loader, String... names) throws ClassNotFoundException {
        for (String name : names) {
            try {
                return Class.forName(name, true, loader);
            } catch (ClassNotFoundException ignored) {
                // Try the runtime-mapped name.
            }
        }
        throw new ClassNotFoundException(String.join("/", names));
    }

    private static String[] aliases(String propertyName, String... defaults) {
        String value = System.getProperty("minecraft.portable.identity." + propertyName);
        if (value == null || value.isBlank()) {
            return defaults;
        }
        return java.util.Arrays.stream(value.split(","))
            .map(String::trim)
            .filter(candidate -> !candidate.isEmpty())
            .toArray(String[]::new);
    }
}
