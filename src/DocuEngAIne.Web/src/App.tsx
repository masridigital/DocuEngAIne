import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom'
import './App.css'
import { Layout } from './components/Layout'
import { AccessReviewsPage } from './pages/AccessReviewsPage'
import { AssetsPage } from './pages/AssetsPage'
import { AuditPage } from './pages/AuditPage'
import { CompaniesPage } from './pages/CompaniesPage'
import { DashboardPage } from './pages/DashboardPage'
import { DocumentsPage } from './pages/DocumentsPage'
import { ExpirationsPage } from './pages/ExpirationsPage'
import { FlagsPage } from './pages/FlagsPage'
import { IntegrationsPage } from './pages/IntegrationsPage'
import { KeeperPage } from './pages/KeeperPage'
import { MuseumPage } from './pages/MuseumPage'
import { RunbooksPage } from './pages/RunbooksPage'
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
          <Route path="documents" element={<DocumentsPage />} />
          <Route path="runbooks" element={<RunbooksPage />} />
          <Route path="runs" element={<RunsPage />} />
          <Route path="expirations" element={<ExpirationsPage />} />
          <Route path="flags" element={<FlagsPage />} />
          <Route path="keeper" element={<KeeperPage />} />
          <Route path="integrations" element={<IntegrationsPage />} />
          <Route path="users" element={<UsersPage />} />
          <Route path="audit" element={<AuditPage />} />
          <Route path="access-reviews" element={<AccessReviewsPage />} />
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
