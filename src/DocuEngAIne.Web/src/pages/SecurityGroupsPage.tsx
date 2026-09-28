import { useState, type FormEvent } from 'react'
import {
  addSecurityGroupMember,
  canManageUsers,
  COMPANY_ACCESS_LEVELS,
  createSecurityGroup,
  deleteSecurityGroup,
  removeSecurityGroupCompany,
  removeSecurityGroupMember,
  setSecurityGroupCompany,
  updateSecurityGroup,
  useCompanies,
  useEffectiveCompanyAccess,
  useProfile,
  useSecurityGroup,
  useSecurityGroups,
  useUsers,
  type CompanyAccessLevel,
} from '../hooks/useApi'

const LEVEL_HELP: Record<CompanyAccessLevel, string> = {
  View: 'read the company and its records',
  Edit: 'also create and change its records',
  Manage: 'also archive, restore and delete them, and change the company’s status and portal',
}

function EffectiveAccess(props: { userId: string }) {
  const { data, isLoading } = useEffectiveCompanyAccess(props.userId)
  if (isLoading || !data) return <span className="muted">…</span>
  if (!data.restricted) return <span className="muted">Every company</span>
  const names = data.companies.map((c) => `${c.companyName} (${c.level})`).join(', ')
  return (
    <span className="muted">
      {names || 'No companies'}
      {data.includesTenantWide ? ' + tenant-wide (read-only)' : ''}
    </span>
  )
}

