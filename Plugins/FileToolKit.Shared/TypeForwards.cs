using System.Runtime.CompilerServices;

// Compatibility for existing plugin binaries compiled against FileToolKit.Shared.
[assembly: TypeForwardedTo(typeof(FileToolKit.Shared.FileToolBase))]
