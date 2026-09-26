using System.Text.Json;
using Shouldly;

namespace CsharpMcp.Tests;

public class ArgumentMismatchTests
{
    private static readonly JsonElement FindSchema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": { "query": {}, "kind": {}, "maxResults": {} },
          "required": ["query"]
        }
        """).RootElement;

    [Fact]
    public void Arguments_matching_the_schema_produce_no_message()
    {
        ArgumentMismatch.Describe(FindSchema, ["query", "kind"]).ShouldBeNull();
    }

    [Fact]
    public void Parameter_borrowed_from_a_sibling_server_is_named_along_with_the_accepted_ones()
    {
        var message = ArgumentMismatch.Describe(FindSchema, ["namePattern"]);

        message.ShouldNotBeNull();
        message.ShouldContain("unknown parameter 'namePattern'");
        message.ShouldContain("missing required parameter 'query'");
        message.ShouldContain("query (required)");
        message.ShouldContain("maxResults");
    }

    [Fact]
    public void Omitted_required_parameter_is_reported_even_when_every_supplied_name_is_valid()
    {
        var message = ArgumentMismatch.Describe(FindSchema, ["kind"]);

        message.ShouldNotBeNull();
        message.ShouldContain("missing required parameter 'query'");
        message.ShouldNotContain("unknown parameter");
    }

    [Fact]
    public void Tool_called_with_no_arguments_at_all_is_told_what_it_must_supply()
    {
        ArgumentMismatch.Describe(FindSchema, null).ShouldNotBeNull()
            .ShouldContain("missing required parameter 'query'");
    }

    [Fact]
    public void Tool_whose_parameters_are_all_optional_accepts_an_empty_call()
    {
        var schema = JsonDocument.Parse("""{ "type": "object", "properties": { "projectName": {} } }""").RootElement;

        ArgumentMismatch.Describe(schema, null).ShouldBeNull();
    }
}
