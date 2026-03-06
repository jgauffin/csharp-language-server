using CsharpMcp.CodeAnalysis.Tools;
using Shouldly;

namespace CsharpMcp.Tests.Tools;

public class CodeStructureToolsTests : WorkspaceFixture
{
    [Fact]
    public async Task GetSymbols_LibAAnimalFile_ReturnsAllDeclaredSymbols()
    {
        var symbols = await CodeStructureTools.GetSymbolsAsync(
            Workspace.Solution, FilePath("LibA", "Animal.cs"));

        symbols.ShouldNotBeEmpty();
        symbols.ShouldContain(s => s.Name == "IAnimal");
        symbols.ShouldContain(s => s.Name == "AnimalBase");
        symbols.ShouldContain(s => s.Name == "Speak");
        symbols.ShouldContain(s => s.Name == "Greet");
    }

    [Fact]
    public async Task GetOutline_LibAAnimalFile_ReturnsNestedStructure()
    {
        var outline = await CodeStructureTools.GetOutlineAsync(
            Workspace.Solution, FilePath("LibA", "Animal.cs"));

        outline.ShouldNotBeEmpty();
        // AnimalBase is nested under the file-scoped namespace node
        var allNodes = outline.Concat(outline.SelectMany(n => n.Children));
        var animalBase = allNodes.FirstOrDefault(n => n.Name == "AnimalBase");
        animalBase.ShouldNotBeNull();
        animalBase!.Children.ShouldContain(c => c.Name == "Greet");
    }

    [Fact]
    public async Task GetImports_AppProgram_ReturnsLibAAndLibBUsings()
    {
        var imports = await CodeStructureTools.GetImportsAsync(
            Workspace.Solution, FilePath("App", "Program.cs"));

        imports.ShouldNotBeEmpty();
        imports.ShouldContain(i => i.Name == "LibA");
        imports.ShouldContain(i => i.Name == "LibB");
    }
}
