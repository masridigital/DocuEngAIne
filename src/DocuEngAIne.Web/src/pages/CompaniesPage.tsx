import { useState, type FormEvent, type ReactNode } from 'react'
import { Link, useParams } from 'react-router-dom'
import { createCompany, createResourceLink, updateCompany, useCompanies, useCompany, useCompanyGraph, type Company, type CompanyGraph, type RelatedLinkItem, type RelatedListItem } from '../hooks/useApi'

function slugify(value: string) {
  return value
    .toLowerCase()
    .trim()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
}

function isHttpUrl(value?: string | null): value is string {
  if (!value) return false
  try {
    const url = new URL(value)
    return url.protocol === 'http:' || url.protocol === 'https:'
  } catch {
    return false
  }
}

function isActive(c: Company) {
  return c.isActive !== false
}

export function CompaniesPage() {
  const { id } = useParams()
  if (id) return <CompanyDetail id={id} />
  return <CompanyList />
}

function CompanyList() {
  const [query, setQuery] = useState('')
  const { data, error, isLoading, mutate } = useCompanies(query)
  const [name, setName] = useState('')
  const [slug, setSlug] = useState('')
  const [parentCompanyId, setParentCompanyId] = useState('')
  const [companyType, setCompanyType] = useState('')
  const [nickname, setNickname] = useState('')
  const [haloClientId, setHaloClientId] = useState('')
  const [ninjaOrganizationId, setNinjaOrganizationId] = useState('')
  const [haloPortalUrl, setHaloPortalUrl] = useState('')
  const [ninjaPortalUrl, setNinjaPortalUrl] = useState('')
  const [formError, setFormError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  const companies = Array.isArray(data) ? data : []

  async function onCreate(e: FormEvent) {
    e.preventDefault()
    setFormError(null)
    const trimmedName = name.trim()
    const trimmedSlug = slug.trim() || slugify(trimmedName)
    if (!trimmedName || !trimmedSlug) {
      setFormError('Name and slug are required.')
      return
    }

    setSubmitting(true)
    try {
      await createCompany({
        name: trimmedName,
        slug: trimmedSlug,
        parentCompanyId: parentCompanyId.trim() || null,
        companyType: companyType.trim() || null,
        nickname: nickname.trim() || null,
        haloClientId: haloClientId.trim() || null,
        ninjaOrganizationId: ninjaOrganizationId.trim() || null,
        haloPortalUrl: haloPortalUrl.trim() || null,
        ninjaPortalUrl: ninjaPortalUrl.trim() || null,
      })
      setName('')
      setSlug('')
      setParentCompanyId('')
      setCompanyType('')
      setNickname('')
      setHaloClientId('')
      setNinjaOrganizationId('')
      setHaloPortalUrl('')
      setNinjaPortalUrl('')
      await mutate()
    } catch (err) {
      setFormError(err instanceof Error ? err.message : 'Failed to create company.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div className="page">
      <h1>Companies</h1>
      <p>Client spaces distinct from the Entra tenant. Halo and Ninja IDs plus portal URLs link PSA and RMM systems of record — URLs only, no secrets.</p>

      <div className="toolbar">
        <input
          className="input"
          type="search"
          placeholder="Search name, slug, or external id…"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
        />
      </div>

      {isLoading && <p>Loading…</p>}
      {error && <p className="error">Failed to load companies.</p>}

      {!isLoading && !error && (
        <table className="data-table">
          <thead>
            <tr>
              <th>Name</th>
              <th>Slug</th>
              <th>HaloClientId</th>
              <th>NinjaOrganizationId</th>
              <th>IsActive</th>
            </tr>
          </thead>
          <tbody>
            {companies.length === 0 && (
              <tr>
                <td colSpan={5}>No companies match.</td>
              </tr>
            )}
            {companies.map((c) => (
              <tr key={c.id}>
                <td>
                  <Link to={`/companies/${c.id}`}>{c.name}</Link>
                </td>
                <td>{c.slug}</td>
                <td>{c.haloClientId || '—'}</td>
                <td>{c.ninjaOrganizationId || '—'}</td>
                <td>{isActive(c) ? 'Yes' : 'No'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <form className="panel" onSubmit={onCreate}>
        <h2>New company</h2>
        {formError && <p className="error">{formError}</p>}
        <div className="form-grid">
          <label>
            Name
            <input
              className="input"
              required
              value={name}
              onChange={(e) => {
                const next = e.target.value
                setName(next)
                if (!slug || slug === slugify(name)) setSlug(slugify(next))
              }}
            />
          </label>
          <label>
            Slug
            <input className="input" required value={slug} onChange={(e) => setSlug(e.target.value)} />
          </label>
          <label>
            Parent company (optional guid)
            <select className="input" value={parentCompanyId} onChange={(e) => setParentCompanyId(e.target.value)}>
              <option value="">None</option>
              {companies.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name}
                </option>
              ))}
            </select>
          </label>
          <label>
            Company type (optional)
            <input className="input" value={companyType} onChange={(e) => setCompanyType(e.target.value)} />
          </label>
          <label>
            Nickname (optional)
            <input className="input" value={nickname} onChange={(e) => setNickname(e.target.value)} />
          </label>
          <label>
            Halo client ID (optional)
            <input className="input" value={haloClientId} onChange={(e) => setHaloClientId(e.target.value)} />
          </label>
          <label>
            Ninja organization ID (optional)
            <input className="input" value={ninjaOrganizationId} onChange={(e) => setNinjaOrganizationId(e.target.value)} />
          </label>
          <label>
            Halo portal URL (optional)
            <input
              className="input"
              type="url"
              placeholder="https://…"
              value={haloPortalUrl}
              onChange={(e) => setHaloPortalUrl(e.target.value)}
            />
          </label>
          <label>
            Ninja portal URL (optional)
            <input
              className="input"
              type="url"
              placeholder="https://…"
              value={ninjaPortalUrl}
              onChange={(e) => setNinjaPortalUrl(e.target.value)}
            />
          </label>
        </div>
        <button className="btn" type="submit" disabled={submitting}>
          {submitting ? 'Creating…' : 'Create company'}
        </button>
      </form>
    </div>
  )
}

function detailRows(company: Company) {
  const rows: [string, ReactNode][] = [
    ['Name', company.name],
    ['Slug', company.slug],
    ['HaloClientId', company.haloClientId || '—'],
    ['NinjaOrganizationId', company.ninjaOrganizationId || '—'],
    ['IsActive', isActive(company) ? 'Yes' : 'No'],
  ]
  if (company.nickname) rows.push(['Nickname', company.nickname])
  if (company.companyType) rows.push(['Company type', company.companyType])
  if (company.parentCompanyId) {
    rows.push([
      'Parent company',
      <Link key="parent-company" to={`/companies/${company.parentCompanyId}`}>{company.parentCompanyId}</Link>,
    ])
  }
  if (company.companyNumber) rows.push(['Company number', company.companyNumber])
  if (company.primaryDomain) rows.push(['Primary domain', company.primaryDomain])
  if (company.address) rows.push(['Address', company.address])
  if (company.city) rows.push(['City', company.city])
  if (company.state) rows.push(['State', company.state])
  if (company.country) rows.push(['Country', company.country])
  if (company.postalCode) rows.push(['Postal code', company.postalCode])
  if (company.phone) rows.push(['Phone', company.phone])
  if (company.fax) rows.push(['Fax', company.fax])
  if (company.website) rows.push(['Website', company.website])
  if (company.hoursOfOperation) rows.push(['Hours', company.hoursOfOperation])
  if (company.notes) rows.push(['Notes', company.notes])
  return rows
}

function RelatedSection({
  title,
  href,
  count,
  items,
  empty,
}: {
  title: string
  href: string
  count?: number
  items?: RelatedListItem[] | null
  empty: string
}) {
  const list = items ?? []
  return (
    <section className="panel related-panel">
      <h2>
        <Link to={href}>{title}</Link>
        <span className="muted"> {count ?? list.length}</span>
      </h2>
      {list.length === 0 ? (
        <p>{empty}</p>
      ) : (
        <ul className="related-list">
          {list.map((item) => (
            <li key={item.id}>
              <Link to={href}>{item.name}</Link>
              {item.runCount != null && (
                <span className="muted"> · {item.runCount === 1 ? '1 run' : `${item.runCount} runs`}</span>
              )}
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}


const linkTypes = ['Asset', 'Document', 'Runbook', 'KeeperLink', 'Company'] as const

function hrefForLink(type: string, id: string) {
  switch (type) {
    case 'Company':
      return `/companies/${id}`
    case 'Asset':
      return '/assets'
    case 'Document':
      return '/documents'
    case 'Runbook':
      return '/runbooks'
    case 'KeeperLink':
      return '/keeper'
    default:
      return '#'
  }
}

function nodeName(graph: CompanyGraph, type: string, id: string) {
  return graph.nodes.find((n) => n.type === type && n.id === id)?.name ?? id
}

function CompanyGraphSection({ companyId }: { companyId: string }) {
  const { data: graph, error, isLoading } = useCompanyGraph(companyId)
  const nodes = graph?.nodes ?? []
  const edges = graph?.edges ?? []

  return (
    <section className="panel related-panel graph-panel">
      <h2>
        Relationships
        <span className="muted"> {edges.length}</span>
      </h2>
      <p className="muted">ResourceLink graph for this company — documents, assets, runbooks, and Keeper links. Not a Hudu visualization.</p>
      {isLoading && <p>Loading relationships…</p>}
      {error && <p className="error">Failed to load relationship graph.</p>}
      {graph && edges.length === 0 && (
        <p>No ResourceLink edges for this company yet.</p>
      )}
      {graph && nodes.length > 0 && (
        <ul className="graph-nodes">
          {nodes.map((node) => (
            <li key={`${node.type}:${node.id}`}>
              <Link to={hrefForLink(node.type, node.id)}>{node.name}</Link>
              <span className="muted"> {node.type}</span>
            </li>
          ))}
        </ul>
      )}
      {graph && edges.length > 0 && (
        <ul className="graph-edges">
          {edges.map((edge) => (
            <li key={edge.id}>
              <Link to={hrefForLink(edge.fromType, edge.fromId)}>{nodeName(graph, edge.fromType, edge.fromId)}</Link>
              <span className="muted"> {edge.fromType}</span>
              <span className="graph-arrow"> → </span>
              <Link to={hrefForLink(edge.toType, edge.toId)}>{nodeName(graph, edge.toType, edge.toId)}</Link>
              <span className="muted"> {edge.toType}</span>
              {edge.label ? <span className="muted"> · {edge.label}</span> : null}
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}

function RelatedLinksSection({
  companyId,
  count,
  items,
  onCreated,
}: {
  companyId: string
  count?: number
  items?: RelatedLinkItem[] | null
  onCreated: () => Promise<unknown>
}) {
  const list = items ?? []
  const [toType, setToType] = useState<string>('Asset')
  const [toId, setToId] = useState('')
  const [label, setLabel] = useState('')
  const [formError, setFormError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  async function onCreate(e: FormEvent) {
    e.preventDefault()
    setFormError(null)
    const id = toId.trim()
    if (!id) {
      setFormError('Id is required.')
      return
    }
    setSubmitting(true)
    try {
      await createResourceLink({
        fromType: 'Company',
        fromId: companyId,
        toType,
        toId: id,
        label: label.trim() || null,
      })
      setToId('')
      setLabel('')
      await onCreated()
    } catch (err) {
      setFormError(err instanceof Error ? err.message : 'Failed to create link.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <section className="panel related-panel">
      <h2>
        Related
        <span className="muted"> {count ?? list.length}</span>
      </h2>
      {list.length === 0 ? (
        <p>No related records linked yet.</p>
      ) : (
        <ul className="related-list">
          {list.map((item) => (
            <li key={item.id}>
              <span className="muted">{item.entityType}</span>{' '}
              <Link to={hrefForLink(item.entityType, item.entityId)}>{item.name}</Link>
              {item.label ? <span className="muted"> · {item.label}</span> : null}
            </li>
          ))}
        </ul>
      )}
      <form className="link-create" onSubmit={onCreate}>
        <label>
          Type
          <select className="input" value={toType} onChange={(e) => setToType(e.target.value)}>
            {linkTypes.map((type) => (
              <option key={type} value={type}>{type}</option>
            ))}
          </select>
        </label>
        <label>
          Id
          <input className="input" value={toId} onChange={(e) => setToId(e.target.value)} placeholder="record id" />
        </label>
        <label>
          Label
          <input className="input" value={label} onChange={(e) => setLabel(e.target.value)} placeholder="optional" />
        </label>
        <button className="btn" type="submit" disabled={submitting}>
          {submitting ? 'Linking…' : 'Add link'}
        </button>
      </form>
      {formError && <p className="error">{formError}</p>}
    </section>
  )
}

function CompanyDetail({ id }: { id: string }) {
  const { data: company, error, isLoading, mutate } = useCompany(id)
  const { mutate: mutateGraph } = useCompanyGraph(id)
  const [haloPortalUrl, setHaloPortalUrl] = useState('')
  const [ninjaPortalUrl, setNinjaPortalUrl] = useState('')
  const [formError, setFormError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [hydrated, setHydrated] = useState<string | null>(null)

  if (company && hydrated !== company.id) {
    setHaloPortalUrl(company.haloPortalUrl ?? '')
    setNinjaPortalUrl(company.ninjaPortalUrl ?? '')
    setHydrated(company.id)
  }

  async function onSaveUrls(e: FormEvent) {
    e.preventDefault()
    setFormError(null)
    setSubmitting(true)
    try {
      await updateCompany(id, {
        haloPortalUrl: haloPortalUrl.trim() || '',
        ninjaPortalUrl: ninjaPortalUrl.trim() || '',
      })
      await mutate()
    } catch (err) {
      setFormError(err instanceof Error ? err.message : 'Failed to update portal URLs.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div className="page">
      <p>
        <Link to="/companies">← Companies</Link>
      </p>
      <h1>{company?.name ?? 'Company'}</h1>
      {company && (
        <div className="portal-links">
          {isHttpUrl(company.haloPortalUrl) && (
            <a className="btn" href={company.haloPortalUrl} target="_blank" rel="noopener noreferrer">
              Open in Halo
            </a>
          )}
          {isHttpUrl(company.ninjaPortalUrl) && (
            <a className="btn" href={company.ninjaPortalUrl} target="_blank" rel="noopener noreferrer">
              Open in Ninja
            </a>
          )}
        </div>
      )}
      {isLoading && <p>Loading…</p>}
      {error && <p className="error">Failed to load company.</p>}
      {company && (
        <>
          <dl className="detail-grid">
            {detailRows(company).map(([label, value]) => (
              <div key={label} className="detail-row">
                <dt>{label}</dt>
                <dd>{value}</dd>
              </div>
            ))}
          </dl>
          <form className="panel" onSubmit={onSaveUrls}>
            <h2>PSA / RMM portal links</h2>
            <p className="muted">Store Halo and Ninja portal URLs only — never API secrets.</p>
            {formError && <p className="error">{formError}</p>}
            <div className="form-grid">
              <label>
                Halo portal URL
                <input
                  className="input"
                  type="url"
                  placeholder="https://…"
                  value={haloPortalUrl}
                  onChange={(e) => setHaloPortalUrl(e.target.value)}
                />
              </label>
              <label>
                Ninja portal URL
                <input
                  className="input"
                  type="url"
                  placeholder="https://…"
                  value={ninjaPortalUrl}
                  onChange={(e) => setNinjaPortalUrl(e.target.value)}
                />
              </label>
            </div>
            <button className="btn" type="submit" disabled={submitting}>
              {submitting ? 'Saving…' : 'Save portal links'}
            </button>
          </form>
          <div className="related-grid">
            <RelatedSection
              title="Assets"
              href="/assets"
              count={company.counts?.assets}
              items={company.assets}
              empty="No assets linked to this company."
            />
            <RelatedSection
              title="Documents"
              href="/documents"
              count={company.counts?.documents}
              items={company.documents}
              empty="No documents linked to this company."
            />
            <RelatedSection
              title="Runbooks"
              href="/runbooks"
              count={company.counts?.runbooks}
              items={company.runbooks}
              empty="No runbooks linked to this company."
            />
            <RelatedSection
              title="Keeper links"
              href="/keeper"
              count={company.counts?.keeperLinks}
              items={company.keeperLinks}
              empty="No Keeper links for this company."
            />
            <RelatedLinksSection
              companyId={company.id}
              count={company.counts?.relatedLinks}
              items={company.relatedLinks}
              onCreated={() => Promise.all([mutate(), mutateGraph()])}
            />
            <CompanyGraphSection companyId={company.id} />
          </div>
        </>
      )}
    </div>
  )
}
