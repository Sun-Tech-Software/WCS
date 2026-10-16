using System.Text.Json.Nodes;
using Xunit;

namespace Wcs.ContractCheck.Test;

/// <summary>
/// The classification of differences, seen from the party that is not the WCS: a client of the host API
/// (<c>x-wcs-provider: wcs</c>) and the WMS that serves the WMS contract (<c>x-wcs-provider: wms</c>).
/// </summary>
public sealed class ContractComparerTest
{
    /// <summary>A document with one POST that takes <c>Body</c> and answers <c>Answer</c>.</summary>
    private static JsonObject Document(string provider, string body, string answer, string version = "1.0.0", string extraPaths = "") =>
        JsonNode.Parse($$"""
        {
          "openapi": "3.1.1",
          "info": { "title": "t", "version": "{{version}}" },
          "x-wcs-provider": "{{provider}}",
          "paths": {
            "/things": {
              "post": {
                "operationId": "Things",
                "requestBody": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Body" } } } },
                "responses": { "200": { "description": "ok", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Answer" } } } } }
              }
            }{{extraPaths}}
          },
          "components": { "schemas": { "Body": {{body}}, "Answer": {{answer}} } }
        }
        """)!.AsObject();

    private const string TwoFields = """{ "type": "object", "properties": { "id": { "type": "number" }, "name": { "type": "string" } }, "required": ["id"] }""";
    private const string OtherPath = """, "/other": { "get": { "operationId": "Other", "responses": { "200": { "description": "ok" } } } }""";

    private static ContractChange Single(JsonObject before, JsonObject after)
    {
        var changes = ContractComparer.Compare(before, after);
        Assert.True(changes.Count == 1, string.Join("\n", changes));
        return changes[0];
    }

    [Theory]
    [InlineData("wcs", ChangeKind.Breaking)]
    [InlineData("wms", ChangeKind.NonBreaking)]
    public void ARemovedOperationBreaksAClientButNotAWms(string provider, ChangeKind expected)
    {
        var change = Single(Document(provider, TwoFields, TwoFields, extraPaths: OtherPath), Document(provider, TwoFields, TwoFields));

        Assert.Equal(expected, change.Kind);
        Assert.Equal("GET /other", change.Where);
    }

    [Theory]
    [InlineData("wcs", ChangeKind.NonBreaking)]
    [InlineData("wms", ChangeKind.Breaking)]
    public void AnAddedOperationBreaksAWmsWhichDoesNotServeItYet(string provider, ChangeKind expected) =>
        Assert.Equal(expected, Single(Document(provider, TwoFields, TwoFields), Document(provider, TwoFields, TwoFields, extraPaths: OtherPath)).Kind);

    /// <summary>Item 55: an operation the WCS does without (404 or 501 = not offered) breaks no WMS.</summary>
    [Fact]
    public void AnAddedOptionalOperationBreaksNoWms()
    {
        const string optional = """, "/other": { "get": { "operationId": "Other", "x-wcs-optional": true, "responses": { "200": { "description": "ok" } } } }""";

        var change = Single(Document("wms", TwoFields, TwoFields), Document("wms", TwoFields, TwoFields, extraPaths: optional));

        Assert.Equal(ChangeKind.NonBreaking, change.Kind);
        Assert.Contains("optional", change.ToString());
    }

    [Theory]
    [InlineData("wcs", true, ChangeKind.Breaking)]
    [InlineData("wcs", false, ChangeKind.NonBreaking)]
    [InlineData("wms", true, ChangeKind.NonBreaking)]
    public void ARequestFieldAddedBreaksOnlyAClientThatMustNowSendIt(string provider, bool required, ChangeKind expected)
    {
        var after = $$"""{ "type": "object", "properties": { "id": { "type": "number" }, "name": { "type": "string" }, "more": { "type": "string" } }, "required": ["id"{{(required ? ", \"more\"" : "")}}] }""";

        Assert.Equal(expected, Single(Document(provider, TwoFields, TwoFields), Document(provider, after, TwoFields)).Kind);
    }

    [Theory]
    [InlineData("wcs", ChangeKind.NonBreaking)]
    [InlineData("wms", ChangeKind.Breaking)]
    public void ARequestFieldRemovedBreaksAWmsThatReadsIt(string provider, ChangeKind expected)
    {
        const string after = """{ "type": "object", "properties": { "id": { "type": "number" } }, "required": ["id"] }""";

        Assert.Equal(expected, Single(Document(provider, TwoFields, TwoFields), Document(provider, after, TwoFields)).Kind);
    }

    [Theory]
    [InlineData("wcs", ChangeKind.Breaking)]
    [InlineData("wms", ChangeKind.NonBreaking)]
    public void AnAnswerFieldRemovedBreaksAClientThatReadsIt(string provider, ChangeKind expected)
    {
        const string after = """{ "type": "object", "properties": { "id": { "type": "number" } }, "required": ["id"] }""";

        Assert.Equal(expected, Single(Document(provider, TwoFields, TwoFields), Document(provider, TwoFields, after)).Kind);
    }

