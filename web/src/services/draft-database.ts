import { getRuntimeConfig } from '@/services/runtime-config'
import type { StoredOutboundDocument } from '@/services/outbound-document-drafts'

const storeName = 'documents'
let databasePromise: Promise<IDBDatabase> | null = null

function requestResult<T>(request: IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    request.onsuccess = () => resolve(request.result)
    request.onerror = () => reject(request.error ?? new Error('本地草稿读取失败'))
  })
}

function transactionDone(transaction: IDBTransaction): Promise<void> {
  return new Promise((resolve, reject) => {
    transaction.oncomplete = () => resolve()
    transaction.onerror = () => reject(transaction.error ?? new Error('本地草稿保存失败'))
    transaction.onabort = () => reject(transaction.error ?? new Error('本地草稿保存失败'))
  })
}

function openDatabase(): Promise<IDBDatabase> {
  if (databasePromise) return databasePromise
  databasePromise = new Promise((resolve, reject) => {
    const request = indexedDB.open(`label-system-drafts-${getRuntimeConfig().environment}`, 1)
    request.onupgradeneeded = () => {
      const database = request.result
      if (!database.objectStoreNames.contains(storeName)) {
        const store = database.createObjectStore(storeName, { keyPath: 'id' })
        store.createIndex('documentType', 'documentType')
        store.createIndex('ownerUserId', 'ownerUserId')
        store.createIndex('syncStatus', 'syncStatus')
      }
    }
    request.onsuccess = () => resolve(request.result)
    request.onerror = () => reject(request.error ?? new Error('本地草稿库打开失败'))
  })
  return databasePromise
}

export async function readDraftDocuments(): Promise<StoredOutboundDocument[]> {
  const database = await openDatabase()
  return requestResult(database.transaction(storeName, 'readonly').objectStore(storeName).getAll())
}

export async function readDraftDocument(id: string): Promise<StoredOutboundDocument | null> {
  const database = await openDatabase()
  return (await requestResult(
    database.transaction(storeName, 'readonly').objectStore(storeName).get(id),
  )) ?? null
}

export async function writeDraftDocument(document: StoredOutboundDocument): Promise<void> {
  const database = await openDatabase()
  const transaction = database.transaction(storeName, 'readwrite')
  transaction.objectStore(storeName).put(document)
  await transactionDone(transaction)
}

export async function writeDraftDocuments(documents: StoredOutboundDocument[]): Promise<void> {
  if (!documents.length) return
  const database = await openDatabase()
  const transaction = database.transaction(storeName, 'readwrite')
  const store = transaction.objectStore(storeName)
  for (const document of documents) store.put(document)
  await transactionDone(transaction)
}

export async function removeDraftDocument(id: string): Promise<void> {
  const database = await openDatabase()
  const transaction = database.transaction(storeName, 'readwrite')
  transaction.objectStore(storeName).delete(id)
  await transactionDone(transaction)
}
