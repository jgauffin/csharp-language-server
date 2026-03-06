using CsharpMcp.CodeAnalysis;
using CsharpMcp.CodeAnalysis.Tools;
using Shouldly;

namespace CsharpMcp.Tests.Tools;

public class TypeIntelligenceToolsTests : WorkspaceFixture
{
    [Fact]
    public async Task GetHover_OnCalculatorAdd_ReturnsMethodInfo()
    {
        var pos = new Position(FilePath("LibA", "Calculator.cs"), Line: 7, Column: 16);

        var result = await TypeIntelligenceTools.GetHoverAsync(Workspace.Solution, pos);

        result.ShouldNotBeNull();
        result.Kind.ShouldBe("Method");
        result.ReturnType.ShouldBe("int");
    }

    [Fact]
    public async Task GetHover_OnDocumentedMethod_ReturnsDocumentation()
    {
        // AnimalBase.Greet has XML doc
        var pos = new Position(FilePath("LibA", "Animal.cs"), Line: 26, Column: 19);

        var result = await TypeIntelligenceTools.GetHoverAsync(Workspace.Solution, pos);

        result.ShouldNotBeNull();
        result.Documentation.ShouldNotBeNullOrWhiteSpace();
        result.Documentation!.ShouldContain("greeting");
    }

    [Fact]
    public async Task GetSignature_AtCallSite_ReturnsParameterDetails()
    {
        // App/Program.cs line 15: calc.Add(1, 2)
        var pos = new Position(FilePath("App", "Program.cs"), Line: 15, Column: 29);

        var result = await TypeIntelligenceTools.GetSignatureAsync(Workspace.Solution, pos);

        result.ShouldNotBeNull();
        result.Parameters.Count.ShouldBe(2);
        result.Parameters[0].Name.ShouldBe("a");
        result.Parameters[1].Name.ShouldBe("b");
        result.Parameters[0].Type.ShouldBe("int");
    }
}
