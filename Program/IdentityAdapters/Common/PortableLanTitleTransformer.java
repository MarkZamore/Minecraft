package minecraft.portable.identity;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;
import jdk.internal.org.objectweb.asm.ClassReader;
import jdk.internal.org.objectweb.asm.ClassWriter;
import jdk.internal.org.objectweb.asm.Opcodes;
import jdk.internal.org.objectweb.asm.tree.AbstractInsnNode;
import jdk.internal.org.objectweb.asm.tree.ClassNode;
import jdk.internal.org.objectweb.asm.tree.FieldInsnNode;
import jdk.internal.org.objectweb.asm.tree.FieldNode;
import jdk.internal.org.objectweb.asm.tree.InsnList;
import jdk.internal.org.objectweb.asm.tree.MethodInsnNode;
import jdk.internal.org.objectweb.asm.tree.MethodNode;
import jdk.internal.org.objectweb.asm.tree.TypeInsnNode;
import jdk.internal.org.objectweb.asm.tree.VarInsnNode;

public final class PortableLanTitleTransformer implements ClassFileTransformer {
    private static final String HOOKS =
        "minecraft/portable/identity/PortableLanTitleHooks";

    @Override
    public byte[] transform(
        Module module,
        ClassLoader loader,
        String className,
        Class<?> classBeingRedefined,
        ProtectionDomain protectionDomain,
        byte[] classfileBuffer) {
        if (!contains(property("lanEntryClasses", ""), className)) {
            return null;
        }

        ClassNode node = new ClassNode(Opcodes.ASM9);
        new ClassReader(classfileBuffer).accept(node, 0);
        return transformEntry(node, className);
    }

    private static byte[] transformEntry(ClassNode node, String className) {
        FieldNode lanServerField = null;
        String lanServerFields = property("lanServerFields", "serverData,b");
        for (FieldNode field : node.fields) {
            if (contains(lanServerFields, field.name)) {
                lanServerField = field;
                break;
            }
        }
        if (lanServerField == null) {
            throw unsupported("server field was not found");
        }

        boolean headerPatched = false;
        boolean subtitlePatched = false;
        String lanHeaderFields = property("lanHeaderFields", "LAN_SERVER_HEADER,d");
        String lanServerClasses = property(
            "lanServerClasses",
            "net/minecraft/client/server/LanServer,gup");
        String lanMotdMethods = property("lanMotdMethods", "getMotd,a");
        for (MethodNode method : node.methods) {
            if (!matchesMethod(
                method,
                "lanRenderMethods",
                "render,a",
                "lanRenderDescriptors",
                "(Lnet/minecraft/client/gui/GuiGraphics;IIIIIIIZF)V,(Lfhz;IIIIIIIZF)V")) {
                continue;
            }

            for (AbstractInsnNode instruction = method.instructions.getFirst();
                 instruction != null;
                 instruction = instruction.getNext()) {
                if (!(instruction instanceof FieldInsnNode header) ||
                    header.getOpcode() != Opcodes.GETSTATIC ||
                    !header.owner.equals(node.name) ||
                    !contains(lanHeaderFields, header.name)) {
                    continue;
                }
                if (!header.desc.startsWith("L") || !header.desc.endsWith(";")) {
                    throw unsupported("header is not an object");
                }

                InsnList replacement = new InsnList();
                replacement.add(new VarInsnNode(Opcodes.ALOAD, 0));
                replacement.add(new FieldInsnNode(
                    Opcodes.GETFIELD,
                    node.name,
                    lanServerField.name,
                    lanServerField.desc));
                replacement.add(new FieldInsnNode(
                    Opcodes.GETSTATIC,
                    header.owner,
                    header.name,
                    header.desc));
                replacement.add(new MethodInsnNode(
                    Opcodes.INVOKESTATIC,
                    HOOKS,
                    "resolveWorldTitle",
                    "(Ljava/lang/Object;Ljava/lang/Object;)Ljava/lang/Object;",
                    false));
                replacement.add(new TypeInsnNode(
                    Opcodes.CHECKCAST,
                    header.desc.substring(1, header.desc.length() - 1)));
                method.instructions.insertBefore(header, replacement);
                method.instructions.remove(header);
                headerPatched = true;
                break;
            }

            for (AbstractInsnNode instruction = method.instructions.getFirst();
                 instruction != null;
                 instruction = instruction.getNext()) {
                if (!(instruction instanceof MethodInsnNode motdCall) ||
                    !contains(lanServerClasses, motdCall.owner) ||
                    !contains(lanMotdMethods, motdCall.name) ||
                    !motdCall.desc.equals("()Ljava/lang/String;")) {
                    continue;
                }
                method.instructions.insert(motdCall, new MethodInsnNode(
                    Opcodes.INVOKESTATIC,
                    HOOKS,
                    "resolveSubtitle",
                    "(Ljava/lang/String;)Ljava/lang/String;",
                    false));
                subtitlePatched = true;
                break;
            }
        }

        if (!headerPatched || !subtitlePatched) {
            throw unsupported("LAN title or subtitle render was not found");
        }

        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
        node.accept(writer);
        System.out.println("[PortableIdentity] Patched LAN world title class " + className + ".");
        return writer.toByteArray();
    }

    private static boolean matchesMethod(
        MethodNode method,
        String namesProperty,
        String defaultNames,
        String descriptorsProperty,
        String defaultDescriptors) {
        return contains(property(namesProperty, defaultNames), method.name)
            && contains(property(descriptorsProperty, defaultDescriptors), method.desc);
    }

    private static String property(String name, String fallback) {
        String value = System.getProperty("minecraft.portable.identity." + name);
        return value == null || value.isBlank() ? fallback : value;
    }

    private static boolean contains(String csv, String value) {
        for (String candidate : csv.split(",")) {
            if (candidate.trim().equals(value)) {
                return true;
            }
        }
        return false;
    }

    private static IllegalStateException unsupported(String detail) {
        return new IllegalStateException("Unsupported Minecraft LAN entry bytecode: " + detail + ".");
    }
}
