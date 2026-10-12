import { readStoredAuthSession } from '@/services/auth-session'
import { getRuntimeConfig } from '@/services/runtime-config'
import { createUniqueId } from '@/utils/unique-id'

export interface ApiEnvelope<T> {
  success: boolean
  code: string
  message: string
  data: T | null
  requestId: string
  serverTime: string
}

export class ApiError extends Error {
  readonly status: number
  readonly code: string
  readonly requestId: string

  constructor(message: string, status = 0, code = 'NETWORK_ERROR', requestId = '') {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.code = code
    this.requestId = requestId
  }
}

interface ApiRequestOptions extends RequestInit {
  auth?: boolean
  timeoutMs?: number
}

let unauthorizedHandler: (() => void | Promise<void>) | null = null

function apiUrl(path: string): string {
  const baseUrl = getRuntimeConfig().apiBaseUrl.replace(/\/$/, '')
  return `${baseUrl}${path.startsWith('/') ? path : `/${path}`}`
}

function isApiEnvelope(value: unknown): value is ApiEnvelope<unknown> {
  if (!value || typeof value !== 'object') return false
  const candidate = value as Partial<ApiEnvelope<unknown>>
  return (
    typeof candidate.success === 'boolean' &&
    typeof candidate.code === 'string' &&
    typeof candidate.message === 'string' &&
    typeof candidate.requestId === 'string' &&
    typeof candidate.serverTime === 'string'
  )
}

export function setUnauthorizedHandler(handler: () => void | Promise<void>): void {
  unauthorizedHandler = handler
}

export async function apiRequest<T>(
  path: string,
  options: ApiRequestOptions = {},
): Promise<T> {
  const { auth = true, timeoutMs = 15_000, ...requestInit } = options
  const headers = new Headers(requestInit.headers)
  const config = getRuntimeConfig()

  headers.set('Accept', 'application/json')
  headers.set('X-Request-ID', createUniqueId('request'))
  headers.set('X-H5-Version', config.h5Version)
  headers.set('X-Bridge-Version', config.bridgeProtocol)
  if (requestInit.body && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json')
  }

  if (auth) {
    const session = readStoredAuthSession()
    if (!session) {
      if (unauthorizedHandler) await unauthorizedHandler()
      throw new ApiError('请先登录。', 401, 'AUTH_REQUIRED')
    }
    headers.set('Authorization', `Bearer ${session.token}`)
  }

  const controller = new AbortController()
  let timedOut = false
  let timeout = 0

  let response: Response
  let payload: unknown
  try {
    const request = (async () => {
      const currentResponse = await fetch(apiUrl(path), {
        ...requestInit,
        headers,
        signal: controller.signal,
        cache: requestInit.cache ?? 'no-store',
      })
      try {
        return { response: currentResponse, payload: await currentResponse.json() as unknown }
      } catch (error) {
        if (timedOut) throw error
        throw new ApiError('服务返回格式错误。', currentResponse.status, 'INVALID_RESPONSE')
      }
    })()
    const deadline = new Promise<never>((_, reject) => {
      timeout = window.setTimeout(() => {
        timedOut = true
        controller.abort()
        reject(new ApiError('请求超时，请稍后重试。', 0, 'REQUEST_TIMEOUT'))
      }, timeoutMs)
    })
    const result = await Promise.race([request, deadline])
    response = result.response
    payload = result.payload
  } catch (error) {
    if (error instanceof ApiError) throw error
    if (timedOut || (error instanceof DOMException && error.name === 'AbortError')) {
      throw new ApiError('请求超时，请稍后重试。', 0, 'REQUEST_TIMEOUT')
    }
    throw new ApiError('网络连接失败，请检查网络。')
  } finally {
    window.clearTimeout(timeout)
  }

  if (!isApiEnvelope(payload)) {
    throw new ApiError('服务返回格式错误。', response.status, 'INVALID_RESPONSE')
  }
  const envelope = payload as ApiEnvelope<T>

  if (response.status === 401 && auth && unauthorizedHandler) {
    await unauthorizedHandler()
  }

  if (!response.ok || !envelope.success || envelope.data === null) {
    throw new ApiError(
      envelope.message || '请求失败。',
      response.status,
      envelope.code || 'REQUEST_FAILED',
      envelope.requestId,
    )
  }

  return envelope.data
}