    [Fact]
    public void AnAnswerFieldTheWcsNowRequiresBreaksAWms()
    {
        const string after = """{ "type": "object", "properties": { "id": { "type": "number" }, "name": { "type": "string" } }, "required": ["id", "name"] }""";

        Assert.Equal(ChangeKind.Breaking, Single(Document("wms", TwoFields, TwoFields), Document("wms", TwoFields, after)).Kind);
    }

    [Theory]
    [InlineData("wcs")]
    [InlineData("wms")]
    public void AChangedTypeBreaksBothSides(string provider)
    {
        const string after = """{ "type": "object", "properties": { "id": { "type": "string" }, "name": { "type": "string" } }, "required": ["id"] }""";

        var changes = ContractComparer.Compare(Document(provider, TwoFields, TwoFields), Document(provider, after, after));

        Assert.Contains(changes, c => c.Kind == ChangeKind.Breaking);
    }

    [Fact]
    public void ACasingChangeOfTheWmsRequestsIsBreaking()
    {
        const string pascal = """{ "type": "object", "properties": { "LoadUnitId": { "type": "number" } }, "required": ["LoadUnitId"] }""";
        const string camel = """{ "type": "object", "properties": { "loadUnitId": { "type": "number" } }, "required": ["loadUnitId"] }""";

        var changes = ContractComparer.Compare(Document("wms", pascal, TwoFields), Document("wms", camel, TwoFields));

        Assert.Contains(changes, c => c.Kind == ChangeKind.Breaking && c.What == "property LoadUnitId removed");
    }

    [Theory]
    [InlineData("""[0, 1]""", """[0, 1, 2]""", "wcs", "Body", ChangeKind.NonBreaking)]
    [InlineData("""[0, 1, 2]""", """[0, 1]""", "wcs", "Body", ChangeKind.Breaking)]
    [InlineData("""[0, 1]""", """[0, 1, 2]""", "wcs", "Answer", ChangeKind.Breaking)]
    [InlineData("""[0, 1]""", """[0, 1, 2]""", "wms", "Body", ChangeKind.Breaking)]
    [InlineData("""[0, 1, 2]""", """[0, 1]""", "wms", "Answer", ChangeKind.Breaking)]
    public void EnumerationValuesBreakTheSideThatReadsThem(string before, string after, string provider, string where, ChangeKind expected)
    {
        string Schema(string values) => $$"""{ "type": "object", "properties": { "state": { "type": "integer", "enum": {{values}} } } }""";

        var old = Document(provider, Schema(before), Schema(before));
        var @new = Document(provider, where == "Body" ? Schema(after) : Schema(before), where == "Answer" ? Schema(after) : Schema(before));

        Assert.Equal(expected, Single(old, @new).Kind);
    }

    [Fact]
    public void ADescriptionIsACompatibleChange()
    {
        var after = Document("wcs", TwoFields, TwoFields);
        after["paths"]!["/things"]!["post"]!["description"] = "now described";

        var change = Single(Document("wcs", TwoFields, TwoFields), after);

        Assert.Equal(ChangeKind.NonBreaking, change.Kind);
    }

    [Fact]
    public void TheVersionAloneIsNoChange() =>
        Assert.Empty(ContractComparer.Compare(Document("wcs", TwoFields, TwoFields), Document("wcs", TwoFields, TwoFields, version: "1.1.0")));

    [Fact]
    public void OnTheRealWmsContractAPascalRequestThatTurnsCamelIsBreakingAndARouteNoLongerCalledIsNot()
    {
        var before = RepositoryContracts.Read(WmsContractDocument.FileName);
        var after = before.DeepClone().AsObject();
        var reserve = after["components"]!["schemas"]!["ReserveStorageRequest"]!.AsObject();
        var properties = reserve["properties"]!.AsObject();
        var id = properties["LoadUnitId"]!.DeepClone();
        properties.Remove("LoadUnitId");
        properties["loadUnitId"] = id;
        after["paths"]!.AsObject().Remove("/api/wcs/warehouse/configuration-check");

        var changes = ContractComparer.Compare(before, after);

        Assert.Contains(changes, c => c is { Kind: ChangeKind.Breaking, Where: "POST /api/wcs/warehouse/reservations request", What: "property LoadUnitId removed" });
        Assert.Contains(changes, c => c is { Kind: ChangeKind.NonBreaking, Where: "GET /api/wcs/warehouse/configuration-check" });
    }

    [Fact]
    public void OnTheRealHostApiARemovedRouteAndANarrowedAnswerAreBreaking()
    {
        var before = RepositoryContracts.Read(HostApiDocument.FileName);
        var after = before.DeepClone().AsObject();
        after["paths"]!.AsObject().Remove("/api/v1/log/transit-times");
        after["components"]!["schemas"]!["EngineStatus"]!["properties"]!.AsObject().Remove("engineLoadError");

        var changes = ContractComparer.Compare(before, after);

        Assert.Contains(changes, c => c is { Kind: ChangeKind.Breaking, Where: "GET /api/v1/log/transit-times", What: "operation removed" });
        Assert.Contains(changes, c => c is { Kind: ChangeKind.Breaking, Where: "GET /api/v1/status answer 200", What: "property engineLoadError removed" });
    }
}
