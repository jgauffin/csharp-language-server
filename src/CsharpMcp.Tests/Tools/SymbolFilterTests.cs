using CsharpMcp.CodeAnalysis.Tools;
using Shouldly;

namespace CsharpMcp.Tests.Tools;

public class SymbolFilterTests : WorkspaceFixture
{
    [Fact]
    public async Task Find_Accessibility_KeepsOnlySymbolsWithThatVisibility()
    {
        var results = await SemanticSearchTools.FindAsync(
            Workspace.Solution, "_calculator",
            filter: new SemanticSearchTools.FindFilter(Accessibility: "private"));

        results.ShouldNotBeEmpty();
        results.ShouldAllBe(r => r.Name == "_calculator");
    }

    [Fact]
    public async Task Find_Accessibility_DropsSymbolsWithOtherVisibility()
    {
        var results = await SemanticSearchTools.FindAsync(
            Workspace.Solution, "_calculator",
            filter: new SemanticSearchTools.FindFilter(Accessibility: "public"));

        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task Find_UnknownAccessibility_ErrorNamesTheAcceptedValues()
    {
        var error = await Should.ThrowAsync<ArgumentException>(() =>
            SemanticSearchTools.FindAsync(
                Workspace.Solution, "Calculator",
                filter: new SemanticSearchTools.FindFilter(Accessibility: "package")));

        error.Message.ShouldContain("public");
        error.Message.ShouldContain("protected");
    }

    [Fact]
    public async Task Find_NamespacePattern_LimitsResultsToThatNamespace()
    {
        var results = await SemanticSearchTools.FindAsync(
            Workspace.Solution, "",
            filter: new SemanticSearchTools.FindFilter(NamespacePattern: "LibB"));

        results.ShouldNotBeEmpty();
        results.ShouldAllBe(r => r.FilePath.Contains("LibB"));
    }

    [Fact]
    public async Task Find_FilePattern_LimitsResultsToMatchingFiles()
    {
        var results = await SemanticSearchTools.FindAsync(
            Workspace.Solution, "",
            filter: new SemanticSearchTools.FindFilter(FilePattern: "*Calculator.cs"));

        results.ShouldNotBeEmpty();
        results.ShouldAllBe(r => r.FilePath.EndsWith("Calculator.cs"));
    }

    [Fact]
    public async Task Find_HasAttribute_KeepsOnlySymbolsCarryingIt()
    {
        var results = await SemanticSearchTools.FindAsync(
            Workspace.Solution, "",
            filter: new SemanticSearchTools.FindFilter(HasAttribute: "Serializable"));

        results.ShouldNotBeEmpty();
        results.ShouldAllBe(r => r.Name == "DogRegistry");
    }

    [Theory]
    [InlineData(@"C:\src\App.Tests\OrderTests.cs")]
    [InlineData("/src/app/specs/order_spec.cs")]
    public void IsTestPath_RecognizesTestAndSpecPaths(string path) =>
        SymbolFilters.IsTestPath(path).ShouldBeTrue();

    [Fact]
    public void IsTestPath_PlainSourcePath_IsNotATest() =>
        SymbolFilters.IsTestPath(@"C:\src\App\Orders\OrderService.cs").ShouldBeFalse();

    [Theory]
    [InlineData(@"C:\src\App\obj\Debug\net10.0\App.AssemblyInfo.cs")]
    [InlineData(@"C:\src\App\Resources.Designer.cs")]
    [InlineData("/src/app/Protos/Order.g.cs")]
    [InlineData("/src/app/Views/Index.generated.cs")]
    public void IsGeneratedPath_RecognizesBuildOutputAndToolOutput(string path) =>
        SymbolFilters.IsGeneratedPath(path).ShouldBeTrue();

    [Fact]
    public void IsGeneratedPath_HandwrittenSource_IsNotGenerated() =>
        SymbolFilters.IsGeneratedPath(@"C:\src\App\Orders\OrderService.cs").ShouldBeFalse();
}
