export type WorkspaceIcon =
  | 'receive'
  | 'truck'
  | 'warehouse'
  | 'document'
  | 'dispatch'
  | 'material'
  | 'transfer'
  | 'location'
  | 'inventory'
  | 'approve'
  | 'query'

export interface WorkspaceEntry {
  code: string
  label: string
  icon: WorkspaceIcon
  approval?: boolean
}

export interface WorkspaceGroup {
  key: string
  title: string
  tone: 'blue' | 'green' | 'orange' | 'purple' | 'cyan'
  entries: WorkspaceEntry[]
}

const businessGroups: WorkspaceGroup[] = [
  {
    key: 'purchase',
    title: '采购管理',
    tone: 'blue',
    entries: [
      { code: 'purchase-arrival', label: '采购到货', icon: 'receive' },
      { code: 'outsourcing-arrival', label: '委外到货', icon: 'truck' },
      { code: 'purchase-inbound', label: '采购入库', icon: 'warehouse' },
    ],
  },
  {
    key: 'sales',
    title: '销售管理',
    tone: 'green',
    entries: [
      { code: 'delivery-note', label: '发货单', icon: 'document' },
      { code: 'sales-outbound', label: '销售出库单', icon: 'dispatch' },
    ],
  },
  {
    key: 'inventory',
    title: '库存管理',
    tone: 'orange',
    entries: [
      { code: 'material-outbound', label: '材料出库单', icon: 'material' },
      { code: 'transfer-order', label: '调拨单', icon: 'transfer' },
      { code: 'location-adjustment', label: '货位调整', icon: 'location' },
      { code: 'inventory-count', label: '盘点单', icon: 'inventory' },
    ],
  },
]

const approvalEntries: WorkspaceEntry[] = businessGroups.flatMap((group) =>
  group.entries.map((entry) => ({
    code: `approval-${entry.code}`,
    label: `${entry.label}审核`,
    icon: 'approve',
    approval: true,
  })),
)

export const workspaceGroups: WorkspaceGroup[] = [
  ...businessGroups,
  {
    key: 'approval',
    title: '审核管理',
    tone: 'purple',
    entries: approvalEntries,
  },
  {
    key: 'query',
    title: '信息查询',
    tone: 'cyan',
    entries: [{ code: 'label-query', label: '标签查询', icon: 'query' }],
  },
]

export const workspaceEntries = workspaceGroups.flatMap((group) => group.entries)

export function findWorkspaceEntry(code: string): WorkspaceEntry | undefined {
  return workspaceEntries.find((entry) => entry.code === code)
}
