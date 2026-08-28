import { MsalProvider } from '@azure/msal-react'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import { AuthConfigNotice, AuthGate } from './auth/AuthGate'
import { initializeMsal, msalInstance } from './auth/msalConfig'

const root = createRoot(document.getElementById('root')!)

async function bootstrap() {
  // Nothing to initialize when the build has no Entra configuration — say so
  // instead of white-screening on a failed MSAL constructor.
  if (!msalInstance) {
    root.render(
      <StrictMode>
        <AuthConfigNotice />
      </StrictMode>,
    )
    return
  }

  // MSAL v3+ requires initialize() (and the redirect promise to settle) before render.
  try {
    await initializeMsal()
  } catch (error) {
    root.render(
      <StrictMode>
        <div className="app-main">
          <div className="page panel">
            <h1>Sign-in failed to start</h1>
            <p className="error">{error instanceof Error ? error.message : String(error)}</p>
          </div>
        </div>
      </StrictMode>,
    )
    return
  }

  root.render(
    <StrictMode>
      <MsalProvider instance={msalInstance}>
        <AuthGate>
          <App />
        </AuthGate>
      </MsalProvider>
    </StrictMode>,
  )
}

void bootstrap()
