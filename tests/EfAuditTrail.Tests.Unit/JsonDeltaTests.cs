using System.Text.Json.Nodes;
using EfAuditTrail.Data.Auditing;

namespace EfAuditTrail.Tests.Unit;

/// <summary>JsonDelta is the reference the trigger's audit.jsonb_delta() ports — these cases are mirrored in
/// the integration parity tests, so both implementations are held to the same answers.</summary>
public sealed class JsonDeltaTests
{
    [Fact]
    public void Equal_documents_have_no_delta()
        => JsonDelta.Compute("""{"a": 1, "b": [1, 2]}""", """{"b": [1, 2], "a": 1}""").ShouldBeEmpty();

    [Fact]
    public void A_scalar_root_yields_the_empty_path()
    {
        var delta = JsonDelta.Compute("1", "2");

        delta.Keys.ShouldBe([string.Empty]);
        Text(delta[string.Empty]).ShouldBe(("1", "2"));
    }

    [Fact]
    public void Nested_changes_are_reported_as_json_pointers()
    {
        var delta = JsonDelta.Compute(
            """{"contact": {"name": "Bo", "phone": "1"}}""",
            """{"contact": {"name": "Bo", "phone": "2"}}""");

        delta.Keys.ShouldBe(["/contact/phone"]);
        Text(delta["/contact/phone"]).ShouldBe(("\"1\"", "\"2\""));
    }

    [Fact]
    public void Added_and_removed_keys_have_a_null_side()
    {
        var delta = JsonDelta.Compute("""{"gone": true}""", """{"new": "x"}""");

        Text(delta["/gone"]).ShouldBe(("true", null));
        Text(delta["/new"]).ShouldBe((null, "\"x\""));
    }

    [Fact]
    public void Json_null_and_a_missing_key_are_the_same()
        => JsonDelta.Compute("""{"a": null}""", "{}").ShouldBeEmpty();

    [Fact]
    public void Arrays_are_compared_by_index()
    {
        var delta = JsonDelta.Compute("[1, 2]", "[1, 3, 4]");

        delta.Keys.OrderBy(x => x, StringComparer.Ordinal).ShouldBe(["/1", "/2"]);
        Text(delta["/1"]).ShouldBe(("2", "3"));
        Text(delta["/2"]).ShouldBe((null, "4"));
    }

    [Fact]
    public void A_container_against_null_is_diffed_to_its_leaves()
    {
        var delta = JsonDelta.Compute(null, """{"a": {"b": 1}}""");

        delta.Keys.ShouldBe(["/a/b"]);
    }

    [Fact]
    public void A_type_change_reports_the_whole_subtree()
    {
        var delta = JsonDelta.Compute("""{"gear": ["boots"]}""", """{"gear": "none"}""");

        Text(delta["/gear"]).ShouldBe(("[\"boots\"]", "\"none\""));
    }

    [Fact]
    public void Keys_are_escaped_per_rfc_6901()
        => JsonDelta.Compute("""{"a/b~c": 1}""", """{"a/b~c": 2}""").Keys.ShouldBe(["/a~1b~0c"]);

    private static (string? Old, string? New) Text((JsonNode? Old, JsonNode? New) change)
        => (change.Old?.ToJsonString(), change.New?.ToJsonString());
}
