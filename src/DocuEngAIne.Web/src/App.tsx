import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom'
import './App.css'
import { Layout } from './components/Layout'
import { AccessReviewsPage } from './pages/AccessReviewsPage'
import { AssetLayoutsPage } from './pages/AssetLayoutsPage'
import { AssetsPage } from './pages/AssetsPage'
import { AuditPage } from './pages/AuditPage'
import { SecurityEventsPage } from './pages/SecurityEventsPage'
import { CompaniesPage } from './pages/CompaniesPage'
import { DashboardPage } from './pages/DashboardPage'
import { DocumentsPage } from './pages/DocumentsPage'
import { ExpirationsPage } from './pages/ExpirationsPage'
import { FlagsPage } from './pages/FlagsPage'
import { IntegrationsPage } from './pages/IntegrationsPage'
import { IpAccessPage } from './pages/IpAccessPage'
import { KeeperPage } from './pages/KeeperPage'
import { MuseumPage } from './pages/MuseumPage'
import { OptionListsPage } from './pages/OptionListsPage'
import { PlatformPage } from './pages/PlatformPage'
import { RunbooksPage } from './pages/RunbooksPage'
import { SecurityGroupsPage } from './pages/SecurityGroupsPage'
import { SettingsPage } from './pages/SettingsPage'
import { RunsPage } from './pages/RunsPage'
import { UsersPage } from './pages/UsersPage'
import { PortalPage } from './pages/PortalPage'

function App() {
  return (
    <BrowserRouter>
      <Routes>
        <Route path="/" element={<Layout />}>
          <Route index element={<DashboardPage />} />
          <Route path="companies" element={<CompaniesPage />} />
          <Route path="companies/:id" element={<CompaniesPage />} />
          <Route path="assets" element={<AssetsPage />} />
          <Route path="asset-layouts" element={<AssetLayoutsPage />} />
          <Route path="option-lists" element={<OptionListsPage />} />
          <Route path="documents" element={<DocumentsPage />} />
          <Route path="runbooks" element={<RunbooksPage />} />
          <Route path="runs" element={<RunsPage />} />
          <Route path="expirations" element={<ExpirationsPage />} />
          <Route path="flags" element={<FlagsPage />} />
          <Route path="keeper" element={<KeeperPage />} />
          <Route path="integrations" element={<IntegrationsPage />} />
          <Route path="users" element={<UsersPage />} />
          <Route path="audit" element={<AuditPage />} />
          <Route path="security-events" element={<SecurityEventsPage />} />
          <Route path="access-reviews" element={<AccessReviewsPage />} />
          <Route path="ip-access" element={<IpAccessPage />} />
          <Route path="security-groups" element={<SecurityGroupsPage />} />
          <Route path="settings" element={<SettingsPage />} />
          <Route path="platform" element={<PlatformPage />} />
          <Route path="museum" element={<MuseumPage />} />
          <Route path="portal" element={<PortalPage />} />
          <Route path="portal/:companyId" element={<PortalPage />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Route>
      </Routes>
    </BrowserRouter>
  )
}

export default App
