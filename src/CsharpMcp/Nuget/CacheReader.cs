using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Xml.Linq;
using NuGet.Packaging;

namespace CsharpMcp.Nuget;

public static class CacheReader
{
    static string CachePath => Environment.GetEnvironmentVariable("NUGET_CACHE_PATH")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    public static List<PackageSummary> ListCached()
    {
        var root = new DirectoryInfo(CachePath);
        if (!root.Exists) return [];

        return root.GetDirectories()
            .Select(pkgDir => new PackageSummary(
                pkgDir.Name,
                pkgDir.GetDirectories().Select(v => v.Name).OrderDescending().ToArray()
            ))
            .OrderBy(p => p.Id)
            .ToList();
    }

    public static PackageInfo? GetPackageInfo(string id, string? version, string? fileFilter = null)
    {
        var versionDir = ResolveVersionDir(id, version);
        if (versionDir == null) return null;

        var nuspecPath = versionDir.GetFiles("*.nuspec").FirstOrDefault();
        if (nuspecPath == null) return null;

        using var stream = File.OpenRead(nuspecPath.FullName);
        var reader = new NuspecReader(stream);
        var meta = reader.GetMetadata().ToDictionary(kv => kv.Key, kv => kv.Value);

        var deps = reader.GetDependencyGroups().Select(g => new DependencyGroup(
            g.TargetFramework.IsSpecificFramework ? g.TargetFramework.GetShortFolderName() : null,
            g.Packages.Select(p => new Dependency(p.Id, p.VersionRange?.ToString())).ToArray()
        )).ToArray();

        var allFiles = versionDir.GetFiles("*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(versionDir.FullName, f.FullName));

        if (fileFilter != null)
        {
            // Convert glob pattern to regex: * matches anything except path separators, ** matches everything
            var escaped = System.Text.RegularExpressions.Regex.Escape(fileFilter.Replace('\\', '/'));
            var regexPattern = "^" + escaped
                .Replace(@"\*\*", "§DOUBLESTAR§")
                .Replace(@"\*", @"[^/]*")
                .Replace(@"\?", @"[^/]")
                .Replace("§DOUBLESTAR§", ".*")
                + "$";
            var regex = new System.Text.RegularExpressions.Regex(regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            allFiles = allFiles.Where(f => regex.IsMatch(f.Replace('\\', '/')));
        }

        var files = allFiles.OrderBy(f => f).ToArray();

        meta.TryGetValue("description", out var desc);
        meta.TryGetValue("authors", out var authors);
        meta.TryGetValue("projectUrl", out var url);
        meta.TryGetValue("license", out var license);

        return new PackageInfo(
            reader.GetId(),
            reader.GetVersion().ToString(),
            desc, authors, url, license,
            deps, files
        );
    }

    public record AssemblyEntry(string Name, string RelativePath, string TargetFramework);

    public static List<AssemblyEntry> ListAssemblies(string id, string? version)
    {
        var versionDir = ResolveVersionDir(id, version);
        if (versionDir == null) return [];

        return versionDir.GetFiles("*.dll", SearchOption.AllDirectories)
            .Select(f =>
            {
                var rel = Path.GetRelativePath(versionDir.FullName, f.FullName);
                var parts = rel.Split(Path.DirectorySeparatorChar);
                var tfm = parts.Length >= 2 ? parts[^2] : "unknown";
                return new AssemblyEntry(f.Name, rel, tfm);
            })
            .OrderBy(a => a.Name)
            .ToList();
    }

    public static List<AssemblyInfo> GetAssemblyTypes(string id, string? version, string? assemblyFilter, string? typeFilter = null)
    {
        var versionDir = ResolveVersionDir(id, version);
        if (versionDir == null) return [];

        var dlls = versionDir.GetFiles("*.dll", SearchOption.AllDirectories)
            .Where(f => assemblyFilter == null
                || f.Name.Equals(assemblyFilter, StringComparison.OrdinalIgnoreCase)
                || f.Name.Equals(assemblyFilter + ".dll", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var assemblies = dlls.Select(dll => ReadAssemblyTypes(dll, versionDir.FullName))
            .OfType<AssemblyInfo>()
            .ToList();

        if (typeFilter != null)
        {
            assemblies = assemblies
                .Select(a => a with {
                    Types = a.Types
                        .Where(t => t.Name.Contains(typeFilter, StringComparison.OrdinalIgnoreCase))
                        .ToArray()
                })
                .Where(a => a.Types.Length > 0)
                .ToList();
        }

        return assemblies;
    }

    public static List<DocEntry> GetAssemblyDocs(string id, string? version, string? assemblyFilter, string? typeFilter)
    {
        var versionDir = ResolveVersionDir(id, version);
        if (versionDir == null) return [];

        var xmlFiles = versionDir.GetFiles("*.xml", SearchOption.AllDirectories)
            .Where(f => assemblyFilter == null
                || f.Name.Equals(assemblyFilter, StringComparison.OrdinalIgnoreCase)
                || f.Name.Equals(assemblyFilter + ".xml", StringComparison.OrdinalIgnoreCase)
                || f.Name.Equals(Path.GetFileNameWithoutExtension(assemblyFilter) + ".xml", StringComparison.OrdinalIgnoreCase));

        var results = new List<DocEntry>();
        foreach (var xml in xmlFiles)
            results.AddRange(ParseXmlDocs(xml.FullName, typeFilter));

        return results;
    }

    public record ResolveResult(DirectoryInfo? Dir, string? Error);

    public static ResolveResult ResolveVersionDirWithError(string id, string? version)
    {
        var pkgDir = new DirectoryInfo(Path.Combine(CachePath, id.ToLowerInvariant()));
        if (!pkgDir.Exists)
            return new(null, $"Package '{id}' not found in local NuGet cache. Use nuget_search to find and download it first.");

        if (version != null)
        {
            var vDir = new DirectoryInfo(Path.Combine(pkgDir.FullName, version.ToLowerInvariant()));
            if (!vDir.Exists)
            {
                var available = pkgDir.GetDirectories().Select(d => d.Name).OrderDescending().ToArray();
                return new(null, $"Version '{version}' not found for '{id}'. Available versions: {string.Join(", ", available)}");
            }
            return new(vDir, null);
        }

        // pick highest version
        var best = pkgDir.GetDirectories()
            .OrderByDescending(d => d.Name)
            .FirstOrDefault();
        return best != null
            ? new(best, null)
            : new(null, $"No versions found in cache for '{id}'.");
    }

    static DirectoryInfo? ResolveVersionDir(string id, string? version)
        => ResolveVersionDirWithError(id, version).Dir;

    static AssemblyInfo? ReadAssemblyTypes(FileInfo dll, string packageRoot)
    {
        try
        {
            using var stream = File.OpenRead(dll.FullName);
            using var peReader = new System.Reflection.PortableExecutable.PEReader(stream);
            if (!peReader.HasMetadata) return null;

            var metadata = peReader.GetMetadataReader();
            var asmDef = metadata.GetAssemblyDefinition();
            var asmName = metadata.GetString(asmDef.Name);

            // infer TFM from folder structure: lib/<tfm>/Assembly.dll
            var rel = Path.GetRelativePath(packageRoot, dll.DirectoryName!);
            var tfm = rel.Split(Path.DirectorySeparatorChar).Length >= 2
                ? rel.Split(Path.DirectorySeparatorChar)[^1]
                : "unknown";

            var types = new List<TypeDef>();
            foreach (var typeHandle in metadata.TypeDefinitions)
            {
                var typeDef = metadata.GetTypeDefinition(typeHandle);
                var attrs = typeDef.Attributes;

                // public types only
                var visibility = attrs & System.Reflection.TypeAttributes.VisibilityMask;
                if (visibility != System.Reflection.TypeAttributes.Public &&
                    visibility != System.Reflection.TypeAttributes.NestedPublic)
                    continue;

                var ns = metadata.GetString(typeDef.Namespace);
                var name = metadata.GetString(typeDef.Name);
                var fullName = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";

                var kind = ResolveKind(attrs, typeDef, metadata);
                var (baseType, interfaces) = ResolveBaseTypes(typeDef, metadata);

                var methods = new List<MethodDef>();
                var properties = new List<PropertyDef>();

                foreach (var mh in typeDef.GetMethods())
                {
                    var m = metadata.GetMethodDefinition(mh);
                    var mAttrs = m.Attributes;
                    if ((mAttrs & System.Reflection.MethodAttributes.Public) == 0) continue;
                    // skip property accessors
                    if ((mAttrs & System.Reflection.MethodAttributes.SpecialName) != 0) continue;

                    var mName = metadata.GetString(m.Name);
                    var isStatic = (mAttrs & System.Reflection.MethodAttributes.Static) != 0;
                    var decoded = m.DecodeSignature(new SignatureTypeProvider(metadata), null);
                    methods.Add(new MethodDef(
                        mName,
                        decoded.ReturnType,
                        decoded.ParameterTypes.ToArray(),
                        isStatic,
                        true
                    ));
                }

                foreach (var ph in typeDef.GetProperties())
                {
                    var p = metadata.GetPropertyDefinition(ph);
                    var pName = metadata.GetString(p.Name);
                    var acc = p.GetAccessors();
                    bool canRead = !acc.Getter.IsNil;
                    bool canWrite = !acc.Setter.IsNil;

                    string propType = "?";
                    try
                    {
                        var decoded = p.DecodeSignature(new SignatureTypeProvider(metadata), null);
                        propType = decoded.ReturnType;
                    }
                    catch { }

                    bool isStatic = false;
                    if (!acc.Getter.IsNil)
                    {
                        var getter = metadata.GetMethodDefinition(acc.Getter);
                        isStatic = (getter.Attributes & System.Reflection.MethodAttributes.Static) != 0;
                    }

                    properties.Add(new PropertyDef(pName, propType, canRead, canWrite, isStatic));
                }

                var fields = new List<FieldDef>();
                foreach (var fh in typeDef.GetFields())
                {
                    var f = metadata.GetFieldDefinition(fh);
                    if ((f.Attributes & System.Reflection.FieldAttributes.Public) == 0) continue;
                    if ((f.Attributes & System.Reflection.FieldAttributes.SpecialName) != 0) continue;
                    var fName = metadata.GetString(f.Name);
                    var isStatic = (f.Attributes & System.Reflection.FieldAttributes.Static) != 0;
                    string fType = "?";
                    try { fType = f.DecodeSignature(new SignatureTypeProvider(metadata), null); } catch { }
                    fields.Add(new FieldDef(fName, fType, isStatic, true));
                }

                types.Add(new TypeDef(fullName, kind, baseType, interfaces, methods.ToArray(), properties.ToArray(), fields.ToArray()));
            }

            return new AssemblyInfo(asmName, tfm, types.ToArray());
        }
        catch
        {
            return null;
        }
    }

    static string ResolveKind(System.Reflection.TypeAttributes attrs, TypeDefinition typeDef, MetadataReader metadata)
    {
        if ((attrs & System.Reflection.TypeAttributes.Interface) != 0) return "interface";
        if ((attrs & System.Reflection.TypeAttributes.Abstract) != 0 &&
            (attrs & System.Reflection.TypeAttributes.Sealed) != 0) return "static class";
        if ((attrs & System.Reflection.TypeAttributes.Abstract) != 0) return "abstract class";

        // check if enum: base type is System.Enum
        if (!typeDef.BaseType.IsNil)
        {
            var baseName = ResolveEntityName(typeDef.BaseType, metadata);
            if (baseName == "System.Enum") return "enum";
            if (baseName == "System.ValueType") return "struct";
            if (baseName == "System.MulticastDelegate") return "delegate";
        }

        return "class";
    }

    static (string? baseType, string[] interfaces) ResolveBaseTypes(TypeDefinition typeDef, MetadataReader metadata)
    {
        string? baseType = null;
        if (!typeDef.BaseType.IsNil)
        {
            var name = ResolveEntityName(typeDef.BaseType, metadata);
            if (name is not ("System.Object" or "System.ValueType" or "System.Enum" or "System.MulticastDelegate"))
                baseType = name;
        }

        var ifaces = typeDef.GetInterfaceImplementations()
            .Select(ih => ResolveEntityName(metadata.GetInterfaceImplementation(ih).Interface, metadata))
            .OfType<string>()
            .ToArray();

        return (baseType, ifaces);
    }

    static string? ResolveEntityName(EntityHandle handle, MetadataReader metadata)
    {
        try
        {
            if (handle.Kind == HandleKind.TypeDefinition)
            {
                var td = metadata.GetTypeDefinition((TypeDefinitionHandle)handle);
                var ns = metadata.GetString(td.Namespace);
                var name = metadata.GetString(td.Name);
                return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
            }
            if (handle.Kind == HandleKind.TypeReference)
            {
                var tr = metadata.GetTypeReference((TypeReferenceHandle)handle);
                var ns = metadata.GetString(tr.Namespace);
                var name = metadata.GetString(tr.Name);
                return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
            }
        }
        catch { }
        return null;
    }

    static List<DocEntry> ParseXmlDocs(string xmlPath, string? typeFilter)
    {
        var results = new List<DocEntry>();
        try
        {
            var doc = XDocument.Load(xmlPath);
            var members = doc.Descendants("member");
            foreach (var member in members)
            {
                var memberId = member.Attribute("name")?.Value ?? "";
                if (typeFilter != null && !memberId.Contains(typeFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                var summary = member.Element("summary")?.Value.Trim();
                var remarks = member.Element("remarks")?.Value.Trim();
                var returns = member.Element("returns")?.Value.Trim();
                var paramDocs = member.Elements("param")
                    .Select(p => new ParamDoc(p.Attribute("name")?.Value ?? "", p.Value.Trim()))
                    .ToArray();

                results.Add(new DocEntry(memberId, summary, remarks, paramDocs, returns));
            }
        }
        catch { }
        return results;
    }
}

// Minimal signature type provider to decode type names from metadata
sealed class SignatureTypeProvider : ISignatureTypeProvider<string, object?>
{
    readonly MetadataReader _metadata;
    public SignatureTypeProvider(MetadataReader metadata) => _metadata = metadata;

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Byte => "byte",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.Double => "double",
        PrimitiveTypeCode.Int16 => "short",
        PrimitiveTypeCode.Int32 => "int",
        PrimitiveTypeCode.Int64 => "long",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.SByte => "sbyte",
        PrimitiveTypeCode.Single => "float",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.UInt16 => "ushort",
        PrimitiveTypeCode.UInt32 => "uint",
        PrimitiveTypeCode.UInt64 => "ulong",
        PrimitiveTypeCode.Void => "void",
        _ => typeCode.ToString()
    };

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var td = reader.GetTypeDefinition(handle);
        var ns = reader.GetString(td.Namespace);
        var name = reader.GetString(td.Name);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var tr = reader.GetTypeReference(handle);
        var ns = reader.GetString(tr.Namespace);
        var name = reader.GetString(tr.Name);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[{new string(',', shape.Rank - 1)}]";
    public string GetByReferenceType(string elementType) => $"ref {elementType}";
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
        $"{genericType}<{string.Join(", ", typeArguments)}>";
    public string GetGenericMethodParameter(object? genericContext, int index) => $"T{index}";
    public string GetGenericTypeParameter(object? genericContext, int index) => $"T{index}";
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetPinnedType(string elementType) => elementType;
    public string GetPointerType(string elementType) => $"{elementType}*";
    public string GetSZArrayType(string elementType) => $"{elementType}[]";
    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
    {
        var spec = reader.GetTypeSpecification(handle);
        return spec.DecodeSignature(this, genericContext);
    }
    public string GetFunctionPointerType(MethodSignature<string> signature) => "delegate*";
}
