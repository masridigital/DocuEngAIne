import useSWR, { mutate } from 'swr'
import { acquireApiToken } from '../auth/msalConfig'

/** Thrown for any non-2xx API response. Carries the HTTP status and the response body. */
export class ApiError extends Error {
  readonly status: number
  readonly body: string
  /** The parsed JSON body, when there was one, for callers that branch on a structured error. */
  readonly data: unknown

  constructor(status: number, statusText: string, body: string, data?: unknown) {
    super(body ? `Request failed (${status}): ${body}` : `Request failed (${status}${statusText ? ` ${statusText}` : ''})`)
    this.name = 'ApiError'
    this.status = status
    this.body = body
    this.data = data
  }
}

function parseJsonBody(contentType: string | null, text: string): unknown {
  if (!text || !contentType?.includes('json')) return undefined
  try {
    return JSON.parse(text) as unknown
  } catch {
    return undefined
  }
}

function describeBody(parsed: unknown, text: string) {
  if (!text) return ''
  if (typeof parsed === 'string') return parsed
  if (parsed && typeof parsed === 'object') {
    const { detail, title, message, error } = parsed as Record<string, unknown>
    for (const candidate of [detail, title, message, error]) {
      if (typeof candidate === 'string' && candidate) return candidate
    }
  }
  return text
}

export const IP_NOT_ALLOWED = 'ip_not_allowed'

/** The address the API saw when the tenant IP allowlist refused a request; null for any other error. */
export function ipBlockedAddress(error: unknown): string | null {
  if (!(error instanceof ApiError) || error.status !== 403) return null
  const data = error.data as { error?: unknown; ip?: unknown } | undefined
  if (!data || typeof data !== 'object' || data.error !== IP_NOT_ALLOWED) return null
  return typeof data.ip === 'string' && data.ip ? data.ip : 'unknown'
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
    const data = parseJsonBody(res.headers.get('content-type'), text)
    throw new ApiError(res.status, res.statusText, describeBody(data, text).trim(), data)
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
  portalEnabled?: boolean
}

export type UpdateCompanyInput = {
  name?: string | null
  slug?: string | null
  haloClientId?: string | null
  ninjaOrganizationId?: string | null
  haloPortalUrl?: string | null
  ninjaPortalUrl?: string | null
  portalEnabled?: boolean
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

export type IntegrationProvider = 'Halo' | 'NinjaOne' | 'UniFi' | 'Blackpoint' | 'CustomMcp' | 'Cipp' | 'Meraki' | 'Composio' | 'Action1' | 'Autotask' | 'DefensX' | 'Pax8' | 'Slide'

const compactProviders: IntegrationProvider[] = ['Halo', 'NinjaOne', 'Cipp', 'Meraki', 'UniFi', 'Blackpoint', 'Action1', 'Autotask', 'DefensX', 'Pax8', 'Slide']

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

/**
 * Suspends or reactivates a user. A suspended user is refused on every route; the server refuses
 * suspending yourself, the last active Owner, or (for non-Owners) any Owner.
 */
export function setUserActive(id: string, active: boolean) {
  return postJson<void>(`/api/users/${id}/${active ? 'activate' : 'deactivate'}`)
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

export type Asset = {
  id: string
  name: string
  location?: string | null
  status?: string | null
  companyId?: string | null
  expiresAt?: string | null
  haloAssetUrl?: string | null
  ninjaDeviceUrl?: string | null
  externalIdsJson?: string | null
  assetType?: string | null
}

export function useAssets() {
  return useSWR<Asset[]>('/api/assets', fetcher)
}

/** Contributor and above may create assets and change asset layouts and option lists. */
export function canEditContent(role?: string | null): boolean {
  return role === 'Contributor' || role === 'Admin' || role === 'Owner'
}

export type AssetFieldType =
  | 'Text'
  | 'Markdown'
  | 'Number'
  | 'Date'
  | 'DateTime'
  | 'Url'
  | 'Email'
  | 'Phone'
  | 'Checkbox'
  | 'Select'
  | 'MultiSelect'

export const ASSET_FIELD_TYPES: AssetFieldType[] = [
  'Text',
  'Markdown',
  'Number',
  'Date',
  'DateTime',
  'Url',
  'Email',
  'Phone',
  'Checkbox',
  'Select',
  'MultiSelect',
]

export function usesOptions(type?: string | null): boolean {
  return type === 'Select' || type === 'MultiSelect'
}

/** One field value on an asset, with the layout field it belongs to. `value` is the stored form. */
export type AssetFieldValue = {
  fieldId: string
  name: string
  fieldType: string
  section?: string | null
  helpText?: string | null
  isRequired: boolean
  optionListId?: string | null
  value?: string | null
  /** False for a value whose field is no longer on the layout (older data): shown, not editable. */
  onLayout: boolean
}

export type AssetDetail = {
  id: string
  name: string
  location?: string | null
  status?: string | null
  notes?: string | null
  companyId?: string | null
  expiresAt?: string | null
  haloAssetUrl?: string | null
  ninjaDeviceUrl?: string | null
  assetType?: { id?: string | null; name?: string | null } | null
  fields: AssetFieldValue[]
}

/** A value to submit for a field: text, a list of option values, a checkbox, or null to clear. */
export type AssetFieldInput = string | string[] | boolean | null

export function useAsset(id: string | undefined) {
  return useSWR<AssetDetail>(id ? `/api/assets/${id}` : null, fetcher)
}

export type CreateAssetInput = {
  name: string
  assetTypeId: string
  companyId?: string | null
  location?: string | null
  status?: string | null
  fields?: Record<string, AssetFieldInput>
}

export async function createAsset(input: CreateAssetInput) {
  const created = await postJson<{ id: string; name: string }>('/api/assets', input)
  await mutate('/api/assets')
  return created
}

/** Sets or clears the submitted fields only; returns the asset as stored. */
export async function updateAssetFields(id: string, values: Record<string, AssetFieldInput>) {
  const res = await apiFetch(`/api/assets/${id}/fields`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ values }),
  })
  const updated = await readJson<AssetDetail>(res)
  await mutate(`/api/assets/${id}`, updated, { revalidate: false })
  return updated
}

