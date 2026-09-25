using CsharpMcp.CodeAnalysis;
using CsharpMcp.CodeAnalysis.Tools;
using Shouldly;

namespace CsharpMcp.Tests.Tools;

public class ReferenceFilterTests : WorkspaceFixture
{
    // LibA/Calculator.cs line 7: declaration of Add
    private Position AddDeclaration => new(FilePath("LibA", "Calculator.cs"), Line: 7, Column: 16);

    // LibA/Vulnerabilities.cs line 88: the local "sum" in WithGoto, written on 92 and read on 96
    private Position SumDeclaration => new(FilePath("LibA", "Vulnerabilities.cs"), Line: 88, Column: 13);

    [Fact]
    public async Task GetReferences_CallSites_AreTaggedAsInvocations()
    {
        var refs = await NavigationTools.GetReferencesAsync(Workspace.Solution, AddDeclaration);

        refs.ShouldNotBeEmpty();
        refs.ShouldAllBe(r => r.Kind == ReferenceKind.Invocation);
    }

    [Fact]
    public async Task GetReferences_UsageWrite_KeepsAssignmentsAndDropsReads()
    {
        var refs = await NavigationTools.GetReferencesAsync(
            Workspace.Solution, SumDeclaration,
            filter: new NavigationTools.ReferenceFilter(Usage: "write"));

        refs.Count.ShouldBe(1);
        refs[0].Location.Line.ShouldBe(92);
    }

    [Fact]
    public async Task GetReferences_UsageRead_KeepsReadsAndDropsAssignments()
    {
        var refs = await NavigationTools.GetReferencesAsync(
            Workspace.Solution, SumDeclaration,
            filter: new NavigationTools.ReferenceFilter(Usage: "read"));

        refs.Count.ShouldBe(1);
        refs[0].Location.Line.ShouldBe(96);
    }

    [Fact]
    public async Task GetReferences_UnknownUsage_ErrorNamesTheAcceptedValues()
    {
        var error = await Should.ThrowAsync<ArgumentException>(() =>
            NavigationTools.GetReferencesAsync(
                Workspace.Solution, AddDeclaration,
                filter: new NavigationTools.ReferenceFilter(Usage: "mutation")));

        error.Message.ShouldContain("invocation");
        error.Message.ShouldContain("write");
    }

    [Fact]
    public async Task GetReferences_EveryHit_NamesTheMemberItSitsIn()
    {
        var refs = await NavigationTools.GetReferencesAsync(Workspace.Solution, AddDeclaration);

        var members = refs.Select(r => r.EnclosingMember).ToList();
        members.ShouldContain("Program.Main");
        members.ShouldContain("DogMath.AddLegs");
    }

    [Fact]
    public async Task GetReferences_InEnclosingMember_DropsHitsInOtherMembers()
    {
        var refs = await NavigationTools.GetReferencesAsync(
            Workspace.Solution, AddDeclaration,
            filter: new NavigationTools.ReferenceFilter(InEnclosingMember: "AddLegs"));

        refs.ShouldNotBeEmpty();
        refs.ShouldAllBe(r => r.EnclosingMember == "DogMath.AddLegs");
    }

    [Fact]
    public async Task GetReferences_EnclosingAlsoCalls_KeepsOnlyMembersMakingBothCalls()
    {
        // Program.Main calls both Add and Multiply; DogMath.AddLegs calls only Add.
        var refs = await NavigationTools.GetReferencesAsync(
            Workspace.Solution, AddDeclaration,
            filter: new NavigationTools.ReferenceFilter(EnclosingAlsoCalls: "Multiply"));

        refs.ShouldNotBeEmpty();
        refs.ShouldAllBe(r => r.EnclosingMember == "Program.Main");
    }

    [Fact]
    public async Task GetReferences_EnclosingAlsoCalls_MatchesConstructedTypesNotJustMethods()
    {
        var refs = await NavigationTools.GetReferencesAsync(
            Workspace.Solution, AddDeclaration,
            filter: new NavigationTools.ReferenceFilter(EnclosingAlsoCalls: "Dog"));

        refs.ShouldNotBeEmpty();
        refs.ShouldAllBe(r => r.EnclosingMember == "Program.Main");
    }

    [Fact]
    public async Task GetReferences_InProject_DropsHitsOutsideThatProject()
    {
        var refs = await NavigationTools.GetReferencesAsync(
            Workspace.Solution, AddDeclaration,
            filter: new NavigationTools.ReferenceFilter(InProject: "LibB"));

        refs.ShouldNotBeEmpty();
        refs.ShouldAllBe(r => r.Location.FilePath.EndsWith("Dog.cs"));
    }

    [Fact]
    public async Task GetReferences_FilePattern_DropsHitsInOtherFiles()
    {
        var refs = await NavigationTools.GetReferencesAsync(
            Workspace.Solution, AddDeclaration,
            filter: new NavigationTools.ReferenceFilter(FilePattern: "*Program.cs"));

        refs.ShouldNotBeEmpty();
        refs.ShouldAllBe(r => r.Location.FilePath.EndsWith("Program.cs"));
    }

    [Fact]
    public async Task GetReferences_SeveralFilters_NarrowRatherThanWiden()
    {
        var refs = await NavigationTools.GetReferencesAsync(
            Workspace.Solution, AddDeclaration,
            filter: new NavigationTools.ReferenceFilter(
                Usage: "invocation", InProject: "LibB", InEnclosingMember: "AddLegs"));

        refs.Count.ShouldBe(1);
        refs[0].Location.FilePath.ShouldEndWith("Dog.cs");
    }
}
