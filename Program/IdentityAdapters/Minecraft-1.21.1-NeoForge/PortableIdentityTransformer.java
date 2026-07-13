package minecraft.portable.identity;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;
import jdk.internal.org.objectweb.asm.ClassReader;
import jdk.internal.org.objectweb.asm.ClassWriter;
import jdk.internal.org.objectweb.asm.Opcodes;
import jdk.internal.org.objectweb.asm.tree.ClassNode;
import jdk.internal.org.objectweb.asm.tree.FrameNode;
import jdk.internal.org.objectweb.asm.tree.InsnList;
import jdk.internal.org.objectweb.asm.tree.InsnNode;
import jdk.internal.org.objectweb.asm.tree.JumpInsnNode;
import jdk.internal.org.objectweb.asm.tree.LabelNode;
import jdk.internal.org.objectweb.asm.tree.MethodInsnNode;
import jdk.internal.org.objectweb.asm.tree.MethodNode;
import jdk.internal.org.objectweb.asm.tree.TypeInsnNode;
import jdk.internal.org.objectweb.asm.tree.VarInsnNode;

public final class PortableIdentityTransformer implements ClassFileTransformer {
    private static final String LOGIN_LISTENER =
        "net/minecraft/server/network/ServerLoginPacketListenerImpl";
    private static final String OBFUSCATED_LOGIN_LISTENER = "arw";
    private static final String HELLO_DESCRIPTOR =
        "(Lnet/minecraft/network/protocol/login/ServerboundHelloPacket;)V";
    private static final String OBFUSCATED_HELLO_DESCRIPTOR = "(Laiy;)V";
    private static final String VERIFY_DESCRIPTOR =
        "(Lcom/mojang/authlib/GameProfile;)V";
    private static final String PLAYER_INFO =
        "net/minecraft/client/multiplayer/PlayerInfo";
    private static final String OBFUSCATED_PLAYER_INFO = "fzq";
    private static final String SKIN_LOOKUP_DESCRIPTOR =
        "(Lcom/mojang/authlib/GameProfile;)Ljava/util/function/Supplier;";
    private static final String SKIN_SELECTION_DESCRIPTOR =
        "(Ljava/util/concurrent/CompletableFuture;Lnet/minecraft/client/resources/PlayerSkin;Z)" +
        "Lnet/minecraft/client/resources/PlayerSkin;";
    private static final String OBFUSCATED_SKIN_SELECTION_DESCRIPTOR =
        "(Ljava/util/concurrent/CompletableFuture;Lgrl;Z)Lgrl;";
    private static final String HOOKS =
        "minecraft/portable/identity/PortableIdentityHooks";
    private static final String SKIN_PROFILES =
        "minecraft/portable/identity/PortableSkinProfiles";

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

    private static boolean matchesMethod(
        MethodNode method,
        String namesProperty,
        String defaultNames,
        String descriptorsProperty,
        String defaultDescriptors) {
        return contains(property(namesProperty, defaultNames), method.name)
            && contains(property(descriptorsProperty, defaultDescriptors), method.desc);
    }

    @Override
    public byte[] transform(
        Module module,
        ClassLoader loader,
        String className,
        Class<?> classBeingRedefined,
        ProtectionDomain protectionDomain,
        byte[] classfileBuffer) {
        String listeners = property(
            "loginClasses",
            LOGIN_LISTENER + "," + OBFUSCATED_LOGIN_LISTENER);
        String playerInfoClasses = property(
            "playerInfoClasses",
            PLAYER_INFO + "," + OBFUSCATED_PLAYER_INFO);
        boolean loginClass = contains(listeners, className);
        boolean playerInfoClass = contains(playerInfoClasses, className);
        if (!loginClass && !playerInfoClass) {
            return null;
        }

        ClassNode node = new ClassNode(Opcodes.ASM9);
        new ClassReader(classfileBuffer).accept(node, 0);
        if (playerInfoClass) {
            return transformPlayerInfo(node, className);
        }

        boolean helloPatched = false;
        boolean duplicatePatched = false;
        for (MethodNode method : node.methods) {
            if (matchesMethod(
                method,
                "helloMethods",
                "handleHello,a",
                "helloDescriptors",
                HELLO_DESCRIPTOR + "," + OBFUSCATED_HELLO_DESCRIPTOR)) {
                prependGuard(method, "handleHello");
                helloPatched = true;
            } else if (matchesMethod(
                method,
                "verifyMethods",
                "verifyLoginAndFinishConnectionSetup,c",
                "verifyDescriptors",
                VERIFY_DESCRIPTOR)) {
                prependGuard(method, "rejectDuplicateUuid");
                duplicatePatched = true;
            }
        }

        if (!helloPatched || !duplicatePatched) {
            throw new IllegalStateException(
                "Unsupported Minecraft login bytecode: required methods were not found.");
        }

        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
        node.accept(writer);
        System.out.println("[PortableIdentity] Patched login class " + className + ".");
        return writer.toByteArray();
    }

