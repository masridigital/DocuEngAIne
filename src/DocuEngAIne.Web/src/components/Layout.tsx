import { useMsal } from '@azure/msal-react'
import { Link, NavLink, Outlet } from 'react-router-dom'
import { canManageUsers, useProfile } from '../hooks/useApi'

export function Layout() {
  const { data: profile, isLoading } = useProfile()
  const { instance } = useMsal()
  const account = instance.getActiveAccount() ?? instance.getAllAccounts()[0]
  const showUsers = canManageUsers(profile?.role)

  return (
    <div className="app-shell">
      <header className="app-header">
        <Link to="/" className="brand">DocuEngAIne</Link>
        <nav className="app-nav">
          <NavLink to="/" end>Dashboard</NavLink>
          <NavLink to="/companies">Companies</NavLink>
          <NavLink to="/assets">Assets</NavLink>
          <NavLink to="/documents">Docs</NavLink>
          <NavLink to="/runbooks">Runbooks</NavLink>
          <NavLink to="/runs">Runs</NavLink>
          <NavLink to="/expirations">Expirations</NavLink>
          <NavLink to="/flags">Flags</NavLink>
          <NavLink to="/keeper">Keeper</NavLink>
          <NavLink to="/portal">Portal</NavLink>
          <NavLink to="/integrations">Integrations</NavLink>
          {showUsers ? <NavLink to="/users">Users</NavLink> : null}
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
        <Outlet />
      </main>
    </div>
  )
}
