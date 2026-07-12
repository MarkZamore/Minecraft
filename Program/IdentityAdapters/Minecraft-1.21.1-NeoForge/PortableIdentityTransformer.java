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
import jdk.internal.org.objectweb.asm.tree.VarInsnNode;

public final class PortableIdentityTransformer implements ClassFileTransformer {
    private static final String LOGIN_LISTENER =
        "net/minecraft/server/network/ServerLoginPacketListenerImpl";
    private static final String HELLO_DESCRIPTOR =
        "(Lnet/minecraft/network/protocol/login/ServerboundHelloPacket;)V";
    private static final String VERIFY_DESCRIPTOR =
        "(Lcom/mojang/authlib/GameProfile;)V";
    private static final String HOOKS =
        "minecraft/portable/identity/PortableIdentityHooks";

    @Override
    public byte[] transform(
        Module module,
        ClassLoader loader,
        String className,
        Class<?> classBeingRedefined,
        ProtectionDomain protectionDomain,
        byte[] classfileBuffer) {
        if (!LOGIN_LISTENER.equals(className)) {
            return null;
        }

        ClassNode node = new ClassNode(Opcodes.ASM9);
        new ClassReader(classfileBuffer).accept(node, 0);
        boolean helloPatched = false;
        boolean duplicatePatched = false;
        for (MethodNode method : node.methods) {
            if ("handleHello".equals(method.name) && HELLO_DESCRIPTOR.equals(method.desc)) {
                prependGuard(method, "handleHello");
                helloPatched = true;
            } else if ("verifyLoginAndFinishConnectionSetup".equals(method.name)
                && VERIFY_DESCRIPTOR.equals(method.desc)) {
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
}