    private static byte[] transformPlayerInfo(ClassNode node, String className) {
        boolean lookupPatched = false;
        boolean selectionPatched = false;
        for (MethodNode method : node.methods) {
            if (matchesMethod(
                method,
                "skinLookupMethods",
                "createSkinLookup,a",
                "skinLookupDescriptors",
                SKIN_LOOKUP_DESCRIPTOR)) {
                prependSkinRegistration(method);
                lookupPatched = true;
            } else if (matchesMethod(
                method,
                "skinSelectionMethods",
                "lambda$createSkinLookup$2,a",
                "skinSelectionDescriptors",
                SKIN_SELECTION_DESCRIPTOR + "," + OBFUSCATED_SKIN_SELECTION_DESCRIPTOR)) {
                replaceSkinSelection(method);
                selectionPatched = true;
            }
        }

        if (!lookupPatched || !selectionPatched) {
            throw new IllegalStateException(
                "Unsupported Minecraft skin bytecode: required methods were not found.");
        }

        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
        node.accept(writer);
        System.out.println("[PortableIdentity] Patched player skin class " + className + ".");
        return writer.toByteArray();
    }

    private static void prependGuard(MethodNode method, String hookName) {
        LabelNode continueLabel = new LabelNode();
        InsnList prefix = new InsnList();
        prefix.add(new VarInsnNode(Opcodes.ALOAD, 0));
        prefix.add(new VarInsnNode(Opcodes.ALOAD, 1));
        prefix.add(new MethodInsnNode(
            Opcodes.INVOKESTATIC,
            HOOKS,
            hookName,
            "(Ljava/lang/Object;Ljava/lang/Object;)Z",
            false));
        prefix.add(new JumpInsnNode(Opcodes.IFEQ, continueLabel));
        prefix.add(new InsnNode(Opcodes.RETURN));
        prefix.add(continueLabel);
        prefix.add(new FrameNode(Opcodes.F_SAME, 0, null, 0, null));
        method.instructions.insert(prefix);
    }

    private static void prependSkinRegistration(MethodNode method) {
        InsnList prefix = new InsnList();
        prefix.add(new VarInsnNode(Opcodes.ALOAD, 0));
        prefix.add(new MethodInsnNode(
            Opcodes.INVOKESTATIC,
            SKIN_PROFILES,
            "apply",
            "(Ljava/lang/Object;)V",
            false));
        method.instructions.insert(prefix);
    }

    private static void replaceSkinSelection(MethodNode method) {
        int returnStart = method.desc.lastIndexOf(')') + 1;
        String returnDescriptor = method.desc.substring(returnStart);
        if (!returnDescriptor.startsWith("L") || !returnDescriptor.endsWith(";")) {
            throw new IllegalStateException("Portable skin selector has an unsupported return type.");
        }
        String returnType = returnDescriptor.substring(1, returnDescriptor.length() - 1);

        method.instructions.clear();
        method.tryCatchBlocks.clear();
        if (method.localVariables != null) {
            method.localVariables.clear();
        }
        method.visibleLocalVariableAnnotations = null;
        method.invisibleLocalVariableAnnotations = null;

        method.instructions.add(new VarInsnNode(Opcodes.ALOAD, 0));
        method.instructions.add(new VarInsnNode(Opcodes.ALOAD, 1));
        method.instructions.add(new VarInsnNode(Opcodes.ILOAD, 2));
        method.instructions.add(new MethodInsnNode(
            Opcodes.INVOKESTATIC,
            SKIN_PROFILES,
            "selectSkin",
            "(Ljava/lang/Object;Ljava/lang/Object;Z)Ljava/lang/Object;",
            false));
        method.instructions.add(new TypeInsnNode(Opcodes.CHECKCAST, returnType));
        method.instructions.add(new InsnNode(Opcodes.ARETURN));
    }
}
