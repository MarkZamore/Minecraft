package minecraft.portable.identity;

import java.lang.reflect.Constructor;
import java.util.UUID;

public final class PortableIdentityHooks {
    private PortableIdentityHooks() {
    }

    public static boolean handleHello(Object listener, Object packet) {
        try {
            Object server = PortableIdentityReflection.getField(listener, "server");
            String name = (String) PortableIdentityReflection.invoke(packet, "name");
            UUID profileId = (UUID) PortableIdentityReflection.invoke(packet, "profileId");
            Object connection = PortableIdentityReflection.getField(listener, "connection");
            boolean memoryConnection = (Boolean) PortableIdentityReflection.invoke(connection, "isMemoryConnection");
            if (memoryConnection) {
                return false;
            }

            boolean usesAuthentication = (Boolean) PortableIdentityReflection.invoke(server, "usesAuthentication");
            if (usesAuthentication) {
                return false;
            }
            if (profileId == null || profileId.equals(new UUID(0L, 0L))) {
                throw new IllegalStateException("Client supplied an empty portable UUID.");
            }

            ClassLoader loader = listener.getClass().getClassLoader();
            Class<?> profileType = Class.forName("com.mojang.authlib.GameProfile", true, loader);
            Constructor<?> constructor = profileType.getConstructor(UUID.class, String.class);
            Object profile = constructor.newInstance(profileId, name);
            PortableIdentityReflection.setField(listener, "requestedUsername", name);
            PortableIdentityReflection.invokeDeclared(
                listener,
                "startClientVerification",
                new Class<?>[] { profileType },
                profile);
            return true;
        } catch (ReflectiveOperationException exception) {
            throw new IllegalStateException("Portable UUID login failed.", exception);
        }
    }

    public static boolean rejectDuplicateUuid(Object listener, Object profile) {
        try {
            UUID profileId = (UUID) PortableIdentityReflection.invoke(profile, "getId");
            Object server = PortableIdentityReflection.getField(listener, "server");
            Object playerList = PortableIdentityReflection.invoke(server, "getPlayerList");
            Object existingPlayer = PortableIdentityReflection.invokeDeclared(
                playerList,
                "getPlayer",
                new Class<?>[] { UUID.class },
                profileId);
            if (existingPlayer == null) {
                return false;
            }

            ClassLoader loader = listener.getClass().getClassLoader();
            Class<?> componentType = Class.forName("net.minecraft.network.chat.Component", true, loader);
            Object message = componentType
                .getMethod("literal", String.class)
                .invoke(null, "This portable UUID is already connected.");
            PortableIdentityReflection.invokeDeclared(
                listener,
                "disconnect",
                new Class<?>[] { componentType },
                message);
            return true;
        } catch (ReflectiveOperationException exception) {
            throw new IllegalStateException("Duplicate portable UUID check failed.", exception);
        }
    }
}
