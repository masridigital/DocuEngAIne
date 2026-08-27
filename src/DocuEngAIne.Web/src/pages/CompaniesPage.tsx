import { useState, type FormEvent } from 'react'
import { Link, useParams } from 'react-router-dom'
import { createCompany, useCompanies, useCompany, type Company, type RelatedListItem } from '../hooks/useApi'

function slugify(value: string) {
  return value
    .toLowerCase()
    .trim()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
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
  const [haloClientId, setHaloClientId] = useState('')
  const [ninjaOrganizationId, setNinjaOrganizationId] = useState('')
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
        haloClientId: haloClientId.trim() || null,
        ninjaOrganizationId: ninjaOrganizationId.trim() || null,
      })
      setName('')
      setSlug('')
      setHaloClientId('')
      setNinjaOrganizationId('')
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
      <p>Client spaces distinct from the Entra tenant. Halo and Ninja IDs link PSA and RMM systems of record.</p>

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
            Halo client ID (optional)
            <input className="input" value={haloClientId} onChange={(e) => setHaloClientId(e.target.value)} />
          </label>
          <label>
            Ninja organization ID (optional)
            <input className="input" value={ninjaOrganizationId} onChange={(e) => setNinjaOrganizationId(e.target.value)} />
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
  const rows: [string, string][] = [
    ['Name', company.name],
    ['Slug', company.slug],
    ['HaloClientId', company.haloClientId || '—'],
    ['NinjaOrganizationId', company.ninjaOrganizationId || '—'],
    ['IsActive', isActive(company) ? 'Yes' : 'No'],
  ]
  if (company.companyNumber) rows.push(['Company number', company.companyNumber])
  if (company.primaryDomain) rows.push(['Primary domain', company.primaryDomain])
  if (company.address) rows.push(['Address', company.address])
  if (company.city) rows.push(['City', company.city])
  if (company.state) rows.push(['State', company.state])
  if (company.phone) rows.push(['Phone', company.phone])
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
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}

function CompanyDetail({ id }: { id: string }) {
  const { data: company, error, isLoading } = useCompany(id)

  return (
    <div className="page">
      <p>
        <Link to="/companies">← Companies</Link>
      </p>
      <h1>{company?.name ?? 'Company'}</h1>
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
          </div>
        </>
      )}
    </div>
  )
}
