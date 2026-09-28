import { useMsal } from '@azure/msal-react'
import { Link, NavLink, Outlet } from 'react-router-dom'
import { ApiError, canEditContent, canManageUsers, ipBlockedAddress, useProfile } from '../hooks/useApi'

export function Layout() {
  const { data: profile, error: profileError, isLoading } = useProfile()
  const { instance } = useMsal()
  const account = instance.getActiveAccount() ?? instance.getAllAccounts()[0]
  const showUsers = canManageUsers(profile?.role)
  const showSchema = canEditContent(profile?.role)
  // The API refuses a suspended user, or an address outside the tenant's IP allowlist, on every
  // route; say which once instead of failing every page.
  const blockedIp = ipBlockedAddress(profileError)
  const suspended = !blockedIp && profileError instanceof ApiError && profileError.status === 403

  return (
    <div className="app-shell">
      <header className="app-header">
        <Link to="/" className="brand">DocuEngAIne</Link>
        <nav className="app-nav">
          <NavLink to="/" end>Dashboard</NavLink>
          <NavLink to="/companies">Companies</NavLink>
          <NavLink to="/assets">Assets</NavLink>
          {showSchema ? <NavLink to="/asset-layouts">Asset layouts</NavLink> : null}
          {showSchema ? <NavLink to="/option-lists">Option lists</NavLink> : null}
          <NavLink to="/documents">Docs</NavLink>
          <NavLink to="/runbooks">Runbooks</NavLink>
          <NavLink to="/runs">Runs</NavLink>
          <NavLink to="/expirations">Expirations</NavLink>
          <NavLink to="/flags">Flags</NavLink>
          <NavLink to="/keeper">Keeper</NavLink>
          <NavLink to="/museum">Museum</NavLink>
          <NavLink to="/portal">Portal</NavLink>
          <NavLink to="/integrations">Integrations</NavLink>
          {showUsers ? <NavLink to="/users">Users</NavLink> : null}
          {showUsers ? <NavLink to="/audit">Audit</NavLink> : null}
          {showUsers ? <NavLink to="/access-reviews">Access reviews</NavLink> : null}
          {showUsers ? <NavLink to="/security-groups">Security groups</NavLink> : null}
          {showUsers ? <NavLink to="/ip-access">IP access</NavLink> : null}
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
