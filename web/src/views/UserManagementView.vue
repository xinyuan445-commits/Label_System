<script setup lang="ts">
import { useRouter } from 'vue-router'

const router = useRouter()
</script>

<template>
  <main class="users-page">
    <header class="topbar-shell">
      <div class="topbar">
        <button class="back-button" type="button" aria-label="返回" @click="router.back()">
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="m15 18-6-6 6-6" /></svg>
        </button>
        <h1>用户管理</h1>
      </div>
    </header>

    <div class="page-content">
      <section class="empty-state">待接入</section>
    </div>
  </main>
</template>

<style scoped>
.users-page{min-height:100dvh;padding-top:72px;background:#f3f6fa;color:#21324a}.topbar-shell{position:fixed;z-index:20;top:0;right:0;left:0;height:72px;border-bottom:1px solid #e4ebf3;background:#fff;box-shadow:0 3px 12px rgba(23,54,93,.05)}.topbar,.page-content{width:min(100%,620px);margin-inline:auto}.topbar{height:100%;display:flex;align-items:center;gap:12px;padding:0 18px}.back-button{width:42px;height:42px;display:grid;place-items:center;border:0;border-radius:12px;background:#f3f6fa;color:#365678}.back-button svg{width:23px;height:23px;fill:none;stroke:currentColor;stroke-linecap:round;stroke-linejoin:round;stroke-width:2}.topbar h1{color:#17365d;font-size:22px}.page-content{padding:16px 14px}.empty-state{min-height:170px;display:grid;place-items:center;border:1px dashed #cbd7e4;border-radius:16px;background:rgba(255,255,255,.62);color:#8b98a8;font-size:14px}
</style>
