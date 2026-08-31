import { Link, useParams } from 'react-router-dom'
import {
  usePortalCompanies,
  usePortalCompany,
  usePortalDocuments,
  usePortalExpirations,
  usePortalKeeperLinks,
  type ExpirationItem,
  type PortalDocument,
  type PortalKeeperLink,
} from '../hooks/useApi'

function formatDate(iso: string) {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso
  return d.toISOString().slice(0, 10)
}

export function PortalPage() {
  const { companyId } = useParams()
  if (companyId) return <PortalCompany companyId={companyId} />
  return <PortalHome />
}

function PortalHome() {
  const { data, error, isLoading } = usePortalCompanies()
  const companies = Array.isArray(data) ? data : []

  return (
    <div className="page portal-page">
      <h1>Client portal</h1>
      <p>
        Read-only company documents, expirations, and Keeper link titles. No password vault.
        Keeper reveal is not available here.
      </p>
      {isLoading && <p>Loading…</p>}
      {error && <p className="error">Failed to load portal companies.</p>}
      {!isLoading && !error && companies.length === 0 && (
        <p>No companies have the client portal enabled.</p>
      )}
      {companies.length > 0 && (
        <ul className="list">
          {companies.map((company) => (
            <li key={company.id} className="list-item">
              <h3>
                <Link to={`/portal/${company.id}`}>{company.name}</Link>
              </h3>
              <p>{company.slug}{company.website ? ` · ${company.website}` : ''}</p>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

function PortalCompany({ companyId }: { companyId: string }) {
  const { data: company, error, isLoading } = usePortalCompany(companyId)
  const { data: docData } = usePortalDocuments(companyId)
  const { data: expirationData } = usePortalExpirations(companyId)
  const { data: keeperData } = usePortalKeeperLinks(companyId)
  const documents: PortalDocument[] = Array.isArray(docData) ? docData : []
  const expirations: ExpirationItem[] = Array.isArray(expirationData) ? expirationData : []
  const keepers: PortalKeeperLink[] = Array.isArray(keeperData) ? keeperData : []

  return (
    <div className="page portal-page">
      <p>
        <Link to="/portal">← Client portal</Link>
      </p>
      <h1>{company?.name ?? 'Company'}</h1>
      {isLoading && <p>Loading…</p>}
      {error && <p className="error">This company is not available in the portal.</p>}
      {company && (
        <p className="muted">
          {company.website ?? company.slug}
          {company.phone ? ` · ${company.phone}` : ''}
          {company.hoursOfOperation ? ` · ${company.hoursOfOperation}` : ''}
          {' · '}
          {company.counts.documents} docs
          {' · '}
          {company.counts.expirations} expirations
          {' · '}
          {company.counts.keeperLinks} Keeper links
        </p>
      )}

      <section>
        <h2>Documents</h2>
        {documents.length === 0 && <p>No published documents for this company.</p>}
        {documents.map((doc) => (
          <article key={doc.id} className="list-item">
            <h3>{doc.title}</h3>
            {doc.summary && <p>{doc.summary}</p>}
            {doc.content && <p>{doc.content}</p>}
          </article>
        ))}
      </section>

      <section>
        <h2>Expirations</h2>
        {expirations.length === 0 && <p>No upcoming expirations.</p>}
        {expirations.length > 0 && (
          <table className="data-table">
            <thead>
              <tr>
                <th>Name</th>
                <th>Type</th>
                <th>Date</th>
                <th>Days</th>
              </tr>
            </thead>
            <tbody>
              {expirations.map((item) => (
                <tr key={`${item.sourceType}-${item.id}`}>
                  <td>{item.name}</td>
                  <td>{item.fieldName}</td>
                  <td>{formatDate(item.expiresAt)}</td>
                  <td>{item.daysUntil}d</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      <section>
        <h2>Keeper links</h2>
        <p className="muted">Titles only. Secrets stay in Keeper. Reveal is not offered here.</p>
        {keepers.length === 0 && <p>No Keeper links for this company.</p>}
        {keepers.length > 0 && (
          <ul className="list">
            {keepers.map((link) => (
              <li key={link.id} className="list-item">
                <h3>{link.title}</h3>
                <p>{link.hasRecordUrl ? 'Stored in Keeper' : 'No Keeper record linked'}</p>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  )
}
