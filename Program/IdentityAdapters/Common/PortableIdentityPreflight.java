package minecraft.portable.identity;

import java.io.InputStream;
import java.lang.instrument.ClassFileTransformer;
import java.nio.file.Path;
import java.util.Arrays;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;
import jdk.internal.org.objectweb.asm.ClassReader;
import jdk.internal.org.objectweb.asm.tree.ClassNode;
import jdk.internal.org.objectweb.asm.tree.FieldNode;
import jdk.internal.org.objectweb.asm.tree.MethodNode;

public final class PortableIdentityPreflight {
    private PortableIdentityPreflight() {
    }

    public static void main(String[] arguments) throws Exception {
        if (arguments.length != 2) {
            throw new IllegalArgumentException("Usage: <runtime-jar> <internal-class-name>");
        }

        Path jarPath = Path.of(arguments[0]).toAbsolutePath().normalize();
        String className = arguments[1].replace('.', '/');
        try (ZipFile archive = new ZipFile(jarPath.toFile())) {
            byte[] original = readClassBytes(archive, className);

            ClassFileTransformer transformer = isAlias("lanEntryClasses", className)
                ? new PortableLanTitleTransformer()
                : new PortableIdentityTransformer();
            byte[] transformed = transformer.transform(
                null,
                null,
                className,
                null,
                null,
                original);
            if (transformed == null || Arrays.equals(original, transformed)) {
                throw new IllegalStateException("Portable identity transformer did not modify " + className + ".");
            }
            new ClassReader(transformed);
            if (isAlias("loginClasses", className)) {
                verifyHookTargets(archive, className);
            } else if (isAlias("lanEntryClasses", className)) {
                verifyLanTitleTargets(archive, className);
            } else if (!isAlias("playerInfoClasses", className) &&
                !isAlias("textureUrlCheckerClasses", className)) {
                throw new IllegalStateException("Unexpected portable identity target: " + className);
            }
        }
        System.out.println("Portable identity preflight passed: " + className + " in " + jarPath);
    }

    private static void verifyHookTargets(ZipFile archive, String loginClass) throws Exception {
        int mappingIndex = aliasIndex("loginClasses", loginClass);
        ClassNode listener = readClass(archive, loginClass);
        requireField(listener, "serverFields");
        requireField(listener, "connectionFields");
        requireField(listener, "requestedUsernameFields");
        requireMethod(listener, "startVerificationMethods", 1);
        requireMethod(listener, "disconnectMethods", 1);

        ClassNode packet = readClass(archive, alias("packetClasses", mappingIndex));
        requireMethod(packet, "packetNameMethods", 0);
        requireMethod(packet, "packetUuidMethods", 0);

        ClassNode connection = readClass(archive, alias("connectionClasses", mappingIndex));
        requireMethod(connection, "memoryConnectionMethods", 0);

        ClassNode server = readClass(archive, alias("serverClasses", mappingIndex));
        requireMethod(server, "playerListMethods", 0);

        ClassNode playerList = readClass(archive, alias("playerListClasses", mappingIndex));
        requireMethod(playerList, "getPlayerMethods", 1);

        ClassNode component = readClass(archive, alias("componentClasses", mappingIndex).replace('.', '/'));
        requireMethod(component, "componentLiteralMethods", 1);
    }

    private static void verifyLanTitleTargets(ZipFile archive, String entryClass) throws Exception {
        int mappingIndex = aliasIndex("lanEntryClasses", entryClass);
        ClassNode entry = readClass(archive, entryClass);
        requireField(entry, "lanServerFields");
        requireField(entry, "lanHeaderFields");
        requireMethod(entry, "lanRenderMethods", 10);

        ClassNode lanServer = readClass(archive, alias("lanServerClasses", mappingIndex));
        requireMethod(lanServer, "lanMotdMethods", 0);

        ClassNode component = readClass(archive, alias("componentClasses", mappingIndex).replace('.', '/'));
        requireMethod(component, "componentLiteralMethods", 1);

        String encoded = "MinecraftPortable:VGVzdCBXb3JsZA:UGxheWVy";
        if (!"Player".equals(PortableLanTitleHooks.resolveSubtitle(encoded)) ||
            !"ordinary motd".equals(PortableLanTitleHooks.resolveSubtitle("ordinary motd"))) {
            throw new IllegalStateException("Portable LAN metadata decoding failed.");
        }
    }

    private static byte[] readClassBytes(ZipFile archive, String className) throws Exception {
        ZipEntry entry = archive.getEntry(className + ".class");
        if (entry == null) {
            throw new IllegalStateException("Required identity class is missing: " + className);
        }
        try (InputStream input = archive.getInputStream(entry)) {
            return input.readAllBytes();
        }
    }

    private static ClassNode readClass(ZipFile archive, String className) throws Exception {
        ClassNode node = new ClassNode();
        new ClassReader(readClassBytes(archive, className)).accept(node, ClassReader.SKIP_CODE);
        return node;
    }

    private static void requireField(ClassNode owner, String propertyName) {
        String[] names = aliases(propertyName);
        for (FieldNode field : owner.fields) {
            if (Arrays.asList(names).contains(field.name)) {
                return;
            }
        }
        throw new IllegalStateException("Required identity field is missing: " + owner.name + "." + String.join("/", names));
    }

    private static void requireMethod(ClassNode owner, String propertyName, int argumentCount) {
        String[] names = aliases(propertyName);
        for (MethodNode method : owner.methods) {
            if (Arrays.asList(names).contains(method.name) && argumentCount(method.desc) == argumentCount) {
                return;
            }
        }
        throw new IllegalStateException("Required identity method is missing: " + owner.name + "." + String.join("/", names));
    }

    private static int argumentCount(String descriptor) {
        int count = 0;
        for (int index = 1; descriptor.charAt(index) != ')'; index++) {
            while (descriptor.charAt(index) == '[') index++;
            if (descriptor.charAt(index) == 'L') index = descriptor.indexOf(';', index);
            count++;
        }
        return count;
    }

    private static int aliasIndex(String propertyName, String value) {
        String[] values = aliases(propertyName);
        for (int index = 0; index < values.length; index++) {
            if (values[index].equals(value)) return index;
        }
        throw new IllegalStateException("Runtime identity alias is not configured: " + value);
    }

    private static String alias(String propertyName, int index) {
        String[] values = aliases(propertyName);
        return values[Math.min(index, values.length - 1)];
    }

    private static boolean isAlias(String propertyName, String value) {
        return Arrays.asList(aliases(propertyName)).contains(value);
    }

    private static String[] aliases(String propertyName) {
        String value = System.getProperty("minecraft.portable.identity." + propertyName);
        if (value == null || value.isBlank()) {
            throw new IllegalStateException("Identity preflight property is missing: " + propertyName);
        }
        return Arrays.stream(value.split(",")).map(String::trim).filter(item -> !item.isEmpty()).toArray(String[]::new);
    }
}
