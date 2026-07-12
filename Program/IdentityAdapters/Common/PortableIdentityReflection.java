package minecraft.portable.identity;

import java.lang.reflect.Field;
import java.lang.reflect.Method;

public final class PortableIdentityReflection {
    private PortableIdentityReflection() {
    }

    public static Object getField(Object target, String name) throws ReflectiveOperationException {
        Field field = findField(target.getClass(), name);
        field.setAccessible(true);
        return field.get(target);
    }

    public static void setField(Object target, String name, Object value) throws ReflectiveOperationException {
        Field field = findField(target.getClass(), name);
        field.setAccessible(true);
        field.set(target, value);
    }

    public static Object invoke(Object target, String name) throws ReflectiveOperationException {
        Method method = target.getClass().getMethod(name);
        return method.invoke(target);
    }

    public static Object invokeDeclared(
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
