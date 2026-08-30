import useSWR, { mutate } from 'swr'
import { acquireApiToken } from '../auth/msalConfig'

/** Thrown for any non-2xx API response. Carries the HTTP status and the response body. */
export class ApiError extends Error {
  readonly status: number
  readonly body: string

  constructor(status: number, statusText: string, body: string) {
    super(body ? `Request failed (${status}): ${body}` : `Request failed (${status}${statusText ? ` ${statusText}` : ''})`)
    this.name = 'ApiError'
    this.status = status
    this.body = body
  }
}

function describeBody(contentType: string | null, text: string) {
  if (!text) return ''
  if (contentType?.includes('json')) {
    try {
      const parsed = JSON.parse(text) as string | { detail?: string; title?: string; message?: string }
      if (typeof parsed === 'string') return parsed
      return parsed.detail ?? parsed.title ?? parsed.message ?? text
    } catch {
      return text
    }
  }
  return text
}

/**
 * Single entry point for every API call: acquires an Entra access token, attaches
 * it as a bearer token and turns any non-2xx response into an ApiError.
 */
async function apiFetch(url: string, init?: RequestInit): Promise<Response> {
  const token = await acquireApiToken()
  const headers = new Headers(init?.headers)
  headers.set('Authorization', `Bearer ${token}`)
  const res = await fetch(url, { ...init, headers })
  if (!res.ok) {
    let text = ''
    try {
      text = await res.text()
    } catch {
      text = ''
    }
    throw new ApiError(res.status, res.statusText, describeBody(res.headers.get('content-type'), text).trim())
  }
  return res
}

async function readJson<T>(res: Response): Promise<T> {
  const text = await res.text()
  if (!text) {
    return undefined as T
  }
  return JSON.parse(text) as T
}

const fetcher = async (url: string): Promise<any> => readJson(await apiFetch(url))

export type RelatedListItem = {
  id: string
  name: string
  updatedAt?: string
  runCount?: number | null
}

export type CompanyCounts = {
  assets: number
  documents: number
  runbooks: number
  keeperLinks: number
  relatedLinks?: number
}

export type RelatedLinkItem = {
  id: string
  entityType: string
  entityId: string
  name: string
  label?: string | null
}

export type Company = {
  id: string
  name: string
  slug: string
  companyNumber?: string | null
  companyType?: string | null
  nickname?: string | null
  parentCompanyId?: string | null
  primaryDomain?: string | null
  address?: string | null
  city?: string | null
  state?: string | null
  country?: string | null
  postalCode?: string | null
  phone?: string | null
  fax?: string | null
  website?: string | null
  notes?: string | null
  hoursOfOperation?: string | null
  isActive?: boolean
  portalEnabled?: boolean
  haloClientId?: string | null
  ninjaOrganizationId?: string | null
  haloPortalUrl?: string | null
  ninjaPortalUrl?: string | null
  counts?: CompanyCounts | null
  assets?: RelatedListItem[] | null
  documents?: RelatedListItem[] | null
  runbooks?: RelatedListItem[] | null
  keeperLinks?: RelatedListItem[] | null
  relatedLinks?: RelatedLinkItem[] | null
}

export type CreateCompanyInput = {
  name: string
  slug: string
  parentCompanyId?: string | null
  companyType?: string | null
  nickname?: string | null
  haloClientId?: string | null
  ninjaOrganizationId?: string | null
  haloPortalUrl?: string | null
  ninjaPortalUrl?: string | null
}

export type UpdateCompanyInput = {
  name?: string | null
  slug?: string | null
  haloClientId?: string | null
  ninjaOrganizationId?: string | null
  haloPortalUrl?: string | null
  ninjaPortalUrl?: string | null
}

export type McpTransport = 'Http' | 'Sse' | 'Stdio'

export type McpServerKind = 'StackJackCompact' | 'Composio'

export const MCP_ENDPOINTS: Record<McpServerKind, string> = {
  StackJackCompact: 'https://compact.stackjack.io/mcp',
  Composio: 'https://connect.composio.dev/mcp',
}

export type McpServer = {
  id: string
  name: string
  kind?: string
  transport: string
  endpointUrl?: string | null
  command?: string | null
  authSecretName?: string | null
  enabled?: boolean
}

export type CreateMcpServerInput = {
  name: string
  kind: McpServerKind
  transport: McpTransport
  endpointUrl?: string | null
  authSecretName?: string | null
  enabled: boolean
}

