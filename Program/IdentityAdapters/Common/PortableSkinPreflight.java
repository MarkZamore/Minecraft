package minecraft.portable.identity;

import java.lang.reflect.Method;

public final class PortableSkinPreflight {
    private PortableSkinPreflight() {
    }

    public static void main(String[] arguments) throws Exception {
        if (arguments.length != 3) {
            throw new IllegalArgumentException("Usage: <registered-url> <unregistered-url> <official-url>");
        }

        Class<?> checker = Class.forName(
            "com.mojang.authlib.yggdrasil.TextureUrlChecker",
            true,
            ClassLoader.getSystemClassLoader());
        Method isAllowed = checker.getMethod("isAllowedTextureDomain", String.class);
        if (!allowed(isAllowed, arguments[0])) {
            throw new IllegalStateException("Registered portable skin URL was rejected.");
        }
        if (allowed(isAllowed, arguments[1])) {
            throw new IllegalStateException("Unregistered local skin URL was accepted.");
        }
        if (!allowed(isAllowed, arguments[2])) {
            throw new IllegalStateException("Official Minecraft texture URL was rejected.");
        }

        System.out.println("Portable skin URL preflight passed.");
    }

    private static boolean allowed(Method method, String url) throws Exception {
        return Boolean.TRUE.equals(method.invoke(null, url));
    }
}
