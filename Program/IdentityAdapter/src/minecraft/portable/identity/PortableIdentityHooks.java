package minecraft.portable.identity;

import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.util.UUID;

public final class PortableIdentityHooks {
    private PortableIdentityHooks() {
    }

    public static boolean handleHello(Object listener, Object packet) {
        try {
            Object server = getField(listener, "server");
            String name = (String) invoke(packet, "name");
            UUID profileId = (UUID) invoke(packet, "profileId");
            Object connection = getField(listener, "connection");
            boolean memoryConnection = (Boolean) invoke(connection, "isMemoryConnection");
            if (memoryConnection) {
                return false;
            }

            boolean usesAuthentication = (Boolean) invoke(server, "usesAuthentication");
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
            setField(listener, "requestedUsername", name);
            invokeDeclared(listener, "startClientVerification", new Class<?>[] { profileType }, profile);
            return true;
        } catch (ReflectiveOperationException exception) {
            throw new IllegalStateException("Portable UUID login failed.", exception);
        }
    }

    public static boolean rejectDuplicateUuid(Object listener, Object profile) {
        try {
            UUID profileId = (UUID) invoke(profile, "getId");
            Object server = getField(listener, "server");
            Object playerList = invoke(server, "getPlayerList");
            Object existingPlayer = invokeDeclared(
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
            invokeDeclared(listener, "disconnect", new Class<?>[] { componentType }, message);
            return true;
        } catch (ReflectiveOperationException exception) {
            throw new IllegalStateException("Duplicate portable UUID check failed.", exception);
        }
    }

    private static Object getField(Object target, String name) throws ReflectiveOperationException {
        Field field = findField(target.getClass(), name);
        field.setAccessible(true);
        return field.get(target);
    }

    private static void setField(Object target, String name, Object value) throws ReflectiveOperationException {
        Field field = findField(target.getClass(), name);
        field.setAccessible(true);
        field.set(target, value);
    }

    private static Object invoke(Object target, String name) throws ReflectiveOperationException {
        Method method = target.getClass().getMethod(name);
        return method.invoke(target);
    }

    private static Object invokeDeclared(
        Object target,
        String name,
        Class<?>[] parameterTypes,
        Object... arguments) throws ReflectiveOperationException {
        Method method = findMethod(target.getClass(), name, parameterTypes);
        method.setAccessible(true);
        return method.invoke(target, arguments);
    }

    private static Field findField(Class<?> type, String name) throws NoSuchFieldException {
        for (Class<?> current = type; current != null; current = current.getSuperclass()) {
            try {
                return current.getDeclaredField(name);
            } catch (NoSuchFieldException ignored) {
                // Continue through the transformed class hierarchy.
            }
        }
        throw new NoSuchFieldException(type.getName() + "." + name);
    }

    private static Method findMethod(
        Class<?> type,
        String name,
        Class<?>[] parameterTypes) throws NoSuchMethodException {
        for (Class<?> current = type; current != null; current = current.getSuperclass()) {
            try {
                return current.getDeclaredMethod(name, parameterTypes);
            } catch (NoSuchMethodException ignored) {
                // Continue through the transformed class hierarchy.
            }
        }
        throw new NoSuchMethodException(type.getName() + "." + name);
    }
}