export type SyncPolicy = {
  skipInactive: boolean
  skipContacts: boolean
  skipLocations: boolean
  skipAssets: boolean
  autoUpdateAssetNames: boolean
  updateCompanyDetails: boolean
}

/** int.MaxValue — how StackJack reports an unlimited connector allowance. */
export const UNLIMITED_CALL_LIMIT = 2147483647

export type IntegrationConnection = {
  id: string
  provider: string
  displayName?: string | null
  status?: string | null
  lastSyncAt?: string | null
  lastError?: string | null
  mcpServerId?: string | null
  authSecretName?: string | null
  isEnabled?: boolean
  /** StackJack tier for this connector, detected during Test. 'Unknown' until then. */
  stackJackPlan?: string | null
  /** Successful tool calls per billing cycle, as reported by StackJack. UNLIMITED_CALL_LIMIT means unlimited. */
  monthlyCallLimit?: number | null
  planDetectedAt?: string | null
  syncIntervalMinutesOverride?: number | null
  /** Derived server-side from the allowance and the override. Null means manual only. */
  syncIntervalMinutes?: number | null
  /** When a check at that cadence would next fall due for the background scheduler. */
  nextSyncDueAt?: string | null
} & Partial<SyncPolicy>

export type IntegrationProvider = 'Halo' | 'NinjaOne' | 'UniFi' | 'Blackpoint' | 'CustomMcp' | 'Cipp' | 'Meraki' | 'Composio' | 'Action1' | 'Autotask' | 'DefensX'

const compactProviders: IntegrationProvider[] = ['Halo', 'NinjaOne', 'Cipp', 'Meraki', 'UniFi', 'Blackpoint', 'Action1', 'Autotask', 'DefensX']

export function mcpKindForProvider(provider: IntegrationProvider): McpServerKind | null {
  if (provider === 'Composio') return 'Composio'
  if (provider === 'CustomMcp') return null
  if (compactProviders.includes(provider)) return 'StackJackCompact'
  return 'StackJackCompact'
}

export type CreateIntegrationInput = {
  provider: IntegrationProvider
  displayName: string
  authSecretName?: string | null
  mcpServerId?: string | null
  isEnabled?: boolean
} & Partial<SyncPolicy>

export type UpdateIntegrationInput = {
  displayName?: string | null
  authSecretName?: string | null
  mcpServerId?: string | null
  isEnabled?: boolean
  /** Minutes between scheduled checks. Omit to leave as-is; 0 clears the override. */
  syncIntervalMinutesOverride?: number
} & Partial<SyncPolicy>

/** Tenant-wide roles as they travel on the wire (JsonStringEnumConverter). */
export type UserRole = 'None' | 'Reader' | 'Contributor' | 'Admin' | 'Owner'

export const USER_ROLES: UserRole[] = ['None', 'Reader', 'Contributor', 'Admin', 'Owner']

export function canManageUsers(role?: string | null): boolean {
  return role === 'Admin' || role === 'Owner'
}

export type Profile = {
  id?: string
  entraObjectId?: string
  objectId?: string
  email?: string
  displayName?: string
  role?: UserRole
  lastSeenAt?: string
  onboardingRequired?: boolean
  tenant?: { id: string; name: string; slug: string; primaryDomain?: string } | null
}

export type TenantUser = {
  id: string
  entraObjectId: string
  email: string
  displayName?: string | null
  role: UserRole
  isActive: boolean
  lastSeenAt?: string | null
}

export function useProfile() {
  return useSWR<Profile>('/api/me', fetcher)
}

/** Admin-gated roster. Pass false to skip the request (Reader/Contributor). */
export function useUsers(enabled = true) {
  return useSWR<TenantUser[]>(enabled ? '/api/users' : null, fetcher)
}

/** PUT /api/users/{id}/role — body is `{ "role": "Contributor" }`. 204 on success. */
export function updateUserRole(id: string, role: UserRole) {
  return putJson(`/api/users/${id}/role`, { role })
}

export type RecentItem = {
  entityType: string
  id: string
  name: string
  companyId?: string | null
  companyName?: string | null
  updatedAt: string
}

export function useRecents() {
  return useSWR<RecentItem[]>('/api/me/recents', fetcher)
}

export type ExpirationItem = {
  sourceType: 'AssetField' | 'Asset' | string
  id: string
  name: string
  companyId?: string | null
  companyName?: string | null
  fieldName: string
  expiresAt: string
  daysUntil: number
}

