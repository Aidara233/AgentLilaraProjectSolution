using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace AgentCoreProcessor.Tool.Host;

/// <summary>只读取 PE/CLR 元数据，不加载或执行候选插件。</summary>
internal static class ManagedAssemblyInspector
{
    public static bool IsManagedAssembly(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new PEReader(stream);
            return reader.PEHeaders.CorHeader is not null
                && reader.HasMetadata && reader.GetMetadataReader().IsAssembly;
        }
        catch (BadImageFormatException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (ArgumentException) { return false; }
    }
}
