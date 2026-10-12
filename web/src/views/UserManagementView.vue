<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import {
  createUser, fetchUserAudit, fetchUserPermissions, fetchUsers, resetUserPassword,
  resetUserPreferences, revokeUserSessions, unlockUser, updateUser, updateUserPermission,
  type UserAdminAuditItem, type UserFunctionPermission, type UserInput, type UserItem,
} from '@/services/users'
import { fetchRdStyles, type RdStyleOption } from '@/services/reference-data'

const router = useRouter()
const users = ref<UserItem[]>([])
const loading = ref(true)
const saving = ref(false)
const error = ref('')
const message = ref('')
const editing = ref<UserItem | null | undefined>(undefined)
const password = ref('')
const permissions = ref<UserFunctionPermission[] | null>(null)
const permissionUser = ref<UserItem | null>(null)
const auditUser = ref<UserItem | null>(null)
const auditItems = ref<UserAdminAuditItem[] | null>(null)
const scopePermission = ref<UserFunctionPermission | null>(null)
const scopeStyles = ref<RdStyleOption[]>([])
const scopeMode = ref<'ALL' | 'SELECTED'>('ALL')
const scopeCodes = ref<string[]>([])
const scopeSearch = ref('')
const search = ref('')
const status = ref<'all' | 'enabled' | 'disabled' | 'locked'>('all')
const form = reactive<UserInput>({ id: '', loginName: '', name: '', erpOperatorCode: null, erpOperatorName: null, isAdmin: false, isEnabled: true, password: '' })

const filteredUsers = computed(() => users.value.filter((user) => {
  const keyword = search.value.trim().toLowerCase()
  const matched = !keyword || [user.name, user.loginName, user.erpOperatorCode, user.erpOperatorName]
    .some((value) => value?.toLowerCase().includes(keyword))
  const locked = isLocked(user)
  const statusMatched = status.value === 'all' ||
    (status.value === 'enabled' && user.isEnabled) ||
    (status.value === 'disabled' && !user.isEnabled) ||
    (status.value === 'locked' && locked)
  return matched && statusMatched
}))

const permissionGroups = computed(() => {
  const groups = new Map<string, { name: string, items: UserFunctionPermission[] }>()
  for (const item of permissions.value ?? []) {
    const group = groups.get(item.groupCode) ?? { name: item.groupName, items: [] }
    group.items.push(item); groups.set(item.groupCode, group)
  }
  return [...groups.entries()].map(([code, group]) => ({ code, ...group }))
})

const categoryScopeDirections: Record<string, 'IN' | 'OUT' | undefined> = {
  'purchase-arrival': 'IN',
  'outsourcing-arrival': 'IN',
  'purchase-inbound': 'IN',
  'delivery-note': 'OUT',
  'sales-outbound': 'OUT',
  'material-outbound': 'OUT',
  'transfer-order': undefined,
  'inventory-count': undefined,
}
const filteredScopeStyles = computed(() => {
  const keyword = scopeSearch.value.trim().toLocaleLowerCase()
  return keyword
    ? scopeStyles.value.filter((item) =>
        item.code.toLocaleLowerCase().includes(keyword) ||
        item.name.toLocaleLowerCase().includes(keyword))
    : scopeStyles.value
})
const scopeAllVisibleSelected = computed(() =>
  filteredScopeStyles.value.length > 0 &&
  filteredScopeStyles.value.every((item) => scopeCodes.value.includes(item.code)),
)

async function load(): Promise<void> {
  loading.value = true; error.value = ''
  try { users.value = await fetchUsers() }
  catch (reason) { error.value = reason instanceof Error ? reason.message : '读取失败' }
  finally { loading.value = false }
}

function openCreate(): void {
  editing.value = null
  Object.assign(form, { id: '', loginName: '', name: '', erpOperatorCode: null, erpOperatorName: null, isAdmin: false, isEnabled: true, password: '' })
  password.value = ''; error.value = ''
}

function openEdit(user: UserItem): void {
  editing.value = user
  Object.assign(form, { id: user.id, loginName: user.loginName, name: user.name, erpOperatorCode: user.erpOperatorCode, erpOperatorName: user.erpOperatorName, isAdmin: user.isAdmin, isEnabled: user.isEnabled, password: '' })
  password.value = ''; error.value = ''
}