export function useExpirations(opts?: { q?: string; showExpired?: boolean; companyId?: string }) {
  const params = new URLSearchParams()
  if (opts?.showExpired) params.set('showExpired', 'true')
  const term = opts?.q?.trim()
  if (term) params.set('q', term)
  if (opts?.companyId) params.set('companyId', opts.companyId)
  const qs = params.toString()
  return useSWR<ExpirationItem[]>(`/api/expirations${qs ? `?${qs}` : ''}`, fetcher)
}

export type FlagDefinition = {
  id: string
  name: string
  color: string
  isActive: boolean
  createdAt?: string
  updatedAt?: string
}

export type FlagReviewItem = {
  assignmentId: string
  flagDefinitionId: string
  flagName: string
  flagColor: string
  entityType: string
  entityId: string
  entityName: string
  companyId?: string | null
  companyName?: string | null
  createdAt: string
}

export function useFlags() {
  return useSWR<FlagDefinition[]>('/api/flags', fetcher)
}

export function useFlagReview(entityType?: string) {
  const params = new URLSearchParams()
  if (entityType) params.set('entityType', entityType)
  const qs = params.toString()
  return useSWR<FlagReviewItem[]>(`/api/flags/review${qs ? `?${qs}` : ''}`, fetcher)
}

export function createFlag(input: { name: string; color: string; isActive?: boolean }) {
  return postJson<FlagDefinition>('/api/flags', input)
}

export function useAssets() {
  return useSWR('/api/assets', fetcher)
}

export type DocumentFolder = {
  id: string
  name: string
  parentId?: string | null
  companyId?: string | null
  updatedAt?: string
}

export type KbDocument = {
  id: string
  title: string
  slug?: string | null
  summary?: string | null
  tags?: string | null
  companyId?: string | null
  folderId?: string | null
  updatedAt?: string
}

export function useFolders(opts?: { companyId?: string; parentId?: string }) {
  const params = new URLSearchParams()
  if (opts?.companyId) params.set('companyId', opts.companyId)
  if (opts?.parentId) params.set('parentId', opts.parentId)
  const qs = params.toString()
  return useSWR<DocumentFolder[]>(`/api/folders${qs ? `?${qs}` : ''}`, fetcher)
}

export function useDocuments(opts?: { search?: string; folderId?: string }) {
  const params = new URLSearchParams()
  const term = opts?.search?.trim()
  if (term) params.set('search', term)
  if (opts?.folderId) params.set('folderId', opts.folderId)
  const qs = params.toString()
  return useSWR<KbDocument[]>(`/api/documents${qs ? `?${qs}` : ''}`, fetcher)
}

export function createFolder(input: { name: string; parentId?: string | null; companyId?: string | null }) {
  return postJson<DocumentFolder>('/api/folders', input)
}

export type Runbook = {
  id: string
  title: string
  slug?: string | null
  description?: string | null
  tags?: string | null
  companyId?: string | null
  runCount?: number
  updatedAt?: string
}

export type RunbookRun = {
  id: string
  runbookId: string
  companyId?: string | null
  status: 'Running' | 'Completed' | 'Cancelled' | string
  startedAt: string
  finishedAt?: string | null
  startedByObjectId?: string | null
}

export type RunbookRunRollup = RunbookRun & {
  runbookTitle: string
  companyName?: string | null
}

export function useRunbooks() {
  return useSWR<Runbook[]>('/api/runbooks', fetcher)
}

export function useRunbookRuns(opts?: { status?: string; companyId?: string }) {
  const params = new URLSearchParams()
  if (opts?.status) params.set('status', opts.status)
  if (opts?.companyId) params.set('companyId', opts.companyId)
  const qs = params.toString()
  return useSWR<RunbookRunRollup[]>(`/api/runbooks/runs${qs ? `?${qs}` : ''}`, fetcher)
}

export function startRunbookRun(runbookId: string, companyId?: string | null) {
  return postJson<RunbookRun>(`/api/runbooks/${runbookId}/runs`, { companyId: companyId ?? null })
}

export type PromotedDocument = {
  id: string
  title: string
  slug?: string | null
}

export function promoteRunbookRun(runbookId: string, runId: string) {
  return postJson<PromotedDocument>(`/api/runbooks/${runbookId}/runs/${runId}/promote`)
}

export function useKeeperLinks() {
  return useSWR('/api/keeper', fetcher)
}

