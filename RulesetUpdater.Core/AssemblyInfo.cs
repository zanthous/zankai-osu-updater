using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace RulesetUpdater;

public sealed record AssemblyInfo(string Name, string InformationalVersion, bool OfficialBuild)
{
    public Version Version { get; init; } = new(0, 0);
    public string TargetFramework { get; init; } = "";
    public string? RulesetApi { get; init; }
    public IReadOnlyDictionary<string, Version> References { get; init; } = new Dictionary<string, Version>();
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
        string framework = "";
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
            if (type.IsNil) continue;
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
            if (name is not ("System.Reflection.AssemblyInformationalVersionAttribute" or "System.Runtime.Versioning.TargetFrameworkAttribute")) continue;
            BlobReader blob = reader.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1) continue;
            string value = blob.ReadSerializedString() ?? "";
            if (name == "System.Runtime.Versioning.TargetFrameworkAttribute") framework = value;
            else info = value;
        }
        string? api = null;
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            if (reader.GetString(type.Namespace) == "osu.Game.Rulesets" && reader.GetString(type.Name) == "Ruleset")
            {
                foreach (var fieldHandle in type.GetFields())
                {
                    var field = reader.GetFieldDefinition(fieldHandle);
                    if (reader.GetString(field.Name) != "CURRENT_RULESET_API_VERSION" || field.GetDefaultValue().IsNil) continue;
                    var constant = reader.GetConstant(field.GetDefaultValue());
                    if (constant.TypeCode == ConstantTypeCode.String) api = Encoding.Unicode.GetString(reader.GetBlobBytes(constant.Value));
                }
            }
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (reader.GetString(method.Name) != "get_RulesetAPIVersionSupported" || method.RelativeVirtualAddress == 0) continue;
                byte[]? il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
                // Normal release builds compile this property to `ldstr <constant>; ret`.
                // More complex getters are unknown: do not execute arbitrary ruleset code to discover it.
                if (il is { Length: 6 } && il[0] == 0x72 && il[5] == 0x2a)
                    api = reader.GetUserString(MetadataTokens.UserStringHandle(BitConverter.ToInt32(il, 1) & 0x00ffffff));
            }
        }
        return new AssemblyInfo(reader.GetString(definition.Name), info, official)
        {
            Version = definition.Version, TargetFramework = framework, RulesetApi = api,
            References = reader.AssemblyReferences.Select(reader.GetAssemblyReference)
                               .ToDictionary(r => reader.GetString(r.Name), r => r.Version),
        };
    }
}
