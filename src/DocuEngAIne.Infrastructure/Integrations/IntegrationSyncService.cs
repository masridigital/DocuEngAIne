using System.Text.Json;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Core.Mcp;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Infrastructure.Integrations;

public class IntegrationSyncService : IIntegrationSyncService
{
    /// <summary><see cref="IntegrationMapping.ExternalType"/> for a remote device. Stable once shipped.</summary>
    private const string DeviceExternalType = "device";

    /// <summary>
    /// Asset layout that synced devices land in. The plan's layout baseline names it, but nothing seeds
    /// asset types, so the first device sync creates it rather than failing on a name an operator would
    /// have to guess. The unique (TenantId, Name) index keeps it to one per tenant.
    /// </summary>
    public const string ComputerAssetTypeName = "Computer Assets";

    private readonly DocuEngAIneDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IMcpClient _mcpClient;
    private readonly IAuditService _audit;

    public IntegrationSyncService(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IMcpClient mcpClient,
        IAuditService audit)
    {
        _db = db;
        _user = user;
        _mcpClient = mcpClient;
        _audit = audit;
    }

    public async Task<(bool Ok, string Message)> TestConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        var connection = await _db.IntegrationConnections.ForTenant(_user)
            .FirstOrDefaultAsync(c => c.Id == connectionId, cancellationToken);
        if (connection is null)
            return (false, "Integration not found.");

        if (await ResolveMcpServerIdAsync(connection, cancellationToken) is Guid mcpId)
        {
            try
            {
                await _mcpClient.ListToolsAsync(mcpId, cancellationToken);
                connection.Status = IntegrationStatus.Connected;
                connection.LastError = null;
            }
            catch (Exception ex)
            {
                connection.Status = IntegrationStatus.Error;
                connection.LastError = ex.Message;
                await _db.SaveChangesAsync(cancellationToken);
                return (false, ex.Message);
            }

            // Second call, and deliberately outside the try above: stackjack_session_info is a free
            // platform tool that never draws down a connector allowance, but it is also not what the
            // test is testing. tools/list already proved the server answers, so a detection failure
            // reports the connection as Connected with the plan left unknown.
            var plan = await DetectPlanAsync(connection, mcpId, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            return (true, $"MCP server responded to tools/list. {plan}");
        }

        if (string.IsNullOrWhiteSpace(connection.AuthSecretName) && string.IsNullOrWhiteSpace(connection.ConfigJson))
        {
            connection.Status = IntegrationStatus.Error;
            connection.LastError = "AuthSecretName or McpServerId required.";
            await _db.SaveChangesAsync(cancellationToken);
            return (false, connection.LastError);
        }

        connection.Status = IntegrationStatus.Connected;
        connection.LastError = null;
        await _db.SaveChangesAsync(cancellationToken);
        return (true, "Configuration present. Live API probe deferred until credentials are loaded from Key Vault.");
    }

    public async Task<SyncRun> SyncAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        var connection = await _db.IntegrationConnections.ForTenant(_user)
            .FirstOrDefaultAsync(c => c.Id == connectionId, cancellationToken)
            ?? throw new InvalidOperationException("Integration not found.");

        if (connection.Provider == IntegrationProvider.Halo)
        {
            if (await ResolveMcpServerIdAsync(connection, cancellationToken) is not Guid mcpId)
                return await FailRunAsync(connection, CompactServerMissing("Halo"), cancellationToken);

            try
            {
                var companies = await PullHaloCompaniesAsync(connection, mcpId, cancellationToken);
                return await SyncFromPayloadAsync(connection.Id, companies, cancellationToken);
            }
            catch (Exception ex)
            {
                return await FailRunAsync(connection, ex.Message, cancellationToken);
            }
        }

        if (connection.Provider == IntegrationProvider.NinjaOne)
        {
            if (await ResolveMcpServerIdAsync(connection, cancellationToken) is not Guid mcpId)
                return await FailRunAsync(connection, CompactServerMissing("NinjaOne"), cancellationToken);

            try
            {
                var companies = await PullNinjaCompaniesAsync(mcpId, cancellationToken);

                // Pulled before the company upsert so a device-tool failure fails the run once, in
                // FailRunAsync, instead of leaving a succeeded company run plus a second failed run.
                // The device *upsert* still runs after companies: it needs their mappings to exist.
                IReadOnlyList<ExternalDeviceDto> devices = [];
                if (!connection.SkipAssets)
                    devices = await PullNinjaDevicesAsync(mcpId, cancellationToken);

                var run = await SyncFromPayloadAsync(connection.Id, companies, cancellationToken);
                if (devices.Count > 0 && run.Status == SyncRunStatus.Succeeded)
                    await SyncDevicesAsync(connection, run, devices, cancellationToken);
                return run;
            }
            catch (Exception ex)
            {
                return await FailRunAsync(connection, ex.Message, cancellationToken);
            }
        }