/** Per-field messages from a refused field write (`{ error, fields: { [fieldId]: message } }`). */
export function fieldErrors(error: unknown): Record<string, string> {
  if (!(error instanceof ApiError)) return {}
  const data = error.data as { fields?: unknown } | undefined
  if (!data || typeof data !== 'object' || !data.fields || typeof data.fields !== 'object') return {}
  const result: Record<string, string> = {}
  for (const [key, message] of Object.entries(data.fields as Record<string, unknown>)) {
    if (typeof message === 'string') result[key] = message
  }
  return result
}

export type AssetLayoutField = {
  id: string
  name: string
  fieldType: AssetFieldType
  isRequired: boolean
  isExpiration: boolean
  sortOrder: number
  section?: string | null
  helpText?: string | null
  optionListId?: string | null
  optionListName?: string | null
}

export type AssetLayoutSummary = {
  id: string
  name: string
  description?: string | null
  icon?: string | null
  isPublished: boolean
  publishedAt?: string | null
  availableToAllCompanies: boolean
  currentVersion: number
  fields: AssetLayoutField[]
  /** For a layout limited to chosen companies: the ones it is enabled for (that you can see). */
  enabledCompanyIds: string[]
}

export type AssetLayoutProblem = { code: string; message: string; fieldId?: string | null }

export type AssetLayoutDetail = {
  layout: AssetLayoutSummary
  companies: { companyId: string; companyName: string; activatedAt: string }[]
  assetCount: number
  /** What would stop the layout from being published as it stands. */
  problems: AssetLayoutProblem[]
}

export type AssetLayoutVersion = {
  versionNumber: number
  summary?: string | null
  createdByName?: string | null
  createdAt: string
}

export type AssetLayoutVersionDetail = AssetLayoutVersion & { schema: unknown }

export type AssetLayoutFieldInput = {
  name: string
  type: AssetFieldType
  isRequired?: boolean
  isExpiration?: boolean
  section?: string | null
  helpText?: string | null
  optionListId?: string | null
}

export type UpdateAssetLayoutFieldInput = {
  name?: string
  fieldType?: AssetFieldType
  isRequired?: boolean
  isExpiration?: boolean
  sortOrder?: number
  section?: string
  helpText?: string
  optionListId?: string
  optionListClear?: boolean
}

