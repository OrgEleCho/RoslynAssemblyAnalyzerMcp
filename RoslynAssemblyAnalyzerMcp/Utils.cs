using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace RoslynAssemblyAnalyzerMcp;

public static class Utils
{
    public static bool IsDotnetAssembly(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        try
        {
            using var fileStream = new FileStream(filePath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete
            });
            using PEReader peReader = new(fileStream);
            if (!peReader.HasMetadata)
            {
                return false;
            }

            return peReader.GetMetadataReader().IsAssembly;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool IsDotnetAssembly(byte[] image)
    {
        try
        {
            using var stream = new MemoryStream(image, writable: false);
            using PEReader peReader = new(stream);
            if (!peReader.HasMetadata)
            {
                return false;
            }

            return peReader.GetMetadataReader().IsAssembly;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
