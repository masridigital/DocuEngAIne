using System.Text.Json;
using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Assets;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

/// <summary>
/// Asset layouts: typed, normalized field values; draft → validated publish → versioned changes;
/// option lists that can never leave a published choice field empty; layouts limited to chosen
/// companies; and schema changes that can never strand stored data.
/// </summary>
public class AssetLayoutTests
{
    private sealed record Ctx(DocuEngAIneDbContext Db, FakeCurrentUser User, ResourceAuthorizationService Auth, Guid TenantId, Guid CompanyA, Guid CompanyB);

    private static DocuEngAIneDbContext Context(string dbName, ICurrentUser user, ICompanyScopeAccessor? scope = null)
        => new(new DbContextOptionsBuilder<DocuEngAIneDbContext>().UseInMemoryDatabase(dbName).Options, user, scope);

    private static async Task<Ctx> SeedAsync(string dbName)
    {
        var tenantId = Guid.NewGuid();
        var user = new FakeCurrentUser { TenantId = tenantId, ObjectId = "author-oid", Email = "author@example.com", Role = UserRole.Admin };
        var db = Context(dbName, user);
        var a = new Company { TenantId = tenantId, Name = "Alpha", Slug = $"alpha-{tenantId:N}" };
        var b = new Company { TenantId = tenantId, Name = "Bravo", Slug = $"bravo-{tenantId:N}" };
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "MSP", Slug = $"msp-{tenantId:N}" });
        db.Companies.AddRange(a, b);
        await db.SaveChangesAsync();
        return new Ctx(db, user, new ResourceAuthorizationService(db, user), tenantId, a.Id, b.Id);
    }

    private static JsonElement J(object? value) => JsonSerializer.SerializeToElement(value);

    private static int StatusOf(IResult? result)
        => result is IStatusCodeHttpResult s && s.StatusCode is int code ? code : 0;

    private static T ValueOf<T>(IResult result) => Assert.IsAssignableFrom<T>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);

    private static string Json(IResult result) => JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);

    /// <summary>Creates a layout through the API and publishes it.</summary>
    private static async Task<AssetLayoutEndpoints.LayoutDetail> PublishedLayoutAsync(
        Ctx c, string name, AssetTypeFieldRequest[] fields, bool availableToAll = true)
    {
        var created = await AssetLayoutEndpoints.CreateAsync(
            new CreateAssetTypeRequest(name, Fields: fields.ToList(), AvailableToAllCompanies: availableToAll), c.Db, c.User, c.Auth);
        Assert.Equal(201, StatusOf(created));
        var id = ValueOf<AssetLayoutEndpoints.LayoutDetail>(created).Layout.Id;
        var published = await AssetLayoutEndpoints.PublishAsync(id, c.Db, c.User, c.Auth);
        Assert.Equal(200, StatusOf(published));
        return ValueOf<AssetLayoutEndpoints.LayoutDetail>(published);
    }

    private static Guid FieldId(AssetLayoutEndpoints.LayoutDetail layout, string name)
        => layout.Layout.Fields.Single(f => f.Name == name).Id;

    [Theory]
    [InlineData(AssetFieldType.Text, "\"  hello  \"", "hello")]
    [InlineData(AssetFieldType.Text, "\"   \"", null)]
    [InlineData(AssetFieldType.Text, "null", null)]
    [InlineData(AssetFieldType.Text, "42", "!")]
    [InlineData(AssetFieldType.Number, "\"1.50\"", "1.50")]
    [InlineData(AssetFieldType.Number, "42", "42")]
    [InlineData(AssetFieldType.Number, "\"12 apples\"", "!")]
    [InlineData(AssetFieldType.Date, "\"2026-09-28\"", "2026-09-28")]
    [InlineData(AssetFieldType.Date, "\"09/28/2026\"", "!")]
    [InlineData(AssetFieldType.DateTime, "\"2026-09-28T10:00:00+02:00\"", "2026-09-28T08:00:00.0000000+00:00")]
    [InlineData(AssetFieldType.Url, "\"https://example.com/runbook\"", "https://example.com/runbook")]
    [InlineData(AssetFieldType.Url, "\"javascript:alert(1)\"", "!")]
    [InlineData(AssetFieldType.Url, "\"ftp://files.example.com\"", "!")]
    [InlineData(AssetFieldType.Email, "\"ops@example.com\"", "ops@example.com")]
    [InlineData(AssetFieldType.Email, "\"not an email\"", "!")]
    [InlineData(AssetFieldType.Phone, "\"+1 (555) 010-2000 x12\"", "+1 (555) 010-2000 x12")]
    [InlineData(AssetFieldType.Phone, "\"call me\"", "!")]
    [InlineData(AssetFieldType.Checkbox, "true", "true")]
    [InlineData(AssetFieldType.Checkbox, "\"no\"", "false")]
    [InlineData(AssetFieldType.Checkbox, "\"maybe\"", "!")]
    public void Values_Are_Checked_And_Stored_In_One_Form_Per_Type(string type, string json, string? expected)
    {
        var field = new FieldDefinition { Name = "Field", FieldType = type };
        using var document = JsonDocument.Parse(json);

        var ok = AssetFieldRules.TryNormalize(field, document.RootElement, null, out var stored, out var error);

        if (expected == "!")
        {
            Assert.False(ok);
            Assert.StartsWith("Field", error);
        }
        else
        {
            Assert.True(ok, error);
            Assert.Equal(expected, stored);
        }
    }

    [Fact]
    public void Choices_Must_Be_Active_Options_Of_An_Active_List()
    {
        var list = new OptionList { Name = "Tiers", Items = [] };
        list.Items.Add(new OptionListItem { Label = "Gold", Value = "gold", SortOrder = 0 });
        list.Items.Add(new OptionListItem { Label = "Silver", Value = "silver", SortOrder = 1 });
        list.Items.Add(new OptionListItem { Label = "Legacy", Value = "legacy", SortOrder = 2, IsActive = false });
        var select = new FieldDefinition { Name = "Tier", FieldType = AssetFieldType.Select };
        var multi = new FieldDefinition { Name = "Tiers", FieldType = AssetFieldType.MultiSelect };

        Assert.True(AssetFieldRules.TryNormalize(select, J("GOLD"), list, out var byValue, out _));
        Assert.Equal("gold", byValue);
        Assert.True(AssetFieldRules.TryNormalize(select, J("Silver"), list, out var byLabel, out _));
        Assert.Equal("silver", byLabel);
        Assert.False(AssetFieldRules.TryNormalize(select, J("legacy"), list, out _, out _));
        Assert.False(AssetFieldRules.TryNormalize(select, J("platinum"), list, out _, out _));

        Assert.True(AssetFieldRules.TryNormalize(multi, J(new[] { "silver", "gold", "gold" }), list, out var chosen, out _));
        Assert.Equal("[\"gold\",\"silver\"]", chosen);
        Assert.True(AssetFieldRules.TryNormalize(multi, J(Array.Empty<string>()), list, out var none, out _));
        Assert.Null(none);
        Assert.False(AssetFieldRules.TryNormalize(multi, J("gold"), list, out _, out _));

        // A value already stored on a retired option is still valid data, just not a new choice.
        Assert.True(AssetFieldRules.IsCompatible(select, "legacy", list));

        list.IsActive = false;
        Assert.False(AssetFieldRules.TryNormalize(select, J("gold"), list, out _, out var error));
        Assert.Contains("no options", error);
    }

    [Fact]
    public async Task Asset_Create_Refuses_Another_Tenants_Layout()
    {
        var dbName = nameof(Asset_Create_Refuses_Another_Tenants_Layout);
        var mine = await SeedAsync(dbName);
        var theirs = await SeedAsync(dbName);
        var foreign = await PublishedLayoutAsync(theirs, "Their layout", [new AssetTypeFieldRequest("Secret field", AssetFieldType.Text)]);

        var result = await AssetEndpoints.PostAsync(
            new CreateAssetRequest("Box", foreign.Layout.Id, null, null, null, mine.CompanyA), mine.Db, mine.User, mine.Auth);

        Assert.Equal(400, StatusOf(result));
        Assert.Equal(AssetEndpoints.LayoutNotFoundMessage, ValueOf<string>(result));
        Assert.Empty(await mine.Db.Assets.ForTenant(mine.User).ToListAsync());
    }

    [Fact]
    public async Task Drafts_Are_Not_Offered_Until_They_Publish_And_Publishing_Validates()
    {
        var c = await SeedAsync(nameof(Drafts_Are_Not_Offered_Until_They_Publish_And_Publishing_Validates));
        var tiers = ValueOf<OptionListEndpoints.OptionListView>(
            await OptionListEndpoints.CreateAsync(new OptionListEndpoints.CreateOptionListRequest("Tiers"), c.Db, c.User, c.Auth));

        var created = await AssetLayoutEndpoints.CreateAsync(
            new CreateAssetTypeRequest("Firewall", Fields:
            [
                new AssetTypeFieldRequest("Serial", AssetFieldType.Text, IsRequired: true),
                new AssetTypeFieldRequest("Tier", AssetFieldType.Select, OptionListId: tiers.Id),
            ]),
            c.Db, c.User, c.Auth);
        Assert.Equal(201, StatusOf(created));
        var draft = ValueOf<AssetLayoutEndpoints.LayoutDetail>(created);
        Assert.False(draft.Layout.IsPublished);
        Assert.Contains(draft.Problems, p => p.Code == "options_empty");

        var early = await AssetEndpoints.PostAsync(
            new CreateAssetRequest("fw-1", draft.Layout.Id, null, null, null, c.CompanyA, Fields: new() { [FieldId(draft, "Serial")] = J("SN-1") }),
            c.Db, c.User, c.Auth);
        Assert.Equal(400, StatusOf(early));

        Assert.Equal(409, StatusOf(await AssetLayoutEndpoints.PublishAsync(draft.Layout.Id, c.Db, c.User, c.Auth)));

        await OptionListEndpoints.AddItemAsync(tiers.Id, new OptionListEndpoints.OptionItemInput("Gold"), c.Db, c.User, c.Auth);
        var published = ValueOf<AssetLayoutEndpoints.LayoutDetail>(await AssetLayoutEndpoints.PublishAsync(draft.Layout.Id, c.Db, c.User, c.Auth));
        Assert.True(published.Layout.IsPublished);
        Assert.Equal(1, published.Layout.CurrentVersion);

        // While published, every schema change is a new version.
        Assert.Equal(201, StatusOf(await AssetLayoutEndpoints.AddFieldAsync(
            draft.Layout.Id, new AssetTypeFieldRequest("Firmware", AssetFieldType.Text), c.Db, c.User, c.Auth)));
        var versions = ValueOf<List<AssetLayoutEndpoints.VersionSummary>>(
            await AssetLayoutEndpoints.ListVersionsAsync(draft.Layout.Id, c.Db, c.User));
        Assert.Equal(new[] { 2, 1 }, versions.Select(v => v.VersionNumber).ToArray());
        Assert.Equal("Added field 'Firmware' (Text)", versions[0].Summary);

        var v1 = ValueOf<AssetLayoutEndpoints.VersionDetail>(await AssetLayoutEndpoints.GetVersionAsync(draft.Layout.Id, 1, c.Db, c.User));
        Assert.Equal(2, v1.Schema.GetProperty("fields").GetArrayLength());
    }

    [Fact]
    public async Task Required_And_Unknown_Fields_Are_Enforced_When_Writing_Values()
    {
        var c = await SeedAsync(nameof(Required_And_Unknown_Fields_Are_Enforced_When_Writing_Values));
        var layout = await PublishedLayoutAsync(c, "Server",
        [
            new AssetTypeFieldRequest("Serial", AssetFieldType.Text, IsRequired: true),
            new AssetTypeFieldRequest("Purchased", AssetFieldType.Date),
        ]);
        var serial = FieldId(layout, "Serial");
        var purchased = FieldId(layout, "Purchased");

        var missing = await AssetEndpoints.PostAsync(new CreateAssetRequest("srv-1", layout.Layout.Id, null, null, null, c.CompanyA), c.Db, c.User, c.Auth);
        Assert.Equal(400, StatusOf(missing));
        Assert.Contains("Serial is required.", Json(missing));

        var created = await AssetEndpoints.PostAsync(
            new CreateAssetRequest("srv-1", layout.Layout.Id, null, null, null, c.CompanyA, Fields: new()
            {
                [serial] = J("  SN-42 "),
                [purchased] = J("2026-01-02"),
            }),
            c.Db, c.User, c.Auth);
        Assert.Equal(201, StatusOf(created));
        var assetId = (await c.Db.Assets.SingleAsync()).Id;
        Assert.Equal("SN-42", (await c.Db.CustomFieldValues.SingleAsync(v => v.FieldDefinitionId == serial)).Value);

        Assert.Equal(400, StatusOf(await AssetEndpoints.PutFieldsAsync(
            assetId, new UpdateAssetFieldsRequest(new() { [serial] = J(null) }), c.Db, c.User, c.Auth)));
        Assert.Equal(400, StatusOf(await AssetEndpoints.PutFieldsAsync(
            assetId, new UpdateAssetFieldsRequest(new() { [purchased] = J("next tuesday") }), c.Db, c.User, c.Auth)));
        Assert.Equal(400, StatusOf(await AssetEndpoints.PutFieldsAsync(
            assetId, new UpdateAssetFieldsRequest(new() { [Guid.NewGuid()] = J("x") }), c.Db, c.User, c.Auth)));

        var cleared = await AssetEndpoints.PutFieldsAsync(
            assetId, new UpdateAssetFieldsRequest(new() { [purchased] = J(null) }), c.Db, c.User, c.Auth);
        Assert.Equal(200, StatusOf(cleared));
        Assert.False(await c.Db.CustomFieldValues.AnyAsync(v => v.FieldDefinitionId == purchased));
    }

    [Fact]
    public async Task A_Layout_Limited_To_Chosen_Companies_Needs_Manage_To_Enable_Per_Company()
    {
        var c = await SeedAsync(nameof(A_Layout_Limited_To_Chosen_Companies_Needs_Manage_To_Enable_Per_Company));
        var layout = await PublishedLayoutAsync(c, "Medical device", [new AssetTypeFieldRequest("Model", AssetFieldType.Text)], availableToAll: false);
        var id = layout.Layout.Id;

        Assert.Equal(400, StatusOf(await AssetEndpoints.PostAsync(new CreateAssetRequest("pump", id, null, null, null, c.CompanyA), c.Db, c.User, c.Auth)));
        Assert.Equal(400, StatusOf(await AssetEndpoints.PostAsync(new CreateAssetRequest("pump", id, null, null, null), c.Db, c.User, c.Auth)));

        // A tech confined to Alpha with Edit cannot choose layouts for it; Manage can.
        var edit = new CompanyScopeAccessor
        {
            Current = CompanyAccessScope.Restricted(false, new Dictionary<Guid, CompanyAccessLevel> { [c.CompanyA] = CompanyAccessLevel.Edit }),
        };
        var tech = new FakeCurrentUser { TenantId = c.TenantId, ObjectId = "tech-oid", Role = UserRole.Contributor };
        var techDb = Context(nameof(A_Layout_Limited_To_Chosen_Companies_Needs_Manage_To_Enable_Per_Company), tech, edit);
        Assert.Equal(403, StatusOf(await AssetLayoutEndpoints.ActivateAsync(id, c.CompanyA, techDb, tech, new ResourceAuthorizationService(techDb, tech))));
        Assert.Equal(404, StatusOf(await AssetLayoutEndpoints.ActivateAsync(id, c.CompanyB, techDb, tech, new ResourceAuthorizationService(techDb, tech))));

        var manage = new CompanyScopeAccessor
        {
            Current = CompanyAccessScope.Restricted(false, new Dictionary<Guid, CompanyAccessLevel> { [c.CompanyA] = CompanyAccessLevel.Manage }),
        };
        var managerDb = Context(nameof(A_Layout_Limited_To_Chosen_Companies_Needs_Manage_To_Enable_Per_Company), tech, manage);
        var enabled = ValueOf<AssetLayoutEndpoints.LayoutDetail>(
            await AssetLayoutEndpoints.ActivateAsync(id, c.CompanyA, managerDb, tech, new ResourceAuthorizationService(managerDb, tech)));
        Assert.Equal("Alpha", Assert.Single(enabled.Companies).CompanyName);

        Assert.Equal(201, StatusOf(await AssetEndpoints.PostAsync(new CreateAssetRequest("pump", id, null, null, null, c.CompanyA), c.Db, c.User, c.Auth)));
        Assert.Equal(400, StatusOf(await AssetEndpoints.PostAsync(new CreateAssetRequest("pump", id, null, null, null, c.CompanyB), c.Db, c.User, c.Auth)));

        // Disabling stops new assets; existing ones stay.
        await AssetLayoutEndpoints.DeactivateAsync(id, c.CompanyA, c.Db, c.User, c.Auth);
        Assert.Equal(400, StatusOf(await AssetEndpoints.PostAsync(new CreateAssetRequest("pump-2", id, null, null, null, c.CompanyA), c.Db, c.User, c.Auth)));
        Assert.Single(await c.Db.Assets.ForTenant(c.User).ToListAsync());
    }

    [Fact]
    public async Task Schema_Changes_Never_Strand_Stored_Data()
    {
        var c = await SeedAsync(nameof(Schema_Changes_Never_Strand_Stored_Data));
        var layout = await PublishedLayoutAsync(c, "Switch",
        [
            new AssetTypeFieldRequest("Ports", AssetFieldType.Number),
            new AssetTypeFieldRequest("Rack", AssetFieldType.Text),
        ]);
        var other = await PublishedLayoutAsync(c, "Router", [new AssetTypeFieldRequest("Model", AssetFieldType.Text)]);
        var ports = FieldId(layout, "Ports");
        await AssetEndpoints.PostAsync(
            new CreateAssetRequest("sw-1", layout.Layout.Id, null, null, null, c.CompanyA, Fields: new() { [ports] = J(48) }),
            c.Db, c.User, c.Auth);
        var assetId = (await c.Db.Assets.SingleAsync()).Id;

        // 48 is not a date; it is text.
        Assert.Equal(409, StatusOf(await AssetLayoutEndpoints.UpdateFieldAsync(
            ports, new UpdateFieldDefinitionRequest(FieldType: AssetFieldType.Date), c.Db, c.User, c.Auth)));
        Assert.Equal(200, StatusOf(await AssetLayoutEndpoints.UpdateFieldAsync(
            ports, new UpdateFieldDefinitionRequest(FieldType: AssetFieldType.Text), c.Db, c.User, c.Auth)));

        Assert.Equal(409, StatusOf(await AssetLayoutEndpoints.DeleteFieldAsync(ports, c.Db, c.User, c.Auth)));
        Assert.Equal(204, StatusOf(await AssetLayoutEndpoints.DeleteFieldAsync(FieldId(layout, "Rack"), c.Db, c.User, c.Auth)));
        Assert.Equal(409, StatusOf(await AssetLayoutEndpoints.DeleteAsync(layout.Layout.Id, c.Db, c.User, c.Auth)));
        Assert.Equal(409, StatusOf(await AssetEndpoints.UpdateAsync(
            assetId, new UpdateAssetRequest(null, other.Layout.Id, null, null, null), c.Db, c.User)));

        // A published layout keeps at least one field.
        Assert.Equal(409, StatusOf(await AssetLayoutEndpoints.DeleteFieldAsync(FieldId(other, "Model"), c.Db, c.User, c.Auth)));
    }

    [Fact]
    public void Type_Changes_Convert_Stored_Values_To_The_New_Form()
    {
        var list = new OptionList { Name = "Tiers" };
        list.Items.Add(new OptionListItem { Label = "Gold tier", Value = "gold", SortOrder = 0 });
        list.Items.Add(new OptionListItem { Label = "Silver tier", Value = "silver", SortOrder = 1 });
        var select = new FieldDefinition { Name = "Tier", FieldType = AssetFieldType.Select };
        var multi = new FieldDefinition { Name = "Tier", FieldType = AssetFieldType.MultiSelect };
        var text = new FieldDefinition { Name = "Tier", FieldType = AssetFieldType.Text };
        var checkbox = new FieldDefinition { Name = "Managed", FieldType = AssetFieldType.Checkbox };
        var date = new FieldDefinition { Name = "Installed", FieldType = AssetFieldType.Date };
        var dateTime = new FieldDefinition { Name = "Installed", FieldType = AssetFieldType.DateTime };

        // A single choice becomes a one-option multi-select, and back.
        Assert.True(AssetFieldRules.TryConvertStored(select, list, multi, list, "gold", out var many));
        Assert.Equal("[\"gold\"]", many);
        Assert.True(AssetFieldRules.TryConvertStored(multi, list, select, list, "[\"silver\"]", out var one));
        Assert.Equal("silver", one);
        Assert.False(AssetFieldRules.TryConvertStored(multi, list, select, list, "[\"gold\",\"silver\"]", out _));

        // Leaving a choice type keeps the labels people saw; coming back finds the option by label.
        Assert.True(AssetFieldRules.TryConvertStored(multi, list, text, null, "[\"gold\",\"silver\"]", out var labels));
        Assert.Equal("Gold tier, Silver tier", labels);
        Assert.True(AssetFieldRules.TryConvertStored(text, null, select, list, "Silver tier", out var back));
        Assert.Equal("silver", back);

        Assert.True(AssetFieldRules.TryConvertStored(text, null, checkbox, null, "Yes", out var yes));
        Assert.Equal("true", yes);
        Assert.True(AssetFieldRules.TryConvertStored(date, null, dateTime, null, "2026-01-02", out var moment));
        Assert.Equal("2026-01-02T00:00:00.0000000+00:00", moment);
        Assert.False(AssetFieldRules.TryConvertStored(text, null, date, null, "soon", out _));
    }

    [Fact]
    public async Task Option_Lists_Keep_Published_Choice_Fields_Usable()
    {
        var c = await SeedAsync(nameof(Option_Lists_Keep_Published_Choice_Fields_Usable));
        var created = await OptionListEndpoints.CreateAsync(
            new OptionListEndpoints.CreateOptionListRequest("Tiers", Items: [new("Gold tier"), new("Gold tier"), new("Silver")]),
            c.Db, c.User, c.Auth);
        var tiers = ValueOf<OptionListEndpoints.OptionListView>(created);
        Assert.Equal(new[] { "gold-tier", "gold-tier-2", "silver" }, tiers.Items.Select(i => i.Value).ToArray());

        // Renaming keeps the stored value.
        var gold = tiers.Items[0];
        var renamed = ValueOf<OptionListEndpoints.OptionItemView>(await OptionListEndpoints.UpdateItemAsync(
            tiers.Id, gold.Id, new OptionListEndpoints.UpdateOptionItemRequest(Label: "Gold"), c.Db, c.User, c.Auth));
        Assert.Equal(("Gold", "gold-tier"), (renamed.Label, renamed.Value));

        await PublishedLayoutAsync(c, "Contract", [new AssetTypeFieldRequest("Tier", AssetFieldType.Select, OptionListId: tiers.Id)]);

        Assert.Equal(409, StatusOf(await OptionListEndpoints.UpdateAsync(
            tiers.Id, new OptionListEndpoints.UpdateOptionListRequest(IsActive: false), c.Db, c.User, c.Auth)));
        Assert.Equal(409, StatusOf(await OptionListEndpoints.DeleteAsync(tiers.Id, c.Db, c.User, c.Auth)));

        // Retiring options is fine until only one active option is left.
        foreach (var item in tiers.Items.Skip(1))
        {
            Assert.Equal(200, StatusOf(await OptionListEndpoints.UpdateItemAsync(
                tiers.Id, item.Id, new OptionListEndpoints.UpdateOptionItemRequest(IsActive: false), c.Db, c.User, c.Auth)));
        }

        Assert.Equal(409, StatusOf(await OptionListEndpoints.UpdateItemAsync(
            tiers.Id, gold.Id, new OptionListEndpoints.UpdateOptionItemRequest(IsActive: false), c.Db, c.User, c.Auth)));

        var unused = ValueOf<OptionListEndpoints.OptionListView>(
            await OptionListEndpoints.CreateAsync(new OptionListEndpoints.CreateOptionListRequest("Unused"), c.Db, c.User, c.Auth));
        Assert.Equal(204, StatusOf(await OptionListEndpoints.DeleteAsync(unused.Id, c.Db, c.User, c.Auth)));
    }

    [Fact]
    public async Task A_Published_Layout_Refuses_A_Choice_Field_With_Nothing_To_Choose()
    {
        var c = await SeedAsync(nameof(A_Published_Layout_Refuses_A_Choice_Field_With_Nothing_To_Choose));
        var empty = ValueOf<OptionListEndpoints.OptionListView>(
            await OptionListEndpoints.CreateAsync(new OptionListEndpoints.CreateOptionListRequest("Empty"), c.Db, c.User, c.Auth));
        var layout = await PublishedLayoutAsync(c, "Printer", [new AssetTypeFieldRequest("Model", AssetFieldType.Text)]);

        Assert.Equal(409, StatusOf(await AssetLayoutEndpoints.AddFieldAsync(
            layout.Layout.Id, new AssetTypeFieldRequest("Colour", AssetFieldType.Select, OptionListId: empty.Id), c.Db, c.User, c.Auth)));
        Assert.Equal(400, StatusOf(await AssetLayoutEndpoints.AddFieldAsync(
            layout.Layout.Id, new AssetTypeFieldRequest("Colour", "Colour"), c.Db, c.User, c.Auth)));
        Assert.Equal(409, StatusOf(await AssetLayoutEndpoints.AddFieldAsync(
            layout.Layout.Id, new AssetTypeFieldRequest("Warranty ends", AssetFieldType.Text, IsExpiration: true), c.Db, c.User, c.Auth)));
    }
}
