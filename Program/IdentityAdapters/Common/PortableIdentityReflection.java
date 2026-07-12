package minecraft.portable.identity;

import java.lang.reflect.Field;
import java.lang.reflect.Method;

public final class PortableIdentityReflection {
    private PortableIdentityReflection() {
    }

    public static Object getField(Object target, String... names) throws ReflectiveOperationException {
        Field field = findField(target.getClass(), names);
        field.setAccessible(true);
        return field.get(target);
    }

    public static void setField(Object target, Object value, String... names) throws ReflectiveOperationException {
        Field field = findField(target.getClass(), names);
        field.setAccessible(true);
        field.set(target, value);
    }

    public static Object invoke(Object target, String... names) throws ReflectiveOperationException {
        Method method = findMethod(target.getClass(), names, new Class<?>[0]);
        method.setAccessible(true);
        return method.invoke(target);
    }

    public static Object invokeDeclared(
        Object target,
        Class<?>[] parameterTypes,
        Object[] arguments,
        String... names) throws ReflectiveOperationException {
        Method method = findMethod(target.getClass(), names, parameterTypes);
        method.setAccessible(true);
        return method.invoke(target, arguments);
    }

    public static Object invokeStatic(
        Class<?> type,
        Class<?>[] parameterTypes,
        Object[] arguments,
        String... names) throws ReflectiveOperationException {
        Method method = findMethod(type, names, parameterTypes);
        method.setAccessible(true);
        return method.invoke(null, arguments);
    }

    private static Field findField(Class<?> type, String... names) throws NoSuchFieldException {
        for (String name : names) {
            for (Class<?> current = type; current != null; current = current.getSuperclass()) {
                try {
                    return current.getDeclaredField(name);
                } catch (NoSuchFieldException ignored) {
                    // Continue through the transformed class hierarchy.
                }
            }
        }
        throw new NoSuchFieldException(type.getName() + "." + String.join("/", names));
    }

    private static Method findMethod(
        Class<?> type,
        String[] names,
        Class<?>[] parameterTypes) throws NoSuchMethodException {
        for (String name : names) {
            for (Class<?> current = type; current != null; current = current.getSuperclass()) {
                try {
                    return current.getDeclaredMethod(name, parameterTypes);
                } catch (NoSuchMethodException ignored) {
                    // Continue through the transformed class hierarchy.
                }
            }
        }
        throw new NoSuchMethodException(type.getName() + "." + String.join("/", names));
    }
}
