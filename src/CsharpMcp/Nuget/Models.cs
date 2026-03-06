namespace CsharpMcp.Nuget;

public record PackageSummary(string Id, string[] Versions);

public record PackageInfo(
    string Id,
    string Version,
    string? Description,
    string? Authors,
    string? ProjectUrl,
    string? License,
    DependencyGroup[] DependencyGroups,
    string[] Files
);

public record DependencyGroup(string? TargetFramework, Dependency[] Dependencies);

public record Dependency(string Id, string? VersionRange);

public record SearchResult(
    string Id,
    string Version,
    string? Description,
    string? Authors,
    long TotalDownloads,
    bool FromCache
);

public record AssemblyInfo(string Name, string TargetFramework, TypeDef[] Types);

public record TypeDef(
    string Name,
    string Kind, // class, interface, struct, enum, delegate
    string? BaseType,
    string[] Interfaces,
    MethodDef[] Methods,
    PropertyDef[] Properties,
    FieldDef[] Fields
);

public record MethodDef(string Name, string ReturnType, string[] Parameters, bool IsStatic, bool IsPublic);

public record PropertyDef(string Name, string Type, bool CanRead, bool CanWrite, bool IsStatic);

public record FieldDef(string Name, string Type, bool IsStatic, bool IsPublic);

public record DocEntry(string MemberId, string? Summary, string? Remarks, ParamDoc[] Params, string? Returns);

public record ParamDoc(string Name, string? Description);
