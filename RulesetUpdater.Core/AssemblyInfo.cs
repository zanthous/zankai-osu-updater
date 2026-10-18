using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace RulesetUpdater;

public sealed record AssemblyInfo(string Name, string InformationalVersion, bool OfficialBuild)
{
    public bool DevelopmentBuild => InformationalVersion.Contains("dev", StringComparison.OrdinalIgnoreCase)
                                    || InformationalVersion.Contains("debug", StringComparison.OrdinalIgnoreCase)
                                    || InformationalVersion.StartsWith("0.0.0", StringComparison.Ordinal);

    // Inspect metadata without executing downloaded or incompatible assemblies.
    public static AssemblyInfo Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) throw new InvalidDataException("File is not a managed DLL.");
        MetadataReader reader = pe.GetMetadataReader();
        if (!reader.IsAssembly) throw new InvalidDataException("File is not an assembly.");
        AssemblyDefinition definition = reader.GetAssemblyDefinition();
        string info = "";
        bool official = false;
        foreach (var handle in definition.GetCustomAttributes())
        {
            CustomAttribute attribute = reader.GetCustomAttribute(handle);
            EntityHandle type = attribute.Constructor.Kind switch
            {
                HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
                HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
                _ => default,
            };
            string name;
            if (type.Kind == HandleKind.TypeReference)
            {
                TypeReference reference = reader.GetTypeReference((TypeReferenceHandle)type);
                name = reader.GetString(reference.Namespace) + "." + reader.GetString(reference.Name);
            }
            else if (type.Kind == HandleKind.TypeDefinition)
            {
                TypeDefinition reference = reader.GetTypeDefinition((TypeDefinitionHandle)type);
                name = reader.GetString(reference.Namespace) + "." + reader.GetString(reference.Name);
            }
            else continue;

            if (name == "osu.Game.Utils.OfficialBuildAttribute") official = true;
            if (name != "System.Reflection.AssemblyInformationalVersionAttribute") continue;
            BlobReader blob = reader.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() == 1) info = blob.ReadSerializedString() ?? "";
        }
        return new AssemblyInfo(reader.GetString(definition.Name), info, official);
    }
}
