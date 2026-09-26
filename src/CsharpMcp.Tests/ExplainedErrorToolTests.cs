using CsharpMcp.CodeAnalysis;
using Shouldly;

namespace CsharpMcp.Tests;

public class ExplainedErrorToolTests
{
    [Fact]
    public void Wrapping_keeps_every_tool_the_type_declares()
    {
        var tools = ExplainedErrorTool.For<CsharpTools>().ToList();

        tools.Select(t => t.ProtocolTool.Name).ShouldContain("find");
        tools.Select(t => t.ProtocolTool.Name).ShouldContain("get_references");
    }

    [Fact]
    public void Find_takes_the_same_pattern_parameter_as_the_typescript_server()
    {
        var find = ExplainedErrorTool.For<CsharpTools>().Single(t => t.ProtocolTool.Name == "find");

        var schema = find.ProtocolTool.InputSchema;
        schema.GetProperty("properties").TryGetProperty("query", out _).ShouldBeTrue();
        schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldContain("query");
    }
}
