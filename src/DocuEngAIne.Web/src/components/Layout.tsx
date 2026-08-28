import { Link, NavLink, Outlet } from 'react-router-dom'
import { useProfile } from '../hooks/useApi'

export function Layout() {
  const { data: profile, isLoading } = useProfile()

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
          <NavLink to="/integrations">Integrations</NavLink>
        </nav>
        <div className="profile">
          {isLoading ? '…' : profile?.displayName ?? profile?.email ?? 'Guest'}
        </div>
      </header>
      <main className="app-main">
        <Outlet />
      </main>
    </div>
  )
}
