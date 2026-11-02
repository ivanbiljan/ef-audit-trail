using System.Text.Json.Nodes;
using EfAuditTrail.Tests.Integration.Infrastructure;

namespace EfAuditTrail.Tests.Integration;

/// <summary>The rules that decide what a row says, held to their answers directly in the database:
/// <c>audit.jsonb_delta</c> and <c>audit.is_personal</c> with the patterns the EF model registered.</summary>
public sealed class AuditSqlTests : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await TestDatabase.CreateAsync(seed: false);

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    [Fact]
    public async Task Equal_documents_have_no_delta()
        => (await DeltaAsync("""{"a": 1, "b": [1, 2]}""", """{"b": [1, 2], "a": 1}""")).ShouldBeEmpty();

    [Fact]
    public async Task A_scalar_root_yields_the_empty_path()
    {
        var delta = await DeltaAsync("1", "2");

        delta.Keys.ShouldBe([string.Empty]);
        delta[string.Empty].ShouldBe(("1", "2"));
    }

    [Fact]
    public async Task Nested_changes_are_reported_as_json_pointers()
    {
        var delta = await DeltaAsync(
            """{"contact": {"name": "Bo", "phone": "1"}}""",
            """{"contact": {"name": "Bo", "phone": "2"}}""");

        delta.Keys.ShouldBe(["/contact/phone"]);
        delta["/contact/phone"].ShouldBe(("\"1\"", "\"2\""));
    }

    [Fact]
    public async Task Added_and_removed_keys_have_a_null_side()
    {
        var delta = await DeltaAsync("""{"gone": true}""", """{"new": "x"}""");

        delta["/gone"].ShouldBe(("true", null));
        delta["/new"].ShouldBe((null, "\"x\""));
    }

    [Fact]
    public async Task Json_null_and_a_missing_key_are_the_same()
        => (await DeltaAsync("""{"a": null}""", "{}")).ShouldBeEmpty();

    [Fact]
    public async Task Arrays_are_compared_by_index()
    {
        var delta = await DeltaAsync("[1, 2]", "[1, 3, 4]");

        delta.Keys.OrderBy(x => x, StringComparer.Ordinal).ShouldBe(["/1", "/2"]);
        delta["/1"].ShouldBe(("2", "3"));
        delta["/2"].ShouldBe((null, "4"));
    }

    [Fact]
    public async Task A_container_against_null_is_diffed_to_its_leaves()
        => (await DeltaAsync(null, """{"a": {"b": 1}}""")).Keys.ShouldBe(["/a/b"]);

    [Fact]
    public async Task A_type_change_reports_the_whole_subtree()
        => (await DeltaAsync("""{"gear": ["boots"]}""", """{"gear": "none"}"""))["/gear"].ShouldBe(("[\"boots\"]", "\"none\""));

    [Fact]
    public async Task Keys_are_escaped_per_rfc_6901()
        => (await DeltaAsync("""{"a/b~c": 1}""", """{"a/b~c": 2}""")).Keys.ShouldBe(["/a~1b~0c"]);

    [Theory]
    [InlineData("/deliveryContact/phone", false, true)]
    [InlineData("/deliveryContact", false, true)]
    [InlineData("/identity/number", false, true)]
    [InlineData("/language", false, false)]
    [InlineData("/interests/0", false, false)]
    [InlineData("/language", true, true)]
    public async Task Profile_patterns_narrow_personal_data_to_their_paths(string path, bool container, bool expected)
        => (await IsPersonalAsync("Customer", "Profile", path, container)).ShouldBe(expected);

    [Theory]
    [InlineData("/parcels/0/recipient", true)]
    [InlineData("/parcels/3/phone", true)]
    [InlineData("/parcels/0/size", false)]
    public async Task Single_segment_wildcards_match_array_indexes(string path, bool expected)
        => (await IsPersonalAsync("Order", "Delivery", path, container: false)).ShouldBe(expected);

    [Fact]
    public async Task A_bare_AuditPii_marks_the_whole_value()
    {
        (await IsPersonalAsync("Customer", "Email", string.Empty, container: false)).ShouldBeTrue();
        (await IsPersonalAsync("Customer", "LoyaltyTier", string.Empty, container: false)).ShouldBeFalse();
    }

    private async Task<Dictionary<string, (string? Old, string? New)>> DeltaAsync(string? oldJson, string? newJson)
    {
        var json = await _db.ScalarAsync<string>($"""
            select coalesce(jsonb_object_agg(path, jsonb_build_array(old_value, new_value)), '{"{}"}')::text
              from audit.jsonb_delta('', {Literal(oldJson)}, {Literal(newJson)})
            """);

        return JsonNode.Parse(json)!.AsObject().ToDictionary(
            x => x.Key,
            x => (Text(x.Value![0]), Text(x.Value![1])));
    }

    private Task<bool> IsPersonalAsync(string entityType, string property, string path, bool container)
        => _db.ScalarAsync<bool>($"""
            select audit.is_personal(c -> 'pii', '{path}', {container})
              from audit.tracked_entity t, jsonb_array_elements(t.columns) c
             where t.entity_type = '{entityType}' and c ->> 'property' = '{property}'
            """);

    private static string Literal(string? json) => json is null ? "null::jsonb" : $"$j${json}$j$::jsonb";

    private static string? Text(JsonNode? node) => node?.ToJsonString();
}
