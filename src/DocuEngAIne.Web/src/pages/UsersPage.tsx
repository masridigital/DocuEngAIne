import { useState } from 'react'
import {
  canManageUsers,
  updateUserRole,
  useProfile,
  useUsers,
  USER_ROLES,
  type TenantUser,
  type UserRole,
} from '../hooks/useApi'

function formatTimestamp(value?: string | null) {
  if (!value) return '—'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return value
  return date.toLocaleString()
}

export function UsersPage() {
  const { data: profile, isLoading: profileLoading } = useProfile()
  const allowed = canManageUsers(profile?.role)
  const { data, error, isLoading, mutate } = useUsers(allowed)
  const users: TenantUser[] = Array.isArray(data) ? data : []

  const [message, setMessage] = useState<string | null>(null)
  const [errorMessage, setErrorMessage] = useState<string | null>(null)
  const [busyId, setBusyId] = useState<string | null>(null)

  const activeOwnerCount = users.filter((u) => u.role === 'Owner' && u.isActive).length

  async function onRoleChange(user: TenantUser, role: UserRole) {
    if (role === user.role) return
    setMessage(null)
    setErrorMessage(null)
    setBusyId(user.id)
    try {
      await updateUserRole(user.id, role)
      setMessage(`Role for ${user.displayName || user.email} set to ${role}.`)
      await mutate()
    } catch (err) {
      setErrorMessage(err instanceof Error ? err.message : 'Failed to change role.')
    } finally {
      setBusyId(null)
    }
  }

  if (profileLoading) {
    return (
      <div className="page">
        <h1>Users</h1>
        <p>Loading…</p>
      </div>
    )
  }

  if (!allowed) {
    return (
      <div className="page">
        <h1>Users</h1>
        <p>Users is available to Admin and Owner.</p>
      </div>
    )
  }

  return (
    <div className="page">
      <h1>Users</h1>
      <p>
        Tenant members and their tenant-wide roles. Changing a role takes effect immediately. The last
        active Owner cannot be demoted, and only an Owner can grant or revoke Owner.
      </p>
      {message && <p className="banner">{message}</p>}
      {errorMessage && <p className="error">{errorMessage}</p>}

      {isLoading && <p>Loading…</p>}
      {error && <p className="error">Failed to load users.</p>}

      {!isLoading && !error && (
        <table className="data-table">
          <thead>
            <tr>
              <th>Name</th>
              <th>Email</th>
              <th>Role</th>
              <th>IsActive</th>
              <th>LastSeenAt</th>
            </tr>
          </thead>
          <tbody>
            {users.length === 0 && (
              <tr>
                <td colSpan={5}>No users provisioned yet.</td>
              </tr>
            )}
            {users.map((u) => {
              const lastOwner = u.role === 'Owner' && u.isActive && activeOwnerCount === 1
              return (
                <tr key={u.id}>
                  <td>{u.displayName || '—'}</td>
                  <td>{u.email}</td>
                  <td>
                    <select
                      className="input"
                      value={u.role}
                      disabled={busyId === u.id || lastOwner}
                      title={lastOwner ? 'Cannot change the role of the tenant\'s last Owner.' : undefined}
                      onChange={(e) => void onRoleChange(u, e.target.value as UserRole)}
                    >
                      {USER_ROLES.map((role) => (
                        <option key={role} value={role}>{role}</option>
                      ))}
                    </select>
                  </td>
                  <td>{u.isActive ? 'Yes' : 'No'}</td>
                  <td>{formatTimestamp(u.lastSeenAt)}</td>
                </tr>
              )
            })}
          </tbody>
        </table>
      )}
    </div>
  )
}
