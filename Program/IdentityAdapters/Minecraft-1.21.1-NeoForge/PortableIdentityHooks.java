package minecraft.portable.identity;

import java.lang.reflect.Constructor;
import java.util.UUID;

public final class PortableIdentityHooks {
    private PortableIdentityHooks() {
    }

    public static boolean handleHello(Object listener, Object packet) {
        try {
            String name = (String) PortableIdentityReflection.invoke(packet, aliases("packetNameMethods", "name", "b"));
            UUID profileId = (UUID) PortableIdentityReflection.invoke(packet, aliases("packetUuidMethods", "profileId", "e"));
            Object connection = PortableIdentityReflection.getField(listener, aliases("connectionFields", "connection", "g"));
            boolean memoryConnection = (Boolean) PortableIdentityReflection.invoke(
                connection,
                aliases("memoryConnectionMethods", "isMemoryConnection", "e"));
            if (memoryConnection) {
                return false;
            }

            if (profileId == null || profileId.equals(new UUID(0L, 0L))) {
                throw new IllegalStateException("Client supplied an empty portable UUID.");
            }

            ClassLoader loader = listener.getClass().getClassLoader();
            Class<?> profileType = Class.forName("com.mojang.authlib.GameProfile", true, loader);
            Constructor<?> constructor = profileType.getConstructor(UUID.class, String.class);
            Object profile = constructor.newInstance(profileId, name);
            PortableIdentityReflection.setField(
                listener,
                name,
                aliases("requestedUsernameFields", "requestedUsername", "j"));
            PortableIdentityReflection.invokeDeclared(
                listener,
                new Class<?>[] { profileType },
                new Object[] { profile },
                aliases("startVerificationMethods", "startClientVerification", "b"));
            System.out.println("[PortableIdentity] Accepted portable UUID " + profileId + ".");
            return true;
        } catch (ReflectiveOperationException exception) {
            throw new IllegalStateException("Portable UUID login failed.", exception);
        }
    }

    public static boolean rejectDuplicateUuid(Object listener, Object profile) {
        try {
            UUID profileId = (UUID) PortableIdentityReflection.invoke(profile, "getId");
            Object server = PortableIdentityReflection.getField(listener, aliases("serverFields", "server", "f"));
            Object playerList = PortableIdentityReflection.invoke(
                server,
                aliases("playerListMethods", "getPlayerList", "ah"));
            Object existingPlayer = PortableIdentityReflection.invokeDeclared(
                playerList,
                new Class<?>[] { UUID.class },
                new Object[] { profileId },
                aliases("getPlayerMethods", "getPlayer", "a"));
            if (existingPlayer == null) {
                return false;
            }

            ClassLoader loader = listener.getClass().getClassLoader();
            Class<?> componentType = loadClass(
                loader,
                aliases("componentClasses", "net.minecraft.network.chat.Component", "wz"));
            Object message = PortableIdentityReflection.invokeStatic(
                componentType,
                new Class<?>[] { String.class },
                new Object[] { "This portable UUID is already connected." },
                aliases("componentLiteralMethods", "literal", "b"));
            PortableIdentityReflection.invokeDeclared(
                listener,
                new Class<?>[] { componentType },
                new Object[] { message },
                aliases("disconnectMethods", "disconnect", "a"));
            return true;
        } catch (ReflectiveOperationException exception) {
            throw new IllegalStateException("Duplicate portable UUID check failed.", exception);
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
