using ICSharpCode.Decompiler.Metadata;
using System.Reflection.PortableExecutable;

namespace RoslynAssemblyAnalyzerMcp;

internal static class AssemblyImageUtilities
{
    public static byte[] ReadFileImage(string path)
    {
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.SequentialScan
        });

        if (stream.Length > int.MaxValue)
        {
            throw new InvalidOperationException($"程序集文件过大，无法载入内存: {path}");
        }

        var image = new byte[(int)stream.Length];
        stream.ReadExactly(image);
        return image;
    }

    public static PEFile OpenPeFile(string assemblyPath, byte[]? assemblyImage)
    {
        if (assemblyImage is null)
        {
            return new PEFile(assemblyPath);
        }

        var stream = new MemoryStream(assemblyImage, writable: false);
        try
        {
            return new PEFile(assemblyPath, stream, PEStreamOptions.PrefetchEntireImage);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
}