/** Whether new assets of `layout` can be created in `companyId` (null = tenant-wide). */
export function layoutUsableFor(layout: AssetLayoutSummary, companyId: string | null): boolean {
  if (!layout.isPublished) return false
  if (layout.availableToAllCompanies) return true
  return companyId !== null && layout.enabledCompanyIds.includes(companyId)
}

/** The problems list a refused publish or schema change carries (409 `{ error, problems }`). */
export function layoutProblems(error: unknown): AssetLayoutProblem[] {
  if (!(error instanceof ApiError)) return []
  const data = error.data as { problems?: unknown } | undefined
  return data && typeof data === 'object' && Array.isArray(data.problems) ? (data.problems as AssetLayoutProblem[]) : []
}

const ASSET_LAYOUTS_KEY = '/api/assets/types'

export function useAssetLayouts() {
  return useSWR<AssetLayoutSummary[]>(ASSET_LAYOUTS_KEY, fetcher)
}

export function useAssetLayout(id: string | undefined) {
  return useSWR<AssetLayoutDetail>(id ? `${ASSET_LAYOUTS_KEY}/${id}` : null, fetcher)
}

export function useAssetLayoutVersions(id: string | undefined) {
  return useSWR<AssetLayoutVersion[]>(id ? `${ASSET_LAYOUTS_KEY}/${id}/versions` : null, fetcher)
}

export function useAssetLayoutVersion(id: string | undefined, versionNumber: number | null) {
  return useSWR<AssetLayoutVersionDetail>(
    id && versionNumber !== null ? `${ASSET_LAYOUTS_KEY}/${id}/versions/${versionNumber}` : null,
    fetcher,
  )
}

function refreshAssetLayout(id: string) {
  return Promise.all([
    mutate(ASSET_LAYOUTS_KEY),
    mutate(`${ASSET_LAYOUTS_KEY}/${id}`),
    mutate(`${ASSET_LAYOUTS_KEY}/${id}/versions`),
    // Option lists show which layouts use them.
    mutate(OPTION_LISTS_KEY),
  ])
}

export async function createAssetLayout(input: {
  name: string
  description?: string
  availableToAllCompanies?: boolean
  fields?: AssetLayoutFieldInput[]
}) {
  const created = await postJson<AssetLayoutDetail>(ASSET_LAYOUTS_KEY, input)
  await mutate(ASSET_LAYOUTS_KEY)
  return created
}

export async function updateAssetLayout(
  id: string,
  input: { name?: string; description?: string; icon?: string; availableToAllCompanies?: boolean },
) {
  await putJson(`${ASSET_LAYOUTS_KEY}/${id}`, input)
  await refreshAssetLayout(id)
}

export async function deleteAssetLayout(id: string) {
  await apiFetch(`${ASSET_LAYOUTS_KEY}/${id}`, { method: 'DELETE' })
  await mutate(ASSET_LAYOUTS_KEY)
  await mutate(OPTION_LISTS_KEY)
}

export async function publishAssetLayout(id: string) {
  await postJson<AssetLayoutDetail>(`${ASSET_LAYOUTS_KEY}/${id}/publish`)
  await refreshAssetLayout(id)
}

export async function unpublishAssetLayout(id: string) {
  await postJson<AssetLayoutDetail>(`${ASSET_LAYOUTS_KEY}/${id}/unpublish`)
  await refreshAssetLayout(id)
}

export async function addAssetLayoutField(id: string, input: AssetLayoutFieldInput) {
  await postJson<AssetLayoutField>(`${ASSET_LAYOUTS_KEY}/${id}/fields`, input)
  await refreshAssetLayout(id)
}

export async function updateAssetLayoutField(layoutId: string, fieldId: string, input: UpdateAssetLayoutFieldInput) {
  await putJson(`/api/assets/fields/${fieldId}`, input)
  await refreshAssetLayout(layoutId)
}

export async function deleteAssetLayoutField(layoutId: string, fieldId: string) {
  await apiFetch(`/api/assets/fields/${fieldId}`, { method: 'DELETE' })
  await refreshAssetLayout(layoutId)
}

export async function enableAssetLayoutForCompany(id: string, companyId: string) {
  await putJson(`${ASSET_LAYOUTS_KEY}/${id}/companies/${companyId}`, {})
  await refreshAssetLayout(id)
}

