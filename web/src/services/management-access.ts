import { apiRequest } from '@/api/client'

export async function hasManagementAccess(): Promise<boolean> {
  const result = await apiRequest<{ allowed: boolean }>('/api/v1/auth/management-access')
  return result.allowed
}

export async function requestManagementAccess(password: string): Promise<void> {
  await apiRequest('/api/v1/auth/management-access', {
    method: 'POST',
    body: JSON.stringify({ password }),
  })
}
