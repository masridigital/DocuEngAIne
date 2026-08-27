export function DashboardPage() {
  return (
    <div className="page">
      <h1>Dashboard</h1>
      <p>Welcome to DocuEngAIne — your MSP documentation hub.</p>
      <div className="card-grid">
        <div className="card">
          <h3>Assets</h3>
          <p>Document servers, switches, firewalls, and custom asset types.</p>
        </div>
        <div className="card">
          <h3>Knowledge Base</h3>
          <p>Versioned documents and SOPs for your team and clients.</p>
        </div>
        <div className="card">
          <h3>Runbooks</h3>
          <p>Step-by-step procedures with ordered checks.</p>
        </div>
        <div className="card">
          <h3>Keeper Links</h3>
          <p>Link to credentials in Keeper without storing secrets here.</p>
        </div>
      </div>
    </div>
  )
}