export async function disableAssetLayoutForCompany(id: string, companyId: string) {
  await apiFetch(`${ASSET_LAYOUTS_KEY}/${id}/companies/${companyId}`, { method: 'DELETE' })
  await refreshAssetLayout(id)
}

export type OptionItem = {
  id: string
  label: string
  /** What asset values store; fixed when the option is created, so labels can be renamed. */
  value: string
  sortOrder: number
  isActive: boolean
}

export type OptionList = {
  id: string
  name: string
  description?: string | null
  isActive: boolean
  items: OptionItem[]
  /** "Layout › Field" for every field that draws on the list. */
  usedBy: string[]
}

const OPTION_LISTS_KEY = '/api/option-lists'

export function useOptionLists() {
  return useSWR<OptionList[]>(OPTION_LISTS_KEY, fetcher)
}

function refreshOptionLists() {
  // Layout problems (e.g. a choice field with nothing left to pick) depend on the lists.
  return Promise.all([
    mutate(OPTION_LISTS_KEY),
    mutate((key) => typeof key === 'string' && key.startsWith(ASSET_LAYOUTS_KEY)),
  ])
}

export async function createOptionList(input: { name: string; description?: string; items?: { label: string }[] }) {
  const created = await postJson<OptionList>(OPTION_LISTS_KEY, input)
  await refreshOptionLists()
  return created
}

export async function updateOptionList(id: string, input: { name?: string; description?: string; isActive?: boolean }) {
  await putJson(`${OPTION_LISTS_KEY}/${id}`, input)
  await refreshOptionLists()
}

export async function deleteOptionList(id: string) {
  await apiFetch(`${OPTION_LISTS_KEY}/${id}`, { method: 'DELETE' })
  await refreshOptionLists()
}

export async function addOptionItem(id: string, label: string) {
  await postJson<OptionItem>(`${OPTION_LISTS_KEY}/${id}/items`, { label })
  await refreshOptionLists()
}