function closeEditor(): void { editing.value = undefined; error.value = ''; password.value = '' }

function normalizeForm(): UserInput {
  const erpCode = form.erpOperatorCode?.trim() || null
  const erpName = form.erpOperatorName?.trim() || null
  if (!form.loginName.trim() || !form.name.trim()) throw new Error('请填写账号和名称')
  if ((erpCode === null) !== (erpName === null)) throw new Error('ERP账号和名称需同时填写')
  return { ...form, id: form.id.trim() || form.loginName.trim(), loginName: form.loginName.trim(), name: form.name.trim(), erpOperatorCode: erpCode, erpOperatorName: erpName, password: form.password?.trim() }
}

async function save(): Promise<void> {
  error.value = ''; saving.value = true
  try {
    const input = normalizeForm()
    if (editing.value === null && !input.password) throw new Error('请设置密码')
    if (editing.value) await updateUser(editing.value, input)
    else await createUser(input)
    closeEditor(); await load(); notify('已保存')
  } catch (reason) { error.value = reason instanceof Error ? reason.message : '保存失败' }
  finally { saving.value = false }
}

async function resetPassword(): Promise<void> {
  if (!editing.value) return
  error.value = ''
  if (!password.value.trim()) { error.value = '请输入新密码'; return }
  saving.value = true
  try { await resetUserPassword(editing.value.id, password.value.trim()); password.value = ''; notify('密码已更新') }
  catch (reason) { error.value = reason instanceof Error ? reason.message : '更新失败' }
  finally { saving.value = false }
}

async function openPermissions(user: UserItem): Promise<void> {
  error.value = ''; permissionUser.value = user; permissions.value = null
  try { permissions.value = await fetchUserPermissions(user.id) }
  catch (reason) { permissionUser.value = null; error.value = reason instanceof Error ? reason.message : '读取失败' }
}

async function changePermission(item: UserFunctionPermission): Promise<void> {
  if (!permissionUser.value) return
  error.value = ''
  try {
    await updateUserPermission(permissionUser.value.id, {
      ...item,
      dataScope: item.state === 'INHERIT' ? null : item.dataScope,
    })
    permissions.value = await fetchUserPermissions(permissionUser.value.id)
  }
  catch (reason) { error.value = reason instanceof Error ? reason.message : '保存失败' }
}

async function changePermissionGroup(items: UserFunctionPermission[], state: UserFunctionPermission['state']): Promise<void> {
  if (!permissionUser.value) return
  error.value = ''; saving.value = true
  try {
    for (const item of items.filter((entry) =>
      entry.state !== state || (state === 'INHERIT' && entry.dataScope !== null)))
      await updateUserPermission(permissionUser.value.id, {
        ...item,
        state,
        dataScope: state === 'INHERIT' ? null : item.dataScope,
      })
    permissions.value = await fetchUserPermissions(permissionUser.value.id)
    notify('已保存')
  } catch (reason) { error.value = reason instanceof Error ? reason.message : '保存失败' }
  finally { saving.value = false }
}

function hasCategoryScope(item: UserFunctionPermission): boolean {
  return Object.prototype.hasOwnProperty.call(categoryScopeDirections, item.functionCode)
}

function readRdStyleScope(dataScope: unknown): { mode: 'ALL' | 'SELECTED', codes: string[] } {
  if (!dataScope || typeof dataScope !== 'object') return { mode: 'ALL', codes: [] }
  const rdStyles = (dataScope as { rdStyles?: unknown }).rdStyles
  if (!rdStyles || typeof rdStyles !== 'object') return { mode: 'ALL', codes: [] }
  const value = rdStyles as { mode?: unknown, codes?: unknown }
  if (value.mode !== 'SELECTED' || !Array.isArray(value.codes)) return { mode: 'ALL', codes: [] }
  return {
    mode: 'SELECTED',
    codes: value.codes.filter((code): code is string => typeof code === 'string'),
  }
}

function scopeLabel(item: UserFunctionPermission): string {
  const scope = readRdStyleScope(item.dataScope)
  return scope.mode === 'ALL' ? '类别：全部' : `类别：${scope.codes.length}项`
}