function GroupDetail(props: { id: string; onClose: () => void }) {
  const { data, error, isLoading } = useSecurityGroup(props.id)
  const { data: users } = useUsers(true)
  const { data: companies } = useCompanies()
  const [memberId, setMemberId] = useState('')
  const [companyId, setCompanyId] = useState('')
  const [level, setLevel] = useState<CompanyAccessLevel>('View')
  const [errorMessage, setErrorMessage] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function act(action: () => Promise<void>) {
    setErrorMessage(null)
    setBusy(true)
    try {
      await action()
    } catch (err) {
      setErrorMessage(err instanceof Error ? err.message : 'Action failed.')
    } finally {
      setBusy(false)
    }
  }

  if (isLoading) return <p>Loading…</p>
  if (error || !data) return <p className="error">Failed to load the group.</p>

  const { group, members, companies: grants } = data
  const memberIds = new Set(members.map((m) => m.userId))
  const grantedIds = new Set(grants.map((g) => g.companyId))
  const candidates = (users ?? []).filter((u) => !memberIds.has(u.id))
  const companyChoices = (companies ?? []).filter((c) => !grantedIds.has(c.id))

  function rename() {
    const name = window.prompt('Group name:', group.name)
    if (name === null || !name.trim()) return
    void act(() => updateSecurityGroup(group.id, { name: name.trim() }))
  }

  function editDescription() {
    const description = window.prompt('Description:', group.description ?? '')
    if (description === null) return
    void act(() => updateSecurityGroup(group.id, { description }))
  }

  function remove() {
    if (!window.confirm(`Delete "${group.name}"? Its members lose the restriction immediately.`)) return
    void act(async () => {
      await deleteSecurityGroup(group.id)
      props.onClose()
    })
  }

  function onAddMember(e: FormEvent) {
    e.preventDefault()
    if (!memberId) return
    void act(async () => {
      await addSecurityGroupMember(group.id, memberId)
      setMemberId('')
    })
  }

  function onAddCompany(e: FormEvent) {
    e.preventDefault()
    if (!companyId) return
    void act(async () => {
      await setSecurityGroupCompany(group.id, companyId, level)
      setCompanyId('')
    })
  }

  return (
    <div className="panel">
      <h2>
        {group.name} <button onClick={props.onClose}>Close</button>
      </h2>
      {errorMessage && <p className="error">{errorMessage}</p>}
      <p>{group.description || <span className="muted">No description.</span>}</p>
      <div className="toolbar">
        <button className="btn" type="button" disabled={busy} onClick={rename}>Rename</button>
        <button className="btn" type="button" disabled={busy} onClick={editDescription}>Edit description</button>
        <label>
          <input
            type="checkbox"
            checked={group.includeTenantWide}
            disabled={busy}
            onChange={(e) => void act(() => updateSecurityGroup(group.id, { includeTenantWide: e.target.checked }))}
          />{' '}
          Members also see tenant-wide content (central KB, runbook templates, tenant-wide Keeper links), read-only
        </label>
        <button className="btn" type="button" disabled={busy} onClick={remove}>Delete group</button>
      </div>

      <h3>Companies</h3>
      {grants.length === 0 ? (
        <p className="muted">
          No companies granted, so this group restricts nobody. Grant at least one company to confine its members.
        </p>
      ) : (
        <table className="data-table">
          <thead>
            <tr>
              <th>Company</th>
              <th>Level</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {grants.map((g) => (
              <tr key={g.companyId}>
                <td>{g.companyName}</td>
                <td>
                  <select
                    className="input"
                    value={g.level}
                    disabled={busy}
                    onChange={(e) => void act(() => setSecurityGroupCompany(group.id, g.companyId, e.target.value as CompanyAccessLevel))}
                  >
                    {COMPANY_ACCESS_LEVELS.map((l) => (
                      <option key={l} value={l}>{l}</option>
                    ))}
                  </select>
                </td>
                <td>
                  <button className="btn" type="button" disabled={busy} onClick={() => void act(() => removeSecurityGroupCompany(group.id, g.companyId))}>
                    Remove
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <form className="toolbar" onSubmit={onAddCompany}>
        <select className="input" value={companyId} onChange={(e) => setCompanyId(e.target.value)} aria-label="Company">
          <option value="">Add a company…</option>
          {companyChoices.map((c) => (
            <option key={c.id} value={c.id}>{c.name}</option>
          ))}
        </select>
        <select className="input" value={level} onChange={(e) => setLevel(e.target.value as CompanyAccessLevel)} aria-label="Level">
          {COMPANY_ACCESS_LEVELS.map((l) => (
            <option key={l} value={l}>{l}</option>
          ))}
        </select>
        <button className="btn" type="submit" disabled={busy || !companyId}>Grant</button>
        <span className="muted">{level}: {LEVEL_HELP[level]}</span>
      </form>

      <h3>Members</h3>
      <table className="data-table">
        <thead>
          <tr>
            <th>User</th>
            <th>Role</th>
            <th>Can reach</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {members.length === 0 && (
            <tr>
              <td colSpan={4}>No members yet.</td>
            </tr>
          )}
          {members.map((m) => (
            <tr key={m.userId}>
              <td>
                {m.displayName || m.email}
                {m.isActive ? '' : ' (suspended)'}
              </td>
              <td>{m.role}</td>
              <td>
                {m.bypasses ? <span className="muted">Every company ({m.role}s are never restricted)</span> : <EffectiveAccess userId={m.userId} />}
              </td>
              <td>
                <button className="btn" type="button" disabled={busy} onClick={() => void act(() => removeSecurityGroupMember(group.id, m.userId))}>
                  Remove
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <form className="toolbar" onSubmit={onAddMember}>
        <select className="input" value={memberId} onChange={(e) => setMemberId(e.target.value)} aria-label="User">
          <option value="">Add a member…</option>
          {candidates.map((u) => (
            <option key={u.id} value={u.id}>{u.displayName ? `${u.displayName} (${u.email})` : u.email}</option>
          ))}
        </select>
        <button className="btn" type="submit" disabled={busy || !memberId}>Add</button>
      </form>
    </div>
  )
}

export function SecurityGroupsPage() {
  const { data: profile, isLoading: profileLoading } = useProfile()
  const allowed = canManageUsers(profile?.role)
  const { data, error, isLoading } = useSecurityGroups(allowed)
  const [selected, setSelected] = useState<string | null>(null)
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [includeTenantWide, setIncludeTenantWide] = useState(false)
  const [errorMessage, setErrorMessage] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function onCreate(e: FormEvent) {
    e.preventDefault()
    if (!name.trim()) return
    setErrorMessage(null)
    setBusy(true)
    try {
      const created = await createSecurityGroup({ name: name.trim(), description: description.trim() || undefined, includeTenantWide })
      setName('')
      setDescription('')
      setIncludeTenantWide(false)
      setSelected(created.group.id)
    } catch (err) {
      setErrorMessage(err instanceof Error ? err.message : 'Failed to create the group.')
    } finally {
      setBusy(false)
    }
  }

  if (profileLoading) {
    return (
      <div className="page">
        <h1>Security groups</h1>
        <p>Loading…</p>
      </div>
    )
  }

  if (!allowed) {
    return (
      <div className="page">
        <h1>Security groups</h1>
        <p>Security groups are available to Admin and Owner.</p>
      </div>
    )
  }

  const groups = data ?? []

  return (
    <div className="page">
      <h1>Security groups</h1>
      <p>
        Confine users to specific companies. A member of any group that grants companies sees only those companies,
        at the highest level any of their groups grants — everywhere: lists, search, the portal and the dashboard. A
        group with no companies restricts nobody. Admins and Owners are never restricted. Changes apply on the
        member’s next request.
      </p>
      {errorMessage && <p className="error">{errorMessage}</p>}

      <form className="toolbar" onSubmit={onCreate}>
        <input className="input" placeholder="Group name" value={name} maxLength={100} onChange={(e) => setName(e.target.value)} aria-label="Group name" />
        <input
          className="input"
          placeholder="Description (optional)"
          value={description}
          maxLength={500}
          onChange={(e) => setDescription(e.target.value)}
          aria-label="Description"
        />
        <label>
          <input type="checkbox" checked={includeTenantWide} onChange={(e) => setIncludeTenantWide(e.target.checked)} /> Include tenant-wide content
        </label>
        <button className="btn" type="submit" disabled={busy || !name.trim()}>Create</button>
      </form>

      {isLoading && <p>Loading…</p>}
      {error && <p className="error">Failed to load security groups.</p>}

      {!isLoading && !error && (
        <table className="data-table">
          <thead>
            <tr>
              <th>Name</th>
              <th>Companies</th>
              <th>Members</th>
              <th>Tenant-wide</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {groups.length === 0 && (
              <tr>
                <td colSpan={5}>No security groups. Every user can reach every company their role allows.</td>
              </tr>
            )}
            {groups.map((g) => (
              <tr key={g.id}>
                <td>
                  {g.name}
                  {g.description ? <div className="muted">{g.description}</div> : null}
                </td>
                <td>{g.companyCount === 0 ? <span className="muted">none (restricts nobody)</span> : g.companyCount}</td>
                <td>{g.memberCount}</td>
                <td>{g.includeTenantWide ? 'Read-only' : 'Hidden'}</td>
                <td>
                  <button className="btn" type="button" onClick={() => setSelected(g.id)}>Open</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {selected && <GroupDetail key={selected} id={selected} onClose={() => setSelected(null)} />}
    </div>
  )
}
