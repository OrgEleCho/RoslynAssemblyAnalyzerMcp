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
            using var fileStream = File.OpenRead(filePath);
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
}