        if (connection.Provider == IntegrationProvider.Cipp)
        {
            if (await ResolveMcpServerIdAsync(connection, cancellationToken) is not Guid mcpId)
                return await FailRunAsync(connection, CompactServerMissing("CIPP"), cancellationToken);

            try
            {
                var companies = await PullCippCompaniesAsync(mcpId, cancellationToken);
                return await SyncFromPayloadAsync(connection.Id, companies, cancellationToken);
            }
            catch (Exception ex)
            {
                return await FailRunAsync(connection, ex.Message, cancellationToken);
            }
        }

        if (connection.Provider == IntegrationProvider.Meraki)
        {
            if (await ResolveMcpServerIdAsync(connection, cancellationToken) is not Guid mcpId)
                return await FailRunAsync(connection, CompactServerMissing("Meraki"), cancellationToken);

            try
            {
                var companies = await PullMerakiCompaniesAsync(mcpId, cancellationToken);
                return await SyncFromPayloadAsync(connection.Id, companies, cancellationToken);
            }
            catch (Exception ex)
            {
                return await FailRunAsync(connection, ex.Message, cancellationToken);
            }
        }

        if (connection.Provider == IntegrationProvider.UniFi)
        {
            if (await ResolveMcpServerIdAsync(connection, cancellationToken) is not Guid mcpId)
                return await FailRunAsync(connection, CompactServerMissing("UniFi"), cancellationToken);

            try
            {
                var companies = await PullUniFiCompaniesAsync(mcpId, cancellationToken);
                return await SyncFromPayloadAsync(connection.Id, companies, cancellationToken);
            }
            catch (Exception ex)
            {
                return await FailRunAsync(connection, ex.Message, cancellationToken);
            }
        }

        if (connection.Provider == IntegrationProvider.Action1)
        {
            if (await ResolveMcpServerIdAsync(connection, cancellationToken) is not Guid mcpId)
                return await FailRunAsync(connection, CompactServerMissing("Action1"), cancellationToken);

            try
            {
                var companies = await PullAction1CompaniesAsync(mcpId, cancellationToken);
                return await SyncFromPayloadAsync(connection.Id, companies, cancellationToken);
            }
            catch (Exception ex)
            {
                return await FailRunAsync(connection, ex.Message, cancellationToken);
            }
        }

        if (connection.Provider == IntegrationProvider.Autotask)
        {
            if (await ResolveMcpServerIdAsync(connection, cancellationToken) is not Guid mcpId)
                return await FailRunAsync(connection, CompactServerMissing("Autotask"), cancellationToken);

            try
            {
                var companies = await PullAutotaskCompaniesAsync(mcpId, cancellationToken);
                return await SyncFromPayloadAsync(connection.Id, companies, cancellationToken);
            }
            catch (Exception ex)
            {
                return await FailRunAsync(connection, ex.Message, cancellationToken);
            }
        }

        if (connection.Provider == IntegrationProvider.Blackpoint)
        {
            if (await ResolveMcpServerIdAsync(connection, cancellationToken) is not Guid mcpId)
                return await FailRunAsync(connection, CompactServerMissing("Blackpoint"), cancellationToken);

            try
            {
                var companies = await PullBlackpointCompaniesAsync(mcpId, cancellationToken);
                return await SyncFromPayloadAsync(connection.Id, companies, cancellationToken);
            }
            catch (Exception ex)
            {
                return await FailRunAsync(connection, ex.Message, cancellationToken);
            }
        }

        if (connection.Provider == IntegrationProvider.DefensX)
        {
            if (await ResolveMcpServerIdAsync(connection, cancellationToken) is not Guid mcpId)
                return await FailRunAsync(connection, CompactServerMissing("DefensX"), cancellationToken);

            try
            {
                var companies = await PullDefensXCompaniesAsync(mcpId, cancellationToken);
                return await SyncFromPayloadAsync(connection.Id, companies, cancellationToken);
            }
            catch (Exception ex)
            {
                return await FailRunAsync(connection, ex.Message, cancellationToken);
            }
        }

        if (connection.Provider == IntegrationProvider.Pax8)
        {
            if (await ResolveMcpServerIdAsync(connection, cancellationToken) is not Guid mcpId)
                return await FailRunAsync(connection, CompactServerMissing("Pax8"), cancellationToken);

            try
            {
                var companies = await PullPax8CompaniesAsync(mcpId, cancellationToken);
                return await SyncFromPayloadAsync(connection.Id, companies, cancellationToken);
            }
            catch (Exception ex)
            {
                return await FailRunAsync(connection, ex.Message, cancellationToken);
            }
        }

        if (connection.Provider == IntegrationProvider.Slide)
        {
            if (await ResolveMcpServerIdAsync(connection, cancellationToken) is not Guid mcpId)
                return await FailRunAsync(connection, CompactServerMissing("Slide"), cancellationToken);

            try
            {
                var companies = await PullSlideClientsAsync(mcpId, cancellationToken);
                return await SyncFromPayloadAsync(connection.Id, companies, cancellationToken);
            }
            catch (Exception ex)
            {
                return await FailRunAsync(connection, ex.Message, cancellationToken);
            }
        }