async function openCategoryScope(item: UserFunctionPermission): Promise<void> {
  scopePermission.value = item
  scopeStyles.value = []
  scopeSearch.value = ''
  const current = readRdStyleScope(item.dataScope)
  scopeMode.value = current.mode
  scopeCodes.value = [...current.codes]
  error.value = ''
  try {
    scopeStyles.value = await fetchRdStyles(categoryScopeDirections[item.functionCode])
  } catch (reason) {
    scopePermission.value = null
    error.value = reason instanceof Error ? reason.message : '读取失败'
  }
}

function toggleScopeCode(code: string): void {
  scopeCodes.value = scopeCodes.value.includes(code)
    ? scopeCodes.value.filter((item) => item !== code)
    : [...scopeCodes.value, code]
}

function toggleVisibleScopeCodes(): void {
  const visible = filteredScopeStyles.value.map((item) => item.code)
  if (scopeAllVisibleSelected.value) {
    scopeCodes.value = scopeCodes.value.filter((code) => !visible.includes(code))
  } else {
    scopeCodes.value = [...new Set([...scopeCodes.value, ...visible])]
  }
}

function withRdStyleScope(dataScope: unknown): unknown {
  const existing = dataScope && typeof dataScope === 'object' && !Array.isArray(dataScope)
    ? { ...(dataScope as Record<string, unknown>) }
    : {}
  if (scopeMode.value === 'ALL') {
    delete existing.rdStyles
    if (Object.keys(existing).length === 1 && existing.version === 1) delete existing.version
    return Object.keys(existing).length ? existing : null
  }
  return {
    ...existing,
    version: 1,
    rdStyles: { mode: 'SELECTED', codes: [...scopeCodes.value].sort() },
  }
}

async function saveCategoryScope(): Promise<void> {
  if (!permissionUser.value || !scopePermission.value) return
  if (scopeMode.value === 'SELECTED' && !scopeCodes.value.length) {
    error.value = '请至少选择一个类别'
    return
  }
  saving.value = true
  error.value = ''
  try {
    await updateUserPermission(permissionUser.value.id, {
      ...scopePermission.value,
      state: scopeMode.value === 'SELECTED' && scopePermission.value.state === 'INHERIT'
        ? 'ALLOW'
        : scopePermission.value.state,
      dataScope: withRdStyleScope(scopePermission.value.dataScope),
    })
    permissions.value = await fetchUserPermissions(permissionUser.value.id)
    scopePermission.value = null
    notify('已保存')
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : '保存失败'
  } finally {
    saving.value = false
  }
}

function isLocked(user: UserItem): boolean {
  return !!user.lockedUntil && Date.parse(user.lockedUntil) > Date.now()
}

async function runSecurityAction(action: 'unlock' | 'sessions' | 'preferences'): Promise<void> {
  if (!editing.value) return
  const prompts = { unlock: '确认解锁该用户？', sessions: '确认退出该用户的全部会话？', preferences: '确认重置该用户的漫游数据？' }
  if (!window.confirm(prompts[action])) return
  saving.value = true; error.value = ''
  try {
    if (action === 'unlock') await unlockUser(editing.value.id)
    if (action === 'sessions') await revokeUserSessions(editing.value.id)
    if (action === 'preferences') await resetUserPreferences(editing.value.id)
    await load(); editing.value = users.value.find((user) => user.id === editing.value?.id) ?? editing.value
    notify(action === 'unlock' ? '已解锁' : action === 'sessions' ? '会话已退出' : '漫游数据已重置')
  } catch (reason) { error.value = reason instanceof Error ? reason.message : '操作失败' }
  finally { saving.value = false }
}

async function openAudit(user: UserItem): Promise<void> {
  auditUser.value = user; auditItems.value = null; error.value = ''
  try { auditItems.value = await fetchUserAudit(user.id) }
  catch (reason) { auditUser.value = null; error.value = reason instanceof Error ? reason.message : '读取失败' }
}

