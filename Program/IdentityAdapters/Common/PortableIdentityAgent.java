package minecraft.portable.identity;

import java.lang.instrument.Instrumentation;
import java.nio.file.Path;
import java.util.jar.JarFile;

public final class PortableIdentityAgent {
    private static JarFile bootstrapJar;

    private PortableIdentityAgent() {
    }

    public static void premain(String arguments, Instrumentation instrumentation) {
        if (!Boolean.getBoolean("minecraft.portable.identity.enabled")) {
            return;
        }

        try {
            Path agentPath = Path.of(
                PortableIdentityAgent.class.getProtectionDomain().getCodeSource().getLocation().toURI());
            bootstrapJar = new JarFile(agentPath.toFile());
            instrumentation.appendToBootstrapClassLoaderSearch(bootstrapJar);
        } catch (Exception exception) {
            throw new IllegalStateException("Portable identity hooks could not be exposed to Minecraft.", exception);
        }

        instrumentation.addTransformer(new PortableIdentityTransformer(), false);
        System.out.println("[PortableIdentity] Stable UUID adapter enabled.");
    }
}