export function useCompanies(q?: string) {
  const term = q?.trim()
  const key = term ? `/api/companies?q=${encodeURIComponent(term)}` : '/api/companies'
  return useSWR<Company[]>(key, fetcher)
}

export function useCompany(id: string | undefined) {
  return useSWR<Company>(id ? `/api/companies/${id}` : null, fetcher)
}

export type CompanyGraphNode = {
  id: string
  type: string
  name: string
}

export type CompanyGraphEdge = {
  id: string
  fromType: string
  fromId: string
  toType: string
  toId: string
  label?: string | null
}

export type CompanyGraph = {
  companyId: string
  nodes: CompanyGraphNode[]
  edges: CompanyGraphEdge[]
}

export function useCompanyGraph(id: string | undefined) {
  return useSWR<CompanyGraph>(id ? `/api/companies/${id}/graph` : null, fetcher)
}

export function useMcpServers() {
  return useSWR<McpServer[]>('/api/mcp/servers', fetcher)
}

export function useIntegrations() {
  return useSWR<IntegrationConnection[]>('/api/integrations', fetcher)
}

export type SyncRunStatus = 'Running' | 'Succeeded' | 'Failed' | 'Partial'

export type SyncRun = {
  id: string
  integrationConnectionId: string
  startedAt: string
  finishedAt?: string | null
  status: SyncRunStatus | string
  itemsCreated: number
  itemsUpdated: number
  itemsSkipped: number
  errorSummary?: string | null
}

export type IntegrationMapping = {
  id: string
  externalId: string
  externalType: string
  localEntityType: string
  localEntityId: string
  metadataJson?: string | null
}

function syncRunsKey(integrationId: string) {
  return `/api/integrations/${integrationId}/runs`
}

function integrationMappingsKey(integrationId: string) {
  return `/api/integrations/${integrationId}/mappings`
}

/** The 50 most recent sync runs for one integration. Pass undefined to skip the fetch. */
export function useSyncRuns(integrationId: string | undefined) {
  return useSWR<SyncRun[]>(integrationId ? syncRunsKey(integrationId) : null, fetcher)
}

/** External→local mappings recorded by past syncs. Pass undefined to skip the fetch. */
export function useIntegrationMappings(integrationId: string | undefined) {
  return useSWR<IntegrationMapping[]>(integrationId ? integrationMappingsKey(integrationId) : null, fetcher)
}

/** Revalidates the cached runs and mappings for one integration — call after triggering a sync. */
export function refreshIntegrationHistory(integrationId: string) {
  return Promise.all([mutate(syncRunsKey(integrationId)), mutate(integrationMappingsKey(integrationId))])
}

async function postJson<T>(url: string, body?: unknown): Promise<T> {
  const res = await apiFetch(url, {
    method: 'POST',
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  return readJson<T>(res)
}

export function createCompany(input: CreateCompanyInput) {
  return postJson<Company>('/api/companies', input)
}

export function updateCompany(id: string, input: UpdateCompanyInput) {
  return putJson(`/api/companies/${id}`, input)
}

export type CreateResourceLinkInput = {
  fromType: string
  fromId: string
  toType: string
  toId: string
  label?: string | null
}

export function createResourceLink(input: CreateResourceLinkInput) {
  return postJson<unknown>('/api/links', input)
}

export function createMcpServer(input: CreateMcpServerInput) {
  return postJson<McpServer>('/api/mcp/servers', input)
}

export function createIntegration(input: CreateIntegrationInput) {
  return postJson<IntegrationConnection>('/api/integrations', input)
}

async function putJson(url: string, body: unknown): Promise<void> {
  await apiFetch(url, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
}

export function updateIntegration(id: string, input: UpdateIntegrationInput) {
  return putJson(`/api/integrations/${id}`, input)
}

export function testIntegration(id: string) {
  return postJson<{ ok?: boolean; message?: string }>(`/api/integrations/${id}/test`)
}

export type KeeperReveal = {
  keeperRecordUrl?: string | null
}

/** Audit-logged on the server. Goes through apiFetch so the reveal carries a token like every other call. */
export function revealKeeperLink(id: string) {
  return postJson<KeeperReveal>(`/api/keeper/${id}/reveal`)
}

export function syncIntegration(id: string) {
  return postJson<{
    status?: string
    errorSummary?: string
    itemsCreated?: number
    itemsUpdated?: number
    itemsSkipped?: number
  }>(`/api/integrations/${id}/sync`, {})
}