function auditLabel(action: string): string {
  return ({ USER_CREATED: '新增用户', USER_UPDATED: '修改用户', PASSWORD_RESET: '重置密码', ACCOUNT_UNLOCKED: '解锁账号', SESSIONS_REVOKED: '退出会话', PREFERENCES_RESET: '重置漫游数据', PERMISSION_UPDATED: '修改权限' } as Record<string, string>)[action] ?? action
}

function formatTime(value: string | null): string {
  return value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '—'
}

function notify(text: string): void {
  message.value = text
  window.setTimeout(() => { if (message.value === text) message.value = '' }, 1800)
}

onMounted(load)
</script>

<template>
  <main class="users-page">
    <header class="topbar-shell"><div class="topbar">
      <button class="back-button" type="button" aria-label="返回" @click="router.back()"><svg viewBox="0 0 24 24"><path d="m15 18-6-6 6-6" /></svg></button>
      <h1>用户管理</h1><button class="add-button" type="button" @click="openCreate">新增</button>
    </div></header>

    <div class="page-content">
      <p v-if="error && editing === undefined && !permissionUser" class="error-message">{{ error }}</p>
      <div class="filters"><input v-model="search" type="search" placeholder="搜索用户" /><select v-model="status"><option value="all">全部</option><option value="enabled">启用</option><option value="disabled">停用</option><option value="locked">锁定</option></select></div>
      <section v-if="loading" class="empty-state">读取中</section>
      <section v-else class="user-list">
        <article v-for="user in filteredUsers" :key="user.id" class="user-card" @click="openEdit(user)">
          <div><strong>{{ user.name }}</strong><span>{{ user.loginName }}</span></div>
          <div class="user-meta"><span v-if="user.isAdmin">管理员</span><span v-if="isLocked(user)" class="locked">锁定</span><span :class="{ missing: !user.erpOperatorName }">{{ user.erpOperatorName || 'ERP未配置' }}</span><i :class="{ disabled: !user.isEnabled }">{{ user.isEnabled ? '启用' : '停用' }}</i></div>
        </article>
        <section v-if="!filteredUsers.length" class="empty-state">暂无用户</section>
      </section>
    </div>

    <div v-if="editing !== undefined" class="mask" @click.self="closeEditor"><section class="sheet editor-sheet">
      <header><h2>{{ editing ? '编辑用户' : '新增用户' }}</h2><button type="button" @click="closeEditor">×</button></header>
      <div class="form-grid">
        <label><span>登录账号</span><input v-model="form.loginName" :disabled="!!editing" autocomplete="off" maxlength="100" /></label>
        <label><span>名称</span><input v-model="form.name" maxlength="100" /></label>
        <label><span>ERP账号</span><input v-model="form.erpOperatorCode" maxlength="100" /></label>
        <label><span>ERP名称</span><input v-model="form.erpOperatorName" maxlength="100" /></label>
        <label v-if="!editing" class="wide"><span>密码</span><input v-model="form.password" type="password" autocomplete="new-password" maxlength="128" /></label>
      </div>
      <div class="switch-row"><label><input v-model="form.isAdmin" type="checkbox" />管理员</label><label><input v-model="form.isEnabled" type="checkbox" />启用</label></div>
      <template v-if="editing">
        <div v-if="editing.authSource === 'LOCAL'" class="inline-action"><input v-model="password" type="password" autocomplete="new-password" placeholder="新密码" maxlength="128" /><button type="button" :disabled="saving" @click="resetPassword">重置密码</button></div>
        <div class="user-time"><span>最近登录</span><b>{{ formatTime(editing.lastLoginAt) }}</b></div>
        <div class="action-grid"><button type="button" @click="openPermissions(editing)">功能权限</button><button type="button" @click="openAudit(editing)">操作记录</button><button type="button" :disabled="saving || !isLocked(editing)" @click="runSecurityAction('unlock')">解锁账号</button><button type="button" :disabled="saving" @click="runSecurityAction('sessions')">退出会话</button><button class="wide" type="button" :disabled="saving" @click="runSecurityAction('preferences')">重置漫游数据</button></div>
      </template>
      <p v-if="error" class="error-message">{{ error }}</p>
      <footer><button type="button" @click="closeEditor">取消</button><button class="primary" type="button" :disabled="saving" @click="save">{{ saving ? '保存中' : '保存' }}</button></footer>
    </section></div>

    <div v-if="permissionUser" class="mask" @click.self="permissionUser = null"><section class="sheet permission-sheet">
      <header><div><h2>功能权限</h2><span>{{ permissionUser.name }}</span></div><button type="button" @click="permissionUser = null">×</button></header>
      <div v-if="permissions === null" class="permission-loading">读取中</div>
      <div v-else class="permission-list"><section v-for="group in permissionGroups" :key="group.code" class="permission-group"><header><strong>{{ group.name }}</strong><div><button type="button" :disabled="saving" @click="changePermissionGroup(group.items, 'INHERIT')">默认</button><button type="button" :disabled="saving" @click="changePermissionGroup(group.items, 'ALLOW')">允许</button><button type="button" :disabled="saving" @click="changePermissionGroup(group.items, 'DENY')">禁止</button></div></header><label v-for="item in group.items" :key="item.functionCode"><span>{{ item.functionName }}<i :class="{ effective: item.isEffective }">{{ item.isEffective ? '已开放' : '未开放' }}</i></span><div class="permission-actions"><button v-if="hasCategoryScope(item)" type="button" :disabled="saving" @click="openCategoryScope(item)">{{ scopeLabel(item) }}</button><select v-model="item.state" :disabled="saving" @change="changePermission(item)"><option value="INHERIT">默认</option><option value="ALLOW">允许</option><option value="DENY">禁止</option></select></div></label></section></div>
      <p v-if="error" class="error-message">{{ error }}</p>
    </section></div>

    <div v-if="scopePermission" class="mask scope-mask" @click.self="scopePermission = null"><section class="sheet scope-sheet">
      <header><div><h2>业务类别</h2><span>{{ scopePermission.functionName }}</span></div><button type="button" @click="scopePermission = null">×</button></header>
      <div class="scope-mode"><button type="button" :class="{ active: scopeMode === 'ALL' }" @click="scopeMode = 'ALL'">全部</button><button type="button" :class="{ active: scopeMode === 'SELECTED' }" @click="scopeMode = 'SELECTED'">指定</button></div>
      <template v-if="scopeMode === 'SELECTED'">
        <div class="scope-search"><input v-model="scopeSearch" type="search" placeholder="编号或名称" /><button type="button" @click="toggleVisibleScopeCodes">{{ scopeAllVisibleSelected ? '取消' : '全选' }}</button></div>
        <div class="scope-options"><label v-for="item in filteredScopeStyles" :key="item.code"><input type="checkbox" :checked="scopeCodes.includes(item.code)" @change="toggleScopeCode(item.code)" /><strong>{{ item.code }}</strong><span>{{ item.name }}</span></label><div v-if="!filteredScopeStyles.length" class="permission-loading">未找到</div></div>
      </template>
      <p v-if="error" class="error-message">{{ error }}</p>
      <footer><button type="button" @click="scopePermission = null">取消</button><button class="primary" type="button" :disabled="saving" @click="saveCategoryScope">{{ saving ? '保存中' : '保存' }}</button></footer>
    </section></div>

    <div v-if="auditUser" class="mask" @click.self="auditUser = null"><section class="sheet audit-sheet">
      <header><div><h2>操作记录</h2><span>{{ auditUser.name }}</span></div><button type="button" @click="auditUser = null">×</button></header>
      <div v-if="auditItems === null" class="permission-loading">读取中</div>
      <div v-else class="audit-list"><article v-for="item in auditItems" :key="item.id"><div><strong>{{ auditLabel(item.action) }}</strong><span>{{ item.actorName }}</span></div><time>{{ formatTime(item.createdAt) }}</time></article><div v-if="!auditItems.length" class="permission-loading">暂无记录</div></div>
    </section></div>

    <Transition name="toast"><div v-if="message" class="toast">{{ message }}</div></Transition>
  </main>
