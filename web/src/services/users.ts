import { apiRequest } from '@/api/client'

export interface UserItem {
  id: string
  loginName: string
  name: string
  authSource: string
  erpOperatorCode: string | null
  erpOperatorName: string | null
  isAdmin: boolean
  isEnabled: boolean
  lastLoginAt: string | null
  failedAttemptCount: number
  lockedUntil: string | null
  passwordChangedAt: string | null
  rowVersion: string
}

export async function fetchUsers(): Promise<UserItem[]> {
  return apiRequest<UserItem[]>('/api/v1/admin/users')
}

export interface UserInput {
  id: string
  loginName: string
  name: string
  erpOperatorCode: string | null
  erpOperatorName: string | null
  isAdmin: boolean
  isEnabled: boolean
  password?: string
}

export interface UserFunctionPermission {
  functionCode: string
  functionName: string
  functionKind: string
  groupCode: string
  groupName: string
  groupSort: number
  functionSort: number
  state: 'INHERIT' | 'ALLOW' | 'DENY'
  isEffective: boolean
  dataScope: unknown
  rowVersion: string | null
}

export interface UserAdminAuditItem {
  id: number
  action: string
  actorUserId: string
  actorName: string
  targetUserId: string
  targetName: string
  requestId: string
  createdAt: string
}

export async function createUser(input: UserInput): Promise<UserItem> {
  return apiRequest<UserItem>('/api/v1/admin/users', {
    method: 'POST',
    body: JSON.stringify({ ...input, authSource: 'LOCAL' }),
  })
}

export async function updateUser(user: UserItem, input: UserInput): Promise<UserItem> {
  return apiRequest<UserItem>(`/api/v1/admin/users/${encodeURIComponent(user.id)}`, {
    method: 'PUT',
    body: JSON.stringify({
      name: input.name,
      erpOperatorCode: input.erpOperatorCode,
      erpOperatorName: input.erpOperatorName,
      isAdmin: input.isAdmin,
      isEnabled: input.isEnabled,
      expectedRowVersion: user.rowVersion,
    }),
  })
}

export async function resetUserPassword(userId: string, password: string): Promise<void> {
  await apiRequest(`/api/v1/admin/users/${encodeURIComponent(userId)}/password`, {
    method: 'PUT',
    body: JSON.stringify({ password }),
  })
}

export async function fetchUserPermissions(userId: string): Promise<UserFunctionPermission[]> {
  return apiRequest<UserFunctionPermission[]>(
    `/api/v1/admin/users/${encodeURIComponent(userId)}/functions`,
  )
}

export async function updateUserPermission(
  userId: string,
  permission: UserFunctionPermission,
): Promise<void> {
  await apiRequest(
    `/api/v1/admin/users/${encodeURIComponent(userId)}/functions/${encodeURIComponent(permission.functionCode)}`,
    {
      method: 'PUT',
      body: JSON.stringify({
        state: permission.state,
        dataScope: permission.dataScope,
        expectedRowVersion: permission.rowVersion,
      }),
    },
  )
}

export async function unlockUser(userId: string): Promise<void> {
  await apiRequest(`/api/v1/admin/users/${encodeURIComponent(userId)}/unlock`, { method: 'POST' })
}

export async function revokeUserSessions(userId: string): Promise<number> {
  const result = await apiRequest<{ revoked: number }>(
    `/api/v1/admin/users/${encodeURIComponent(userId)}/sessions/revoke`,
    { method: 'POST' },
  )
  return result.revoked
}

export async function resetUserPreferences(userId: string): Promise<number> {
  const result = await apiRequest<{ deleted: number }>(
    `/api/v1/admin/users/${encodeURIComponent(userId)}/preferences/reset`,
    { method: 'POST' },
  )
  return result.deleted
}

export async function fetchUserAudit(userId: string): Promise<UserAdminAuditItem[]> {
  return apiRequest<UserAdminAuditItem[]>(
    `/api/v1/admin/users/audit?userId=${encodeURIComponent(userId)}&limit=100`,
  )
}