export async function updateOptionItem(id: string, itemId: string, input: { label?: string; sortOrder?: number; isActive?: boolean }) {
  await putJson(`${OPTION_LISTS_KEY}/${id}/items/${itemId}`, input)
  await refreshOptionLists()
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

export type DocumentAssistAction = 'summarize' | 'rewrite'

export type DocumentAssistRequest = {
  action: DocumentAssistAction
  instruction?: string
  apply?: boolean
}

export type DocumentAssistResponse = {
  content: string
  model: string
  provider: string
}

/** Preview (default) or apply an LLM summarize/rewrite for one document. */
export function assistDocument(id: string, input: DocumentAssistRequest) {
  return postJson<DocumentAssistResponse>(`/api/documents/${id}/assist`, input)
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

export type LlmConfig = {
  provider: string
  model: string
}

/** Current LLM provider and model from appsettings / Key Vault. Never includes API keys. */
export function useLlmConfig() {
  return useSWR<LlmConfig>('/api/llm/config', fetcher)
}

export type SyncRunStatus = 'Running' | 'Succeeded' | 'Failed' | 'Partial'

export type SyncRun = {
  id: string
  integrationConnectionId: string
  provider?: string | null
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

export type PortalCompanyListItem = {
  id: string
  name: string
  slug: string
  website?: string | null
}

export type PortalCounts = {
  documents: number
  expirations: number
  keeperLinks: number
}

export type PortalCompanyDetail = {
  id: string
  name: string
  slug: string
  website?: string | null
  phone?: string | null
  hoursOfOperation?: string | null
  counts: PortalCounts
}

export type PortalDocument = {
  id: string
  title: string
  slug?: string | null
  summary?: string | null
  content?: string | null
  tags?: string | null
  updatedAt: string
}

export type PortalKeeperLink = {
  id: string
  title: string
  companyId?: string | null
  updatedAt: string
  hasRecordUrl: boolean
}

export function usePortalCompanies() {
  return useSWR<PortalCompanyListItem[]>('/api/portal/companies', fetcher)
}

export function usePortalCompany(id: string | undefined) {
  return useSWR<PortalCompanyDetail>(id ? `/api/portal/companies/${id}` : null, fetcher)
}

export function usePortalDocuments(companyId: string | undefined) {
  return useSWR<PortalDocument[]>(companyId ? `/api/portal/companies/${companyId}/documents` : null, fetcher)
}

export function usePortalExpirations(companyId: string | undefined) {
  return useSWR<ExpirationItem[]>(companyId ? `/api/portal/companies/${companyId}/expirations` : null, fetcher)
}

export function usePortalKeeperLinks(companyId: string | undefined) {
  return useSWR<PortalKeeperLink[]>(companyId ? `/api/portal/companies/${companyId}/keeper-links` : null, fetcher)
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

export type AuditEvent = {
  id: string
  action: string
  category?: string | null
  entityType: string
  entityId?: string | null
  targetLabel?: string | null
  details?: string | null
  changesJson?: string | null
  actorObjectId?: string | null
  actorName?: string | null
  userId?: string | null
  ipAddress?: string | null
  requestMethod?: string | null
  requestPath?: string | null
  occurredAt: string
}

export type AuditEventPage = {
  total: number
  page: number
  pageSize: number
  items: AuditEvent[]
}

export type AuditFilters = {
  action?: string
  category?: string
  entityType?: string
  from?: string
  to?: string
  page?: number
}

function auditQuery(filters: AuditFilters) {
  const params = new URLSearchParams()
  if (filters.action) params.set('action', filters.action)
  if (filters.category) params.set('category', filters.category)
  if (filters.entityType) params.set('entityType', filters.entityType)
  if (filters.from) params.set('from', new Date(filters.from).toISOString())
  if (filters.to) params.set('to', new Date(filters.to).toISOString())
  return params
}

/** Admin-gated audit trail. Pass enabled=false to skip the request entirely. */
export function useAuditEvents(filters: AuditFilters, enabled = true) {
  const params = auditQuery(filters)
  if (filters.page && filters.page > 1) params.set('page', String(filters.page))
  const qs = params.toString()
  return useSWR<AuditEventPage>(enabled ? `/api/audit-events${qs ? `?${qs}` : ''}` : null, fetcher)
}

/** Full activity feed for one record — newest first, capped server-side. */
export function useAuditActivity(entityType?: string, entityId?: string) {
  return useSWR<AuditEvent[]>(
    entityType && entityId ? `/api/audit-events/activity/${entityType}/${entityId}` : null,
    fetcher,
  )
}

/** Downloads the filtered trail as CSV. The export itself is audit-logged server-side. */
export async function exportAuditCsv(filters: AuditFilters) {
  const qs = auditQuery(filters).toString()
  const res = await apiFetch(`/api/audit-events/export${qs ? `?${qs}` : ''}`)
  const blob = await res.blob()
  const url = URL.createObjectURL(blob)
  try {
    const link = document.createElement('a')
    link.href = url
    link.download = `audit-events-${new Date().toISOString().slice(0, 10)}.csv`
    document.body.appendChild(link)
    link.click()
    link.remove()
  } finally {
    URL.revokeObjectURL(url)
  }
}

export type ArchiveResourceType = 'Asset' | 'Document' | 'Runbook' | 'KeeperLink'

const ARCHIVE_ROUTES: Record<ArchiveResourceType, string> = {
  Asset: '/api/assets',
  Document: '/api/documents',
  Runbook: '/api/runbooks',
  KeeperLink: '/api/keeper',
}

/** Revalidates every cached list for the archivable resource types (all query-string variants). */
function revalidateResourceLists() {
  const prefixes = Object.values(ARCHIVE_ROUTES)
  return mutate((key) => typeof key === 'string' && prefixes.some((p) => key.startsWith(p)))
}

/** DELETE archives to the Museum (restorable). The reason is optional and lands on the entry + audit row. */
export async function archiveResource(type: ArchiveResourceType, id: string, reason?: string) {
  const trimmed = reason?.trim()
  const qs = trimmed ? `?reason=${encodeURIComponent(trimmed)}` : ''
  await apiFetch(`${ARCHIVE_ROUTES[type]}/${id}${qs}`, { method: 'DELETE' })
  await Promise.all([revalidateResourceLists(), mutate((key) => typeof key === 'string' && key.startsWith('/api/archive'))])
}

export type ArchiveState = 'archived' | 'restored' | 'deleted' | 'all'

export type ArchiveEntry = {
  id: string
  resourceType: ArchiveResourceType
  resourceId: string
  resourceLabel: string
  reason?: string | null
  state: 'archived' | 'restored' | 'deleted'
  archivedAt: string
  archivedByName?: string | null
  archivedByObjectId?: string | null
  restoredAt?: string | null
  permanentlyDeletedAt?: string | null
}

export type ArchivePage = {
  total: number
  page: number
  pageSize: number
  items: ArchiveEntry[]
}

export function useArchive(opts: { state: ArchiveState; resourceType?: ArchiveResourceType; page?: number }) {
  const params = new URLSearchParams()
  params.set('state', opts.state)
  if (opts.resourceType) params.set('resourceType', opts.resourceType)
  if (opts.page && opts.page > 1) params.set('page', String(opts.page))
  return useSWR<ArchivePage>(`/api/archive?${params.toString()}`, fetcher)
}

export async function restoreArchiveEntry(id: string) {
  const entry = await postJson<ArchiveEntry>(`/api/archive/${id}/restore`)
  await revalidateResourceLists()
  return entry
}

/** Admin only. Irreversible: the row is destroyed; the Museum entry stays as a tombstone. */
export async function permanentlyDeleteArchiveEntry(id: string) {
  await apiFetch(`/api/archive/${id}`, { method: 'DELETE' })
}

export type AccessReviewStatus = 'Draft' | 'InProgress' | 'Completed' | 'Cancelled'
export type AccessReviewDecision = 'Pending' | 'Retain' | 'Revoke' | 'ChangeRole'

export type AccessReviewSummary = {
  id: string
  name: string
  status: AccessReviewStatus
  reviewerUserId?: string | null
  dueAt?: string | null
  startedAt?: string | null
  completedAt?: string | null
  cancelledAt?: string | null
  itemCount: number
  pendingCount: number
  createdAt: string
}

export type AccessReviewItem = {
  id: string
  subjectUserId: string
  subjectEmail: string
  subjectName?: string | null
  roleAtSnapshot: UserRole
  grantCount: number
  decision: AccessReviewDecision
  requestedRole?: UserRole | null
  decidedByName?: string | null
  decidedAt?: string | null
  decisionNotes?: string | null
}

export type AccessReviewDetail = {
  review: AccessReviewSummary
  notes?: string | null
  items: AccessReviewItem[]
}

const ACCESS_REVIEWS_KEY = '/api/access-reviews'

export function useAccessReviews(enabled = true) {
  return useSWR<AccessReviewSummary[]>(enabled ? ACCESS_REVIEWS_KEY : null, fetcher)
}

export function useAccessReview(id: string | undefined) {
  return useSWR<AccessReviewDetail>(id ? `${ACCESS_REVIEWS_KEY}/${id}` : null, fetcher)
}

function refreshAccessReview(id: string) {
  return Promise.all([mutate(ACCESS_REVIEWS_KEY), mutate(`${ACCESS_REVIEWS_KEY}/${id}`)])
}

export async function createAccessReview(input: { name: string; dueAt?: string; notes?: string }) {
  const created = await postJson<AccessReviewSummary>(ACCESS_REVIEWS_KEY, {
    name: input.name,
    dueAt: input.dueAt ? new Date(input.dueAt).toISOString() : undefined,
    notes: input.notes || undefined,
  })
  await mutate(ACCESS_REVIEWS_KEY)
  return created
}

export async function startAccessReview(id: string) {
  await postJson(`${ACCESS_REVIEWS_KEY}/${id}/start`)
  await refreshAccessReview(id)
}

export async function completeAccessReview(id: string) {
  await postJson(`${ACCESS_REVIEWS_KEY}/${id}/complete`)
  await refreshAccessReview(id)
}

export async function cancelAccessReview(id: string) {
  await postJson(`${ACCESS_REVIEWS_KEY}/${id}/cancel`)
  await refreshAccessReview(id)
}

/** Applies immediately: Revoke suspends the user, ChangeRole changes their tenant role. */
export async function decideAccessReviewItem(
  id: string,
  itemId: string,
  input: { decision: Exclude<AccessReviewDecision, 'Pending'>; requestedRole?: UserRole; notes?: string },
) {
  await apiFetch(`${ACCESS_REVIEWS_KEY}/${id}/items/${itemId}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  })
  await Promise.all([refreshAccessReview(id), mutate('/api/users')])
}

export async function exportAccessReviewCsv(id: string) {
  const res = await apiFetch(`${ACCESS_REVIEWS_KEY}/${id}/export`)
  const blob = await res.blob()
  const url = URL.createObjectURL(blob)
  try {
    const link = document.createElement('a')
    link.href = url
    link.download = `access-review-${id}.csv`
    document.body.appendChild(link)
    link.click()
    link.remove()
  } finally {
    URL.revokeObjectURL(url)
  }
}

export type IpAllowlistEntry = {
  id: string
  cidr: string
  label?: string | null
  isActive: boolean
  createdAt: string
}

export type IpAccessState = {
  enabled: boolean
  /** Enforcement is switched off at the host (Security:DisableIpAllowlist) regardless of the toggle. */
  breakGlass: boolean
  currentIp?: string | null
  currentIpCovered: boolean
  entries: IpAllowlistEntry[]
}

const IP_ACCESS_KEY = '/api/tenant/ip-access'

export function useIpAccess(enabled = true) {
  return useSWR<IpAccessState>(enabled ? IP_ACCESS_KEY : null, fetcher)
}

export async function setIpAllowlistEnabled(enabled: boolean) {
  await putJson(`${IP_ACCESS_KEY}/policy`, { enabled })
  await mutate(IP_ACCESS_KEY)
}

export async function addIpAllowlistEntry(input: { cidr: string; label?: string; isActive?: boolean }) {
  await postJson<IpAllowlistEntry>(`${IP_ACCESS_KEY}/entries`, input)
  await mutate(IP_ACCESS_KEY)
}

export async function updateIpAllowlistEntry(id: string, input: { cidr?: string; label?: string; isActive?: boolean }) {
  await putJson(`${IP_ACCESS_KEY}/entries/${id}`, input)
  await mutate(IP_ACCESS_KEY)
}

export async function deleteIpAllowlistEntry(id: string) {
  await apiFetch(`${IP_ACCESS_KEY}/entries/${id}`, { method: 'DELETE' })
  await mutate(IP_ACCESS_KEY)
}

export type CompanyAccessLevel = 'View' | 'Edit' | 'Manage'

export const COMPANY_ACCESS_LEVELS: CompanyAccessLevel[] = ['View', 'Edit', 'Manage']

export type SecurityGroupSummary = {
  id: string
  name: string
  description?: string | null
  includeTenantWide: boolean
  memberCount: number
  companyCount: number
  createdAt: string
}

export type SecurityGroupMember = {
  userId: string
  email: string
  displayName?: string | null
  role: UserRole
  isActive: boolean
  /** Admin or Owner: membership never restricts them. */
  bypasses: boolean
}

export type CompanyGrant = {
  companyId: string
  companyName: string
  level: CompanyAccessLevel
}

export type SecurityGroupDetail = {
  group: SecurityGroupSummary
  members: SecurityGroupMember[]
  companies: CompanyGrant[]
}

export type CompanyAccess = {
  restricted: boolean
  includesTenantWide: boolean
  companies: CompanyGrant[]
}

const SECURITY_GROUPS_KEY = '/api/security-groups'

export function useSecurityGroups(enabled = true) {
  return useSWR<SecurityGroupSummary[]>(enabled ? SECURITY_GROUPS_KEY : null, fetcher)
}

export function useSecurityGroup(id: string | undefined) {
  return useSWR<SecurityGroupDetail>(id ? `${SECURITY_GROUPS_KEY}/${id}` : null, fetcher)
}

/** What the signed-in user can reach; `restricted: false` means every company. */
export function useMyCompanyAccess() {
  return useSWR<CompanyAccess>('/api/me/company-access', fetcher)
}

export function useEffectiveCompanyAccess(userId: string | undefined) {
  return useSWR<CompanyAccess>(userId ? `${SECURITY_GROUPS_KEY}/effective/${userId}` : null, fetcher)
}

function refreshSecurityGroup(id: string) {
  return Promise.all([
    mutate(SECURITY_GROUPS_KEY),
    mutate(`${SECURITY_GROUPS_KEY}/${id}`),
    mutate((key) => typeof key === 'string' && key.startsWith(`${SECURITY_GROUPS_KEY}/effective/`)),
  ])
}

export async function createSecurityGroup(input: { name: string; description?: string; includeTenantWide?: boolean }) {
  const created = await postJson<SecurityGroupDetail>(SECURITY_GROUPS_KEY, input)
  await mutate(SECURITY_GROUPS_KEY)
  return created
}

export async function updateSecurityGroup(id: string, input: { name?: string; description?: string; includeTenantWide?: boolean }) {
  await putJson(`${SECURITY_GROUPS_KEY}/${id}`, input)
  await refreshSecurityGroup(id)
}

export async function deleteSecurityGroup(id: string) {
  await apiFetch(`${SECURITY_GROUPS_KEY}/${id}`, { method: 'DELETE' })
  await mutate(SECURITY_GROUPS_KEY)
}

export async function addSecurityGroupMember(id: string, userId: string) {
  await putJson(`${SECURITY_GROUPS_KEY}/${id}/members/${userId}`, {})
  await refreshSecurityGroup(id)
}

export async function removeSecurityGroupMember(id: string, userId: string) {
  await apiFetch(`${SECURITY_GROUPS_KEY}/${id}/members/${userId}`, { method: 'DELETE' })
  await refreshSecurityGroup(id)
}

export async function setSecurityGroupCompany(id: string, companyId: string, level: CompanyAccessLevel) {
  await putJson(`${SECURITY_GROUPS_KEY}/${id}/companies/${companyId}`, { level })
  await refreshSecurityGroup(id)
}

export async function removeSecurityGroupCompany(id: string, companyId: string) {
  await apiFetch(`${SECURITY_GROUPS_KEY}/${id}/companies/${companyId}`, { method: 'DELETE' })
  await refreshSecurityGroup(id)
}

export type TenantFeatureKey = 'client_portal' | 'ai_assistant' | 'mcp_server' | 'access_reviews'

export type TenantFeature = {
  key: string
  name: string
  description: string
  enabled: boolean
  enabledByDefault: boolean
}

export type TenantTermKey = 'company' | 'asset' | 'document' | 'runbook'

export type TenantTerm = {
  key: string
  /** What the term names, for the settings page. */
  describes: string
  singular: string
  plural: string
  defaultSingular: string
  defaultPlural: string
}

export type TenantBranding = { displayName?: string | null; accentColor?: string | null }

export type TenantConfiguration = {
  features: TenantFeature[]
  terminology: TenantTerm[]
  branding: TenantBranding
}

export const TENANT_CONFIGURATION_KEY = '/api/tenant/configuration'

export const DEFAULT_TERMS: Record<TenantTermKey, { singular: string; plural: string }> = {
  company: { singular: 'Company', plural: 'Companies' },
  asset: { singular: 'Asset', plural: 'Assets' },
  document: { singular: 'Document', plural: 'Documents' },
  runbook: { singular: 'Runbook', plural: 'Runbooks' },
}

export const DEFAULT_PRODUCT_NAME = 'DocuEngAIne'

export function useTenantConfiguration() {
  return useSWR<TenantConfiguration>(TENANT_CONFIGURATION_KEY, fetcher)
}

/** Every feature defaults on, so an unknown state (still loading, or a failed read) counts as on. */
export function featureEnabled(config: TenantConfiguration | undefined, key: TenantFeatureKey): boolean {
  const feature = config?.features.find((f) => f.key === key)
  return feature ? feature.enabled : true
}

/** What this tenant calls a thing: `term('company')` → "Companies", `term('company', 'singular')` → "Company". */
export function useTerms() {
  const { data } = useTenantConfiguration()
  return (key: TenantTermKey, form: 'singular' | 'plural' = 'plural') => {
    const term = data?.terminology.find((t) => t.key === key)
    return term ? term[form] : DEFAULT_TERMS[key][form]
  }
}

export async function setTenantFeature(key: string, enabled: boolean) {
  await putJson(`/api/tenant/features/${key}`, { enabled })
  await mutate(TENANT_CONFIGURATION_KEY)
}

/** A null entry restores that term's default. */
export async function setTenantTerminology(terms: Record<string, { singular: string; plural: string } | null>) {
  await putJson('/api/tenant/terminology', { terms })
  await mutate(TENANT_CONFIGURATION_KEY)
}

/** Replaces both values; null clears one back to the default. */
export async function setTenantBranding(input: { displayName: string | null; accentColor: string | null }) {
  await putJson('/api/tenant/branding', input)
  await mutate(TENANT_CONFIGURATION_KEY)
}