</template>

<style scoped>
.users-page{min-height:100dvh;padding-top:var(--app-header-height);background:#f3f6fa;color:#21324a}.topbar-shell{position:fixed;z-index:20;top:0;right:0;left:0;height:var(--app-header-height);border-bottom:1px solid #e4ebf3;background:#fff}.topbar,.page-content{width:min(100%,650px);margin-inline:auto}.topbar{height:100%;display:flex;align-items:center;gap:12px;padding:var(--app-safe-top) 18px 0}.topbar h1{flex:1;color:#17365d;font-size:21px}.back-button{width:42px;height:42px;display:grid;place-items:center;border:0;border-radius:12px;background:#f3f6fa;color:#365678}.back-button svg{width:23px;height:23px;fill:none;stroke:currentColor;stroke-width:2}.add-button{height:38px;padding:0 14px;border:0;border-radius:10px;background:#2368c4;color:#fff;font-weight:750}.page-content{padding:15px 14px}.filters{display:grid;grid-template-columns:1fr 90px;gap:8px;margin-bottom:12px}.filters input,.filters select{height:42px;border:1px solid #d3deea;border-radius:11px;padding:0 11px;background:#fff;font:inherit;color:#334b67}.empty-state{min-height:150px;display:grid;place-items:center;border:1px dashed #cbd7e4;border-radius:16px;background:#fff;color:#8b98a8}.user-list{display:grid;gap:10px}.user-card{display:flex;align-items:center;justify-content:space-between;gap:12px;padding:15px;border:1px solid rgba(23,54,93,.08);border-radius:15px;background:#fff;box-shadow:0 7px 20px rgba(23,54,93,.05);cursor:pointer}.user-card>div:first-child{min-width:0;display:grid;gap:4px}.user-card strong{color:#263c59;font-size:15px}.user-card span{color:#7b8999;font-size:12px}.user-meta{display:flex;flex-wrap:wrap;justify-content:flex-end;gap:5px}.user-meta span,.user-meta i{padding:4px 7px;border-radius:7px;background:#eef4fa;color:#47627f;font-size:11px;font-style:normal}.user-meta .missing{background:#fff3e5;color:#a86724}.user-meta .locked{background:#fdecec;color:#a83a3a}.user-meta i{background:#e9f6ef;color:#1c7654}.user-meta i.disabled{background:#f1f2f4;color:#8993a0}.mask{position:fixed;z-index:50;inset:0;display:flex;align-items:flex-end;justify-content:center;padding:14px;background:rgba(20,32,48,.42)}.sheet{width:min(100%,620px);max-height:calc(100dvh - 28px);overflow:auto;padding:18px;border-radius:18px;background:#fff;box-shadow:0 18px 44px rgba(15,29,48,.24)}.sheet header{display:flex;align-items:center;justify-content:space-between;gap:12px;margin-bottom:16px}.sheet h2{color:#203552;font-size:18px}.sheet header>button{width:36px;height:36px;border:0;border-radius:10px;background:#f1f4f8;color:#617187;font-size:22px}.sheet header div{display:grid;gap:3px}.sheet header span{color:#7c8999;font-size:12px}.form-grid{display:grid;grid-template-columns:1fr 1fr;gap:11px}.form-grid label{display:grid;gap:6px}.form-grid label.wide{grid-column:1/-1}.form-grid span{color:#5d6f84;font-size:12px;font-weight:700}.form-grid input,.inline-action input,.permission-list select{height:42px;border:1px solid #d3deea;border-radius:10px;padding:0 10px;font:inherit;outline:none}.form-grid input:focus,.inline-action input:focus,.permission-list select:focus{border-color:#3378cf}.form-grid input:disabled{background:#f2f4f7;color:#8a94a2}.switch-row{display:flex;gap:22px;margin:16px 2px}.switch-row label{display:flex;align-items:center;gap:7px;color:#42566d;font-size:13px}.switch-row input{width:18px;height:18px}.inline-action{display:grid;grid-template-columns:1fr 100px;gap:8px;margin-top:12px}.inline-action button,.action-grid button,.permission-group header button{border:1px solid #c8d7e6;border-radius:10px;background:#f7fafc;color:#375a7d;font-weight:700}.user-time{display:flex;justify-content:space-between;margin:13px 2px 8px;color:#7c8999;font-size:12px}.user-time b{color:#4b6179;font-weight:600}.action-grid{display:grid;grid-template-columns:1fr 1fr;gap:8px}.action-grid button{min-height:40px}.action-grid .wide{grid-column:1/-1}.action-grid button:disabled{opacity:.45}.sheet footer{display:grid;grid-template-columns:1fr 1.5fr;gap:9px;margin-top:16px}.sheet footer button{height:44px;border:1px solid #ccd8e6;border-radius:10px;background:#fff;color:#50657e;font-weight:750}.sheet footer .primary{border-color:#2368c4;background:#2368c4;color:#fff}.error-message{margin:10px 2px 0;color:#b43d3d;font-size:13px}.permission-list{display:grid;gap:14px}.permission-group{overflow:hidden;border:1px solid #e2e9f1;border-radius:12px}.permission-group>header{min-height:46px;margin:0;padding:7px 9px 7px 12px;background:#f5f8fb}.permission-group header strong{color:#334b67;font-size:13px}.permission-group header div{display:flex;grid-auto-flow:column;gap:5px}.permission-group header button{height:30px;padding:0 8px;border-radius:7px;font-size:11px}.permission-list label{display:flex;align-items:center;justify-content:space-between;gap:14px;min-height:51px;padding:6px 10px;border-top:1px solid #edf1f5}.permission-list label>span{display:flex;align-items:center;gap:7px;color:#344960;font-size:13px}.permission-list label i{padding:2px 5px;border-radius:5px;background:#f1f2f4;color:#8b95a2;font-size:10px;font-style:normal}.permission-list label i.effective{background:#e9f6ef;color:#1c7654}.permission-actions{display:flex;align-items:center;justify-content:flex-end;gap:6px}.permission-actions>button{min-height:32px;padding:0 8px;border:1px solid #c8d7e6;border-radius:8px;background:#f7fafc;color:#375a7d;font-size:11px;font-weight:700}.permission-list select{width:92px;height:36px}.permission-loading{min-height:100px;display:grid;place-items:center;color:#8491a2}.scope-mask{z-index:60}.scope-mode{display:grid;grid-template-columns:1fr 1fr;gap:8px;margin-bottom:12px}.scope-mode button{height:40px;border:1px solid #ccd8e6;border-radius:10px;background:#fff;color:#50657e;font-weight:700}.scope-mode button.active{border-color:#2368c4;background:#edf4fd;color:#2368c4}.scope-search{display:grid;grid-template-columns:1fr 64px;gap:7px;margin-bottom:9px}.scope-search input{height:40px;padding:0 10px;border:1px solid #d3deea;border-radius:9px;font:inherit;outline:none}.scope-search button{border:0;border-radius:9px;background:#edf4fd;color:#2368c4;font-weight:700}.scope-options{max-height:44dvh;overflow:auto;border:1px solid #e2e9f1;border-radius:11px}.scope-options label{min-height:44px;display:grid;grid-template-columns:20px auto minmax(0,1fr);align-items:center;gap:8px;padding:6px 10px;border-bottom:1px solid #edf1f5}.scope-options label:last-of-type{border-bottom:0}.scope-options input{width:17px;height:17px}.scope-options strong{color:#304861;font:700 12px ui-monospace,SFMono-Regular,Consolas,monospace}.scope-options span{overflow:hidden;color:#65768a;font-size:12px;text-overflow:ellipsis;white-space:nowrap}.audit-list{display:grid}.audit-list article{display:flex;align-items:center;justify-content:space-between;gap:10px;min-height:55px;border-bottom:1px solid #edf1f5}.audit-list article>div{display:grid;gap:3px}.audit-list strong{color:#344960;font-size:13px}.audit-list span,.audit-list time{color:#8290a1;font-size:11px}.toast{position:fixed;z-index:80;top:calc(var(--app-header-height) + 10px);left:50%;padding:10px 16px;border-radius:10px;background:#197052;color:#fff;font-size:13px;font-weight:700;transform:translateX(-50%)}.toast-enter-active,.toast-leave-active{transition:opacity .16s}.toast-enter-from,.toast-leave-to{opacity:0}@media(max-width:480px){.topbar{padding-inline:14px}.page-content{padding-inline:12px}.form-grid{grid-template-columns:1fr}.form-grid label.wide{grid-column:auto}.permission-list label{align-items:flex-start;flex-direction:column}.permission-actions{width:100%}.permission-actions>button{margin-right:auto}}
</style>