        if (string.IsNullOrWhiteSpace(connection.AuthSecretName) && connection.McpServerId is null)
        {
            return await FailRunAsync(connection,
                "Configure AuthSecretName (Key Vault) or link an McpServer before syncing.",
                cancellationToken);
        }

        // Composio live pulls are out of scope this slice — payload path remains.
        return await FailRunAsync(connection,
            "No sync payload supplied. Use SyncFromPayload (tests/importers) or wire MCP tool results into company upsert.",
            cancellationToken);
    }

    /// <summary>
    /// The MCP server this connection speaks through. StackJack Compact is built in, so a
    /// Compact-backed connection that carries no <see cref="IntegrationConnection.McpServerId"/> —
    /// created before Compact became the default, or created with an explicit null — adopts the
    /// tenant's Compact registration here and keeps it. This only ever <em>resolves</em>: creating a
    /// registration needs a Key Vault secret name, which only the create endpoint is given.
    /// </summary>
    private async Task<Guid?> ResolveMcpServerIdAsync(IntegrationConnection connection, CancellationToken cancellationToken)
    {
        if (connection.McpServerId is Guid linked)
            return linked;

        if (!McpServerDefaults.IsCompactBacked(connection.Provider))
            return null;

        // Same ordering as the create endpoint, so both land on the same server for a tenant that
        // somehow registered more than one: an enabled server first, then the oldest.
        var compact = await _db.McpServers.ForTenant(_user)
            .Where(s => s.Kind == McpServerKind.StackJackCompact)
            .OrderByDescending(s => s.Enabled)
            .ThenBy(s => s.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (compact is null)
            return null;

        connection.McpServerId = compact.Id;
        await _db.SaveChangesAsync(cancellationToken);
        return compact.Id;
    }

    private static string CompactServerMissing(string providerName)
        => $"{providerName} sync runs through StackJack Compact and this tenant has no Compact MCP server registered. "
            + "Add the integration again with a Key Vault secret name, or link one with McpServerId. "
            + "AuthSecretName is a Key Vault name only; secrets are never stored in SQL.";

    /// <summary>
    /// Reads the StackJack tier and monthly allowance for this connector and stamps them on the
    /// connection. Returns one sentence for the test result and never throws: <c>session_info</c>
    /// being unavailable says nothing about whether the connection itself works.
    /// </summary>
    private async Task<string> DetectPlanAsync(
        IntegrationConnection connection,
        Guid mcpServerId,
        CancellationToken cancellationToken)
    {
        var connector = StackJackPlanDetector.ConnectorName(connection.Provider);
        if (connector is null)
            return "No StackJack connector covers this provider, so no plan was detected.";

        try
        {
            var detected = await StackJackPlanDetector.DetectAsync(
                _mcpClient, mcpServerId, connection.Provider, cancellationToken);
            connection.PlanDetectedAt = DateTimeOffset.UtcNow;

            if (detected is null)
            {
                // A session that answered and did not list the connector is authoritative: this
                // StackJack key holds no subscription for it. Say so rather than keep a stale tier.
                connection.StackJackPlan = StackJackPlan.Unknown;
                connection.MonthlyCallLimit = null;
                return $"StackJack lists no {connector} subscription on this key, so the plan is unknown.";
            }

            connection.StackJackPlan = detected.Plan;
            connection.MonthlyCallLimit = detected.MonthlyCallLimit;
            var credentials = detected.HasCredentials
                ? string.Empty
                : "; StackJack holds no credentials for it yet";
            return $"StackJack plan {detected.Plan} ({DescribeAllowance(detected.MonthlyCallLimit)})"
                + $"{DescribeCadence(connection)}{credentials}.";
        }
        catch (Exception ex)
        {
            // Plan, limit and PlanDetectedAt are left exactly as they were: a stale but real tier
            // beats one overwritten from a read that failed, and the timestamp keeps saying when the
            // last good read happened.
            return $"Plan not detected ({ex.Message}).";
        }
    }

    private static string DescribeAllowance(int? monthlyCallLimit)
    {
        if (monthlyCallLimit is not int limit)
            return "allowance not reported";
        return limit >= StackJackPlanDetector.UnlimitedCallLimit
            ? "unlimited calls per cycle"
            : $"{limit:N0} calls per cycle";
    }

    private static string DescribeCadence(IntegrationConnection connection)
    {
        var minutes = SyncCadencePolicy.IntervalMinutesFor(connection);
        return minutes is int interval
            ? $", which schedules a check every {interval} min"
            : ", too little to suggest a cadence";
    }

    public async Task<SyncRun> SyncFromPayloadAsync(
        Guid connectionId,
        IReadOnlyList<ExternalCompanyDto> companies,
        CancellationToken cancellationToken = default)
    {
        var connection = await _db.IntegrationConnections.ForTenant(_user)
            .FirstOrDefaultAsync(c => c.Id == connectionId, cancellationToken)
            ?? throw new InvalidOperationException("Integration not found.");

        var run = await StartRunAsync(connection, cancellationToken);
        connection.Status = IntegrationStatus.Syncing;

        try
        {
            // Companies only. SkipContacts/SkipLocations still document intent for later live pulls;
            // SkipAssets/AutoUpdateAssetNames are honoured by the Ninja device pass in SyncAsync,
            // which runs after this one so device→company mappings already exist.
            var providerKey = CompanyIdentity.ProviderKey(connection.Provider);
            var index = new CompanyMatchIndex(
                await _db.Companies.ForTenant(_user).ToListAsync(cancellationToken));

            // One external record per company per connection. Without this, two rows from the same
            // provider that share a normalized name or domain ("Acme" and "ACME") would both adopt
            // the company the first row created, and the second client would never get one.
            var claimedCompanyIds = (await _db.IntegrationMappings.ForTenant(_user)
                .Where(m => m.IntegrationConnectionId == connection.Id && m.ExternalType == "company")
                .Select(m => m.LocalEntityId)
                .ToListAsync(cancellationToken)).ToHashSet();

            foreach (var dto in companies)
            {
                if (connection.SkipInactive && dto.IsInactive == true)
                {
                    run.ItemsSkipped++;
                    continue;
                }

                var mapping = await _db.IntegrationMappings.ForTenant(_user)
                    .FirstOrDefaultAsync(m =>
                        m.IntegrationConnectionId == connection.Id
                        && m.ExternalType == "company"
                        && m.ExternalId == dto.ExternalId, cancellationToken);

                Company company;
                if (mapping is null)
                {
                    // Another provider may already own this client. Adopt it instead of duplicating.
                    var match = index.Find(providerKey, dto);
                    if (match is not null && !claimedCompanyIds.Contains(match.Company.Id))
                    {
                        company = match.Company;
                        ApplyDetails(company, dto, connection);
                        StampExternalId(company, connection, providerKey, dto.ExternalId);
                        _db.IntegrationMappings.Add(new IntegrationMapping
                        {
                            TenantId = _user.TenantId!.Value,
                            IntegrationConnectionId = connection.Id,
                            ExternalId = dto.ExternalId,
                            ExternalType = "company",
                            LocalEntityType = nameof(Company),
                            LocalEntityId = company.Id,
                            MetadataJson = JsonSerializer.Serialize(new { matchedBy = match.Reason }),
                        });
                        index.Add(company);
                        claimedCompanyIds.Add(company.Id);
                        run.ItemsUpdated++;
                        continue;
                    }

                    var slug = string.IsNullOrWhiteSpace(dto.Slug)
                        ? Slugify(dto.Name)
                        : dto.Slug!;

                    company = new Company
                    {
                        TenantId = _user.TenantId!.Value,
                        Name = dto.Name,
                        Slug = await EnsureUniqueSlugAsync(slug, cancellationToken),
                        PrimaryDomain = dto.PrimaryDomain,
                        City = dto.City,
                        State = dto.State,
                        Website = dto.Website,
                        Address = dto.Address,
                    };
                    StampExternalId(company, connection, providerKey, dto.ExternalId);
                    _db.Companies.Add(company);
                    await _db.SaveChangesAsync(cancellationToken);

                    _db.IntegrationMappings.Add(new IntegrationMapping
                    {
                        TenantId = _user.TenantId!.Value,
                        IntegrationConnectionId = connection.Id,
                        ExternalId = dto.ExternalId,
                        ExternalType = "company",
                        LocalEntityType = nameof(Company),
                        LocalEntityId = company.Id,
                    });
                    index.Add(company);
                    claimedCompanyIds.Add(company.Id);
                    run.ItemsCreated++;
                }
                else
                {
                    company = await _db.Companies.ForTenant(_user)
                        .FirstAsync(c => c.Id == mapping.LocalEntityId, cancellationToken);
                    ApplyDetails(company, dto, connection);
                    StampExternalId(company, connection, providerKey, dto.ExternalId);
                    index.Add(company);
                    claimedCompanyIds.Add(company.Id);
                    run.ItemsUpdated++;
                }
            }

            await _db.SaveChangesAsync(cancellationToken);
            run.Status = SyncRunStatus.Succeeded;
            run.FinishedAt = DateTimeOffset.UtcNow;
            connection.Status = IntegrationStatus.Connected;
            connection.LastSyncAt = run.FinishedAt;
            connection.LastError = null;
            await _audit.LogAsync("Integration.Sync", nameof(IntegrationConnection), connection.Id,
                $"created={run.ItemsCreated} updated={run.ItemsUpdated}", cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            return run;
        }
        catch (Exception ex)
        {
            run.Status = SyncRunStatus.Failed;
            run.FinishedAt = DateTimeOffset.UtcNow;
            run.ErrorSummary = ex.Message;
            connection.Status = IntegrationStatus.Error;
            connection.LastError = ex.Message;
            await _db.SaveChangesAsync(cancellationToken);
            return run;
        }
    }

    /// <summary>
    /// Upserts devices onto <see cref="Asset"/> rows for an already-synced connection. Runs after the
    /// company upsert in the same <see cref="SyncRun"/> and contributes to the same counters.
    /// </summary>
    private async Task SyncDevicesAsync(
        IntegrationConnection connection,
        SyncRun run,
        IReadOnlyList<ExternalDeviceDto> devices,
        CancellationToken cancellationToken)
    {
        try
        {
            // A device attaches to whatever company this connection mapped its organization to.
            // Built as an assignment loop, not ToDictionary: a duplicate external id must not throw
            // and abort an otherwise good run.
            var companyByOrganization = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            var companyMappings = await _db.IntegrationMappings.ForTenant(_user)
                .Where(m => m.IntegrationConnectionId == connection.Id
                    && m.ExternalType == "company"
                    && m.LocalEntityType == nameof(Company))
                .Select(m => new { m.ExternalId, m.LocalEntityId })
                .ToListAsync(cancellationToken);
            foreach (var companyMapping in companyMappings)
                companyByOrganization[companyMapping.ExternalId] = companyMapping.LocalEntityId;

            var deviceMappings = await _db.IntegrationMappings.ForTenant(_user)
                .Where(m => m.IntegrationConnectionId == connection.Id
                    && m.ExternalType == DeviceExternalType
                    && m.LocalEntityType == nameof(Asset))
                .ToListAsync(cancellationToken);
            var mappingByDevice = new Dictionary<string, IntegrationMapping>(StringComparer.OrdinalIgnoreCase);
            foreach (var deviceMapping in deviceMappings)
                mappingByDevice[deviceMapping.ExternalId] = deviceMapping;

            // Loaded up front: a tenant can have thousands of devices and one query per device would
            // make a sync unusable.
            var mappedAssetIds = deviceMappings.Select(m => m.LocalEntityId).Distinct().ToList();
            var assetsById = (await _db.Assets.ForTenant(_user)
                .Where(a => mappedAssetIds.Contains(a.Id))
                .ToListAsync(cancellationToken))
                .ToDictionary(a => a.Id);

            Guid? computerAssetTypeId = null;

            foreach (var dto in devices)
            {
                if (!companyByOrganization.TryGetValue(dto.OrganizationExternalId, out var companyId))
                {
                    // Organization never mapped to a company on this connection (skipped as inactive,
                    // or created in Ninja after the org page was read). Skip rather than leave an
                    // orphan asset nobody can find from a company page.
                    run.ItemsSkipped++;
                    continue;
                }

                if (mappingByDevice.TryGetValue(dto.ExternalId, out var mapping)
                    && assetsById.TryGetValue(mapping.LocalEntityId, out var existing))
                {
                    // AutoUpdateAssetNames is exactly this switch: default off means a local rename sticks.
                    if (connection.AutoUpdateAssetNames)
                        existing.Name = dto.Name;
                    existing.CompanyId = companyId;
                    mapping.MetadataJson = DeviceMetadataJson(dto);
                    run.ItemsUpdated++;
                    continue;
                }

                computerAssetTypeId ??= (await EnsureComputerAssetTypeAsync(cancellationToken)).Id;

                var asset = new Asset
                {
                    TenantId = _user.TenantId!.Value,
                    Name = dto.Name,
                    CompanyId = companyId,
                    AssetTypeId = computerAssetTypeId.Value,
                };
                _db.Assets.Add(asset);
                assetsById[asset.Id] = asset;

                if (mapping is null)
                {
                    mapping = new IntegrationMapping
                    {
                        TenantId = _user.TenantId!.Value,
                        IntegrationConnectionId = connection.Id,
                        ExternalId = dto.ExternalId,
                        ExternalType = DeviceExternalType,
                        LocalEntityType = nameof(Asset),
                        LocalEntityId = asset.Id,
                    };
                    _db.IntegrationMappings.Add(mapping);
                    mappingByDevice[dto.ExternalId] = mapping;
                }
                else
                {
                    // Mapping outlived its asset (deleted locally). Re-point it instead of adding a
                    // second mapping row for the same device.
                    mapping.LocalEntityId = asset.Id;
                }

                mapping.MetadataJson = DeviceMetadataJson(dto);
                run.ItemsCreated++;
            }

            await _db.SaveChangesAsync(cancellationToken);
            run.FinishedAt = DateTimeOffset.UtcNow;
            connection.LastSyncAt = run.FinishedAt;
            // These are the run's cumulative totals, not device-only counts: the company pass already
            // added to the same counters. Labelled so the audit trail does not overstate the device pass.
            await _audit.LogAsync("Integration.SyncDevices", nameof(IntegrationConnection), connection.Id,
                $"runTotals created={run.ItemsCreated} updated={run.ItemsUpdated} skipped={run.ItemsSkipped}", cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            run.Status = SyncRunStatus.Failed;
            run.FinishedAt = DateTimeOffset.UtcNow;
            run.ErrorSummary = ex.Message;
            connection.Status = IntegrationStatus.Error;
            connection.LastError = ex.Message;
            await SaveFailureAsync(run, connection, cancellationToken);
        }
    }

    /// <summary>
    /// Persists a failed device pass without re-throwing. If the original failure was itself a
    /// <c>SaveChanges</c> failure, the ChangeTracker still holds the entities that caused it, so saving
    /// again would throw straight out of the catch, past <c>SyncAsync</c>, leaving the run recorded as
    /// whatever it was before -- Succeeded, in the common case. Detach everything except the run and the
    /// connection first, and swallow a second failure rather than lose the caller's result.
    /// </summary>
    private async Task SaveFailureAsync(SyncRun run, IntegrationConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var entry in _db.ChangeTracker.Entries().ToList())
            {
                if (!ReferenceEquals(entry.Entity, run) && !ReferenceEquals(entry.Entity, connection))
                    entry.State = EntityState.Detached;
            }

            await _db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // The failure is already on the run object in memory; losing this write is strictly better
            // than throwing out of a catch block and returning no run at all.
        }
    }

    /// <summary>Finds (case-insensitively) or creates the tenant's <see cref="ComputerAssetTypeName"/> layout.</summary>
    private async Task<AssetType> EnsureComputerAssetTypeAsync(CancellationToken cancellationToken)
    {
        // Matched in memory, not in SQL: asset types are a handful of rows per tenant, and this keeps
        // the comparison identical under SQL Server collation and the in-memory provider.
        var assetTypes = await _db.AssetTypes.ForTenant(_user).ToListAsync(cancellationToken);
        var existing = assetTypes.FirstOrDefault(t =>
            string.Equals(t.Name, ComputerAssetTypeName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return existing;

        var assetType = new AssetType
        {
            TenantId = _user.TenantId!.Value,
            Name = ComputerAssetTypeName,
            Description = "Devices synced from RMM integrations.",
        };
        _db.AssetTypes.Add(assetType);
        await _db.SaveChangesAsync(cancellationToken);
        return assetType;
    }

    /// <summary>Remote detail kept on the mapping, not on the asset — it must never clobber a tech's edits.</summary>
    private static string DeviceMetadataJson(ExternalDeviceDto dto)
        => JsonSerializer.Serialize(new
        {
            organizationId = dto.OrganizationExternalId,
            nodeClass = dto.NodeClass,
            systemName = dto.SystemName,
            dnsName = dto.DnsName,
        });

    private async Task<IReadOnlyList<ExternalCompanyDto>> PullHaloCompaniesAsync(
        IntegrationConnection connection,
        Guid mcpServerId,
        CancellationToken cancellationToken)
    {
        var server = await _db.McpServers.ForTenant(_user)
            .FirstOrDefaultAsync(s => s.Id == mcpServerId, cancellationToken)
            ?? throw new InvalidOperationException("MCP server not found.");

        if (server.Kind != McpServerKind.StackJackCompact)
            throw new InvalidOperationException("Halo company pull requires a StackJack Compact MCP server. Composio is not a Halo connector.");

        const int maxPages = 500;
        var companies = new List<ExternalCompanyDto>();
        for (var pageNo = 1; pageNo <= maxPages; pageNo++)
        {
            var args = HaloClientMapper.BuildArgumentsJson(pageNo, connection.SkipInactive);
            var body = await _mcpClient.CallToolAsync(mcpServerId, HaloClientMapper.ToolName, args, cancellationToken);
            var page = HaloClientMapper.MapClients(body, out var rowCount);
            companies.AddRange(page);
            // Raw rows, never mapped rows: a client with no id or name is dropped, and testing the
            // mapped count would read that short page as the last one and abandon the rest.
            if (rowCount < HaloClientMapper.DefaultPageSize)
                break;
        }

        return companies;
    }

    private async Task<IReadOnlyList<ExternalCompanyDto>> PullNinjaCompaniesAsync(
        Guid mcpServerId,
        CancellationToken cancellationToken)
    {
        var server = await _db.McpServers.ForTenant(_user)
            .FirstOrDefaultAsync(s => s.Id == mcpServerId, cancellationToken)
            ?? throw new InvalidOperationException("MCP server not found.");

        if (server.Kind != McpServerKind.StackJackCompact)
            throw new InvalidOperationException("NinjaOne company pull requires a StackJack Compact MCP server. Composio is not a NinjaOne connector.");

        return await NinjaOrganizationMapper.PullAsync(_mcpClient, mcpServerId, cancellationToken: cancellationToken);
    }

    private async Task<IReadOnlyList<ExternalDeviceDto>> PullNinjaDevicesAsync(
        Guid mcpServerId,
        CancellationToken cancellationToken)
    {
        var server = await _db.McpServers.ForTenant(_user)
            .FirstOrDefaultAsync(s => s.Id == mcpServerId, cancellationToken)
            ?? throw new InvalidOperationException("MCP server not found.");

        if (server.Kind != McpServerKind.StackJackCompact)
            throw new InvalidOperationException("NinjaOne device pull requires a StackJack Compact MCP server. Composio is not a NinjaOne connector.");

        return await NinjaDeviceMapper.PullAsync(_mcpClient, mcpServerId, cancellationToken: cancellationToken);
    }

    private async Task<IReadOnlyList<ExternalCompanyDto>> PullCippCompaniesAsync(
        Guid mcpServerId,
        CancellationToken cancellationToken)
    {
        var server = await _db.McpServers.ForTenant(_user)
            .FirstOrDefaultAsync(s => s.Id == mcpServerId, cancellationToken)
            ?? throw new InvalidOperationException("MCP server not found.");

        if (server.Kind != McpServerKind.StackJackCompact)
            throw new InvalidOperationException("CIPP tenant pull requires a StackJack Compact MCP server. Composio is not a CIPP connector.");

        return await CippTenantMapper.PullAsync(_mcpClient, mcpServerId, cancellationToken);
    }

    private async Task<IReadOnlyList<ExternalCompanyDto>> PullMerakiCompaniesAsync(
        Guid mcpServerId,
        CancellationToken cancellationToken)
    {
        var server = await _db.McpServers.ForTenant(_user)
            .FirstOrDefaultAsync(s => s.Id == mcpServerId, cancellationToken)
            ?? throw new InvalidOperationException("MCP server not found.");

        if (server.Kind != McpServerKind.StackJackCompact)
            throw new InvalidOperationException("Meraki organization pull requires a StackJack Compact MCP server. Composio is not a Meraki connector.");

        return await MerakiOrganizationMapper.PullAsync(_mcpClient, mcpServerId, cancellationToken: cancellationToken);
    }

    private async Task<IReadOnlyList<ExternalCompanyDto>> PullUniFiCompaniesAsync(
        Guid mcpServerId,
        CancellationToken cancellationToken)
    {
        var server = await _db.McpServers.ForTenant(_user)
            .FirstOrDefaultAsync(s => s.Id == mcpServerId, cancellationToken)
            ?? throw new InvalidOperationException("MCP server not found.");

        if (server.Kind != McpServerKind.StackJackCompact)
            throw new InvalidOperationException("UniFi host pull requires a StackJack Compact MCP server. Composio is not a UniFi connector.");

        return await UnifiHostMapper.PullAsync(_mcpClient, mcpServerId, cancellationToken: cancellationToken);
    }

    private async Task<IReadOnlyList<ExternalCompanyDto>> PullAction1CompaniesAsync(
        Guid mcpServerId,
        CancellationToken cancellationToken)
    {
        var server = await _db.McpServers.ForTenant(_user)
            .FirstOrDefaultAsync(s => s.Id == mcpServerId, cancellationToken)
            ?? throw new InvalidOperationException("MCP server not found.");

        if (server.Kind != McpServerKind.StackJackCompact)
            throw new InvalidOperationException("Action1 organization pull requires a StackJack Compact MCP server. Composio is not an Action1 connector.");

        return await Action1OrganizationMapper.PullAsync(_mcpClient, mcpServerId, cancellationToken: cancellationToken);
    }

    private async Task<IReadOnlyList<ExternalCompanyDto>> PullAutotaskCompaniesAsync(
        Guid mcpServerId,
        CancellationToken cancellationToken)
    {
        var server = await _db.McpServers.ForTenant(_user)
            .FirstOrDefaultAsync(s => s.Id == mcpServerId, cancellationToken)
            ?? throw new InvalidOperationException("MCP server not found.");

        if (server.Kind != McpServerKind.StackJackCompact)
            throw new InvalidOperationException("Autotask company pull requires a StackJack Compact MCP server. Composio is not an Autotask connector.");

        return await AutotaskCompanyMapper.PullAsync(_mcpClient, mcpServerId, cancellationToken: cancellationToken);
    }

    private async Task<IReadOnlyList<ExternalCompanyDto>> PullBlackpointCompaniesAsync(
        Guid mcpServerId,
        CancellationToken cancellationToken)
    {
        var server = await _db.McpServers.ForTenant(_user)
            .FirstOrDefaultAsync(s => s.Id == mcpServerId, cancellationToken)
            ?? throw new InvalidOperationException("MCP server not found.");

        if (server.Kind != McpServerKind.StackJackCompact)
            throw new InvalidOperationException("Blackpoint tenant pull requires a StackJack Compact MCP server. Composio is not a CompassOne connector.");

        return await CompassOneTenantMapper.PullAsync(_mcpClient, mcpServerId, cancellationToken: cancellationToken);
    }

    private async Task<IReadOnlyList<ExternalCompanyDto>> PullDefensXCompaniesAsync(
        Guid mcpServerId,
        CancellationToken cancellationToken)
    {
        var server = await _db.McpServers.ForTenant(_user)
            .FirstOrDefaultAsync(s => s.Id == mcpServerId, cancellationToken)
            ?? throw new InvalidOperationException("MCP server not found.");

        if (server.Kind != McpServerKind.StackJackCompact)
            throw new InvalidOperationException("DefensX customer pull requires a StackJack Compact MCP server. Composio is not a DefensX connector.");

        return await DefensXCustomerMapper.PullAsync(_mcpClient, mcpServerId, cancellationToken);
    }

    private async Task<IReadOnlyList<ExternalCompanyDto>> PullPax8CompaniesAsync(
        Guid mcpServerId,
        CancellationToken cancellationToken)
    {
        var server = await _db.McpServers.ForTenant(_user)
            .FirstOrDefaultAsync(s => s.Id == mcpServerId, cancellationToken)
            ?? throw new InvalidOperationException("MCP server not found.");

        if (server.Kind != McpServerKind.StackJackCompact)
            throw new InvalidOperationException("Pax8 company pull requires a StackJack Compact MCP server. Composio is not a Pax8 connector.");

        return await Pax8CompanyMapper.PullAsync(_mcpClient, mcpServerId, cancellationToken: cancellationToken);
    }

    private async Task<IReadOnlyList<ExternalCompanyDto>> PullSlideClientsAsync(
        Guid mcpServerId,
        CancellationToken cancellationToken)
    {
        var server = await _db.McpServers.ForTenant(_user)
            .FirstOrDefaultAsync(s => s.Id == mcpServerId, cancellationToken)
            ?? throw new InvalidOperationException("MCP server not found.");

        if (server.Kind != McpServerKind.StackJackCompact)
            throw new InvalidOperationException("Slide client pull requires a StackJack Compact MCP server. Composio is not a Slide connector.");

        return await SlideClientMapper.PullAsync(_mcpClient, mcpServerId, cancellationToken: cancellationToken);
    }

    /// <summary>Overwrites local company detail only when the connection opts in. Default is refuse-to-clobber.</summary>
    private static void ApplyDetails(Company company, ExternalCompanyDto dto, IntegrationConnection connection)
    {
        if (!connection.UpdateCompanyDetails)
            return;

        company.Name = dto.Name;
        company.PrimaryDomain = dto.PrimaryDomain ?? company.PrimaryDomain;
        company.City = dto.City ?? company.City;
        company.State = dto.State ?? company.State;
        company.Website = dto.Website ?? company.Website;
        company.Address = dto.Address ?? company.Address;
    }

    /// <summary>
    /// Records provider identity on the company: the typed Halo/Ninja columns where they exist,
    /// and <see cref="Company.ExternalIdsJson"/> for every provider so later runs can match on it.
    /// </summary>
    private static void StampExternalId(Company company, IntegrationConnection connection, string providerKey, string externalId)
    {
        if (connection.Provider == IntegrationProvider.Halo
            && (connection.UpdateCompanyDetails || string.IsNullOrEmpty(company.HaloClientId)))
            company.HaloClientId = externalId;

        if (connection.Provider == IntegrationProvider.NinjaOne
            && (connection.UpdateCompanyDetails || string.IsNullOrEmpty(company.NinjaOrganizationId)))
            company.NinjaOrganizationId = externalId;

        // Guarded exactly like the typed columns above: without this the JSON would disagree with
        // HaloClientId/NinjaOrganizationId, and providers with no typed column would lose their
        // original id -- which is what provider-id matching relies on.
        if (connection.UpdateCompanyDetails
            || !CompanyIdentity.ReadExternalIds(company.ExternalIdsJson).ContainsKey(providerKey))
        {
            company.ExternalIdsJson = CompanyIdentity.UpsertExternalId(company.ExternalIdsJson, providerKey, externalId);
        }
    }

    private async Task<SyncRun> FailRunAsync(IntegrationConnection connection, string error, CancellationToken cancellationToken)
    {
        var run = await StartRunAsync(connection, cancellationToken);
        run.Status = SyncRunStatus.Failed;
        run.FinishedAt = DateTimeOffset.UtcNow;
        run.ErrorSummary = error;
        connection.Status = IntegrationStatus.Error;
        connection.LastError = error;
        await _db.SaveChangesAsync(cancellationToken);
        return run;
    }

    private async Task<SyncRun> StartRunAsync(IntegrationConnection connection, CancellationToken cancellationToken)
    {
        var run = new SyncRun
        {
            TenantId = _user.TenantId!.Value,
            IntegrationConnectionId = connection.Id,
            StartedAt = DateTimeOffset.UtcNow,
            Status = SyncRunStatus.Running,
        };
        _db.SyncRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken);
        return run;
    }

    private async Task<string> EnsureUniqueSlugAsync(string slug, CancellationToken cancellationToken)
    {
        var candidate = slug;
        var i = 2;
        while (await _db.Companies.ForTenant(_user).AnyAsync(c => c.Slug == candidate, cancellationToken))
        {
            candidate = $"{slug}-{i}";
            i++;
        }
        return candidate;
    }

    private static string Slugify(string name)
    {
        var chars = name.Trim().ToLowerInvariant().Select(ch =>
            char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Trim('-');
    }
}
