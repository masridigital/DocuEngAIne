import { useMsal } from '@azure/msal-react'
import { useEffect } from 'react'
import { Link, NavLink, Outlet } from 'react-router-dom'
import {
  ApiError,
  canEditContent,
  canManageUsers,
  DEFAULT_PRODUCT_NAME,
  featureEnabled,
  ipBlockedAddress,
  useProfile,
  useTenantConfiguration,
  useTerms,
} from '../hooks/useApi'
import { applyAccent } from './branding'

export function Layout() {
  const { data: profile, error: profileError, isLoading } = useProfile()
  const { instance } = useMsal()
  const account = instance.getActiveAccount() ?? instance.getAllAccounts()[0]
  const showUsers = canManageUsers(profile?.role)
  const showSchema = canEditContent(profile?.role)
  const { data: configuration } = useTenantConfiguration()
  const term = useTerms()
  const productName = configuration?.branding.displayName || DEFAULT_PRODUCT_NAME
  const accent = configuration?.branding.accentColor

  useEffect(() => {
    applyAccent(accent)
  }, [accent])

  useEffect(() => {
    document.title = productName
  }, [productName])
  // The API refuses a suspended user, or an address outside the tenant's IP allowlist, on every
  // route; say which once instead of failing every page.
  const blockedIp = ipBlockedAddress(profileError)
  const suspended = !blockedIp && profileError instanceof ApiError && profileError.status === 403

  return (
    <div className="app-shell">
      <header className="app-header">
        <Link to="/" className="brand">{productName}</Link>
        <nav className="app-nav">
          <NavLink to="/" end>Dashboard</NavLink>
          <NavLink to="/companies">{term('company')}</NavLink>
          <NavLink to="/assets">{term('asset')}</NavLink>
          {showSchema ? <NavLink to="/asset-layouts">{term('asset', 'singular')} layouts</NavLink> : null}
          {showSchema ? <NavLink to="/option-lists">Option lists</NavLink> : null}
          <NavLink to="/documents">{term('document')}</NavLink>
          <NavLink to="/runbooks">{term('runbook')}</NavLink>
          <NavLink to="/runs">Runs</NavLink>
          <NavLink to="/expirations">Expirations</NavLink>
          <NavLink to="/flags">Flags</NavLink>
          <NavLink to="/keeper">Keeper</NavLink>
          <NavLink to="/museum">Museum</NavLink>
          {featureEnabled(configuration, 'client_portal') ? <NavLink to="/portal">Portal</NavLink> : null}
          <NavLink to="/integrations">Integrations</NavLink>
          {showUsers ? <NavLink to="/users">Users</NavLink> : null}
          {showUsers ? <NavLink to="/audit">Audit</NavLink> : null}
          {showUsers && featureEnabled(configuration, 'access_reviews') ? <NavLink to="/access-reviews">Access reviews</NavLink> : null}
          {showUsers ? <NavLink to="/security-groups">Security groups</NavLink> : null}
          {showUsers ? <NavLink to="/ip-access">IP access</NavLink> : null}
          {showUsers ? <NavLink to="/settings">Settings</NavLink> : null}
        </nav>
        <div className="profile">
          <span>{isLoading ? '…' : profile?.displayName ?? profile?.email ?? account?.name ?? account?.username ?? 'Guest'}</span>
          {account ? (
            <button type="button" className="btn btn-secondary" onClick={() => void instance.logoutRedirect()}>
              Sign out
            </button>
          ) : null}
        </div>
      </header>
      <main className="app-main">
        {blockedIp ? (
          <div className="page">
            <h1>Network not allowed</h1>
            <p>
              This tenant only accepts requests from approved networks, and your address (<code>{blockedIp}</code>) is
              not one of them. Connect from an approved network, or ask a tenant administrator to add this address.
            </p>
          </div>
        ) : suspended ? (
          <div className="page">
            <h1>Access suspended</h1>
            <p>Your access to this tenant has been suspended. Contact a tenant administrator to have it restored.</p>
          </div>
        ) : (
          <Outlet />
        )}
      </main>
    </div>
  )
}
