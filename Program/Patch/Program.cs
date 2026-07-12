using BsDiff;

namespace Minecraft.Patch;

internal static class Program
{
    private const int InvalidArgumentsExitCode = 2;

    private static int Main(string[] args)
    {
        if (args.Length != 4 || args[0] is not ("create" or "apply"))
        {
            Console.Error.WriteLine("Usage: DeltaPatchTool <create|apply> <old-file> <new-or-patch-file> <output-file>");
            return InvalidArgumentsExitCode;
        }

        try
        {
            var sourcePath = Path.GetFullPath(args[1]);
            var inputPath = Path.GetFullPath(args[2]);
            var outputPath = Path.GetFullPath(args[3]);
            RequireInputFile(sourcePath);
            RequireInputFile(inputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            if (string.Equals(args[0], "create", StringComparison.Ordinal))
            {
                CreatePatch(sourcePath, inputPath, outputPath);
            }
            else
            {
                ApplyPatch(sourcePath, inputPath, outputPath);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void CreatePatch(string oldFilePath, string newFilePath, string outputPath)
    {
        var oldBytes = File.ReadAllBytes(oldFilePath);
        var newBytes = File.ReadAllBytes(newFilePath);
        WriteAtomically(outputPath, output => BinaryPatch.Create(oldBytes, newBytes, output));
    }

    private static void ApplyPatch(string oldFilePath, string patchPath, string outputPath)
    {
        WriteAtomically(outputPath, output =>
        {
            using var oldFile = File.OpenRead(oldFilePath);
            BinaryPatch.Apply(oldFile, () => File.OpenRead(patchPath), output);
        });
    }

    private static void WriteAtomically(string outputPath, Action<FileStream> write)
    {
        var temporaryPath = outputPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       1024 * 1024,
                       FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                write(output);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void RequireInputFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Input file was not found.", path);
    }
}
