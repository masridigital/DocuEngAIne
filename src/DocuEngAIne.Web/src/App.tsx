import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom'
import './App.css'
import { Layout } from './components/Layout'
import { AssetsPage } from './pages/AssetsPage'
import { CompaniesPage } from './pages/CompaniesPage'
import { DashboardPage } from './pages/DashboardPage'
import { DocumentsPage } from './pages/DocumentsPage'
import { ExpirationsPage } from './pages/ExpirationsPage'
import { IntegrationsPage } from './pages/IntegrationsPage'
import { KeeperPage } from './pages/KeeperPage'
import { RunbooksPage } from './pages/RunbooksPage'

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
          <Route path="expirations" element={<ExpirationsPage />} />
          <Route path="keeper" element={<KeeperPage />} />
          <Route path="integrations" element={<IntegrationsPage />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Route>
      </Routes>
    </BrowserRouter>
  )
}

export default App
