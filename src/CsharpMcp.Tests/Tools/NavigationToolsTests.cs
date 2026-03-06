using CsharpMcp.CodeAnalysis;
using CsharpMcp.CodeAnalysis.Tools;
using Shouldly;

namespace CsharpMcp.Tests.Tools;

public class NavigationToolsTests : WorkspaceFixture
{
    [Fact]
    public async Task GetDefinition_FromUsageSite_ReturnsDeclarationInLibA()
    {
        // App/Program.cs line 11: dog.Speak() — resolves to override in Dog.cs
        var pos = new Position(FilePath("App", "Program.cs"), Line: 11, Column: 25);

        var result = await NavigationTools.GetDefinitionAsync(Workspace.Solution, pos);

        result.ShouldNotBeNull();
        result.FilePath.ShouldEndWith("Dog.cs");
    }

    [Fact]
    public async Task GetDefinition_OnNewDog_ReturnsLibBConstructor()
    {
        // App/Program.cs line 10: new Dog("Rex")
        var pos = new Position(FilePath("App", "Program.cs"), Line: 10, Column: 22);

        var result = await NavigationTools.GetDefinitionAsync(Workspace.Solution, pos);

        result.ShouldNotBeNull();
        result.FilePath.ShouldEndWith("Dog.cs");
    }

    [Fact]
    public async Task GetReferences_Calculator_Add_FindsUsagesAcrossProjects()
    {
        // LibA/Calculator.cs line 7: declaration of Add
        var pos = new Position(FilePath("LibA", "Calculator.cs"), Line: 7, Column: 16);

        var refs = await NavigationTools.GetReferencesAsync(Workspace.Solution, pos);

        refs.ShouldNotBeEmpty();
        // Should find usage in App/Program.cs and LibB/Dog.cs
        var files = refs.Select(r => Path.GetFileName(r.Location.FilePath)).Distinct().ToList();
        files.ShouldContain("Program.cs");
        files.ShouldContain("Dog.cs");
    }

    [Fact]
    public async Task GetImplementations_IAnimal_Speak_FindsDogInLibB()
    {
        // LibA/Animal.cs: IAnimal.Speak at line 8
        var pos = new Position(FilePath("LibA", "Animal.cs"), Line: 8, Column: 12);

        var impls = await NavigationTools.GetImplementationsAsync(Workspace.Solution, pos);

        impls.ShouldNotBeEmpty();
        impls.ShouldContain(l => l.FilePath.EndsWith("Dog.cs"));
    }

    [Fact]
    public async Task GetCallHierarchy_Calculator_Add_HasCallersAndCallees()
    {
        // LibA/Calculator.cs: Add method
        var pos = new Position(FilePath("LibA", "Calculator.cs"), Line: 7, Column: 16);

        var hierarchy = await NavigationTools.GetCallHierarchyAsync(Workspace.Solution, pos);

        hierarchy.ShouldNotBeEmpty();
        hierarchy.ShouldContain(c => c.Direction == NavigationTools.CallDirection.Caller);
    }

    [Fact]
    public async Task GetTypeHierarchy_AnimalBase_ShowsInterfaceAndDerived()
    {
        // LibA/Animal.cs: AnimalBase class
        var pos = new Position(FilePath("LibA", "Animal.cs"), Line: 14, Column: 24);

        var result = await NavigationTools.GetTypeHierarchyAsync(Workspace.Solution, pos);

        result.ShouldNotBeNull();
        result.Interfaces.ShouldContain(i => i.Contains("IAnimal"));
        result.DerivedTypes.ShouldContain(d => d.Contains("Dog"));
    }
}
