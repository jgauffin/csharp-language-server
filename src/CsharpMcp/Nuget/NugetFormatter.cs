using System.Text;

namespace CsharpMcp.Nuget;

public static class NugetFormatter
{
    public static string Format(List<SearchResult> results)
    {
        if (results.Count == 0) return "No results found.";
        var sb = new StringBuilder();
        foreach (var r in results)
        {
            sb.Append(r.Id).Append(' ').Append(r.Version);
            if (r.TotalDownloads > 0) sb.Append(" (downloads: ").Append(r.TotalDownloads).Append(')');
            if (r.FromCache) sb.Append(" [cached]");
            if (r.Description != null) sb.Append(" - ").Append(r.Description);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    public static string Format(List<PackageSummary> packages)
    {
        if (packages.Count == 0) return "No cached packages found.";
        var sb = new StringBuilder();
        foreach (var p in packages)
            sb.Append(p.Id).Append(": ").AppendLine(string.Join(", ", p.Versions));
        return sb.ToString().TrimEnd();
    }

    public static string Format(PackageInfo info)
    {
        var sb = new StringBuilder();
        sb.Append("Package: ").Append(info.Id).Append(' ').AppendLine(info.Version);
        if (info.Description != null) sb.Append("Description: ").AppendLine(info.Description);
        if (info.Authors != null) sb.Append("Authors: ").AppendLine(info.Authors);
        if (info.ProjectUrl != null) sb.Append("ProjectUrl: ").AppendLine(info.ProjectUrl);
        if (info.License != null) sb.Append("License: ").AppendLine(info.License);

        if (info.DependencyGroups.Length > 0)
        {
            sb.AppendLine().AppendLine("Dependencies:");
            foreach (var g in info.DependencyGroups)
            {
                sb.Append("  ").Append(g.TargetFramework ?? "all").Append(": ");
                if (g.Dependencies.Length == 0)
                    sb.AppendLine("(none)");
                else
                    sb.AppendLine(string.Join(", ", g.Dependencies.Select(d =>
                        d.VersionRange != null ? $"{d.Id} {d.VersionRange}" : d.Id)));
            }
        }

        if (info.Files.Length > 0)
        {
            sb.AppendLine().AppendLine("Files:");
            foreach (var f in info.Files)
                sb.Append("  ").AppendLine(f);
        }

        return sb.ToString().TrimEnd();
    }

    public static string FormatAssemblyList(List<CacheReader.AssemblyEntry> entries)
    {
        if (entries.Count == 0) return "No assemblies found.";
        var sb = new StringBuilder();
        foreach (var e in entries)
            sb.Append(e.Name).Append(" (").Append(e.TargetFramework).Append(") ").AppendLine(e.RelativePath);
        return sb.ToString().TrimEnd();
    }

    public static string Format(List<AssemblyInfo> assemblies)
    {
        if (assemblies.Count == 0) return "No assemblies found.";
        var sb = new StringBuilder();
        foreach (var asm in assemblies)
        {
            sb.Append("Assembly: ").Append(asm.Name).Append(" (").Append(asm.TargetFramework).AppendLine(")");
            sb.AppendLine();
            foreach (var t in asm.Types)
            {
                FormatType(sb, t);
                sb.AppendLine();
            }
        }
        return sb.ToString().TrimEnd();
    }

    static void FormatType(StringBuilder sb, TypeDef t)
    {
        // e.g. "class MyLib.MyClass : BaseClass, IFoo, IBar"
        sb.Append(t.Kind).Append(' ').Append(t.Name);
        var supertypes = new List<string>();
        if (t.BaseType != null) supertypes.Add(t.BaseType);
        supertypes.AddRange(t.Interfaces);
        if (supertypes.Count > 0)
            sb.Append(" : ").Append(string.Join(", ", supertypes));
        sb.AppendLine();

        foreach (var f in t.Fields)
        {
            sb.Append("  ");
            if (f.IsStatic) sb.Append("static ");
            sb.Append(f.Type).Append(' ').AppendLine(f.Name);
        }

        foreach (var p in t.Properties)
        {
            sb.Append("  ");
            if (p.IsStatic) sb.Append("static ");
            sb.Append(p.Type).Append(' ').Append(p.Name).Append(" { ");
            if (p.CanRead) sb.Append("get; ");
            if (p.CanWrite) sb.Append("set; ");
            sb.AppendLine("}");
        }

        foreach (var m in t.Methods)
        {
            sb.Append("  ");
            if (m.IsStatic) sb.Append("static ");
            sb.Append(m.ReturnType).Append(' ').Append(m.Name);
            sb.Append('(').Append(string.Join(", ", m.Parameters)).AppendLine(")");
        }
    }

    public static string Format(List<DocEntry> docs)
    {
        if (docs.Count == 0) return "No documentation found.";
        var sb = new StringBuilder();
        foreach (var d in docs)
        {
            sb.AppendLine(d.MemberId);
            if (d.Summary != null) sb.Append("  Summary: ").AppendLine(d.Summary);
            if (d.Remarks != null) sb.Append("  Remarks: ").AppendLine(d.Remarks);
            if (d.Params.Length > 0)
            {
                sb.AppendLine("  Params:");
                foreach (var p in d.Params)
                    sb.Append("    ").Append(p.Name).Append(": ").AppendLine(p.Description ?? "");
            }
            if (d.Returns != null) sb.Append("  Returns: ").AppendLine(d.Returns);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}
