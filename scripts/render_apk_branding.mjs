import { createRequire } from 'node:module'
import { mkdir, readFile } from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const require = createRequire(import.meta.url)
const sharp = process.env.LABEL_SYSTEM_NODE_MODULES
  ? createRequire(path.join(process.env.LABEL_SYSTEM_NODE_MODULES, 'package.json'))('sharp')
  : require('sharp')
const workspace = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const icon = await readFile(path.join(workspace, 'android-bridge/branding/app-icon.svg'))
const resourceDir = path.join(workspace, 'label_system/miniapp/android/nativeResources/res')
for (const [density, size] of [['mdpi', 48], ['hdpi', 72], ['xhdpi', 96], ['xxhdpi', 144], ['xxxhdpi', 192]]) {
  const target = path.join(resourceDir, `mipmap-${density}`)
  await mkdir(target, { recursive: true })
  await sharp(icon).resize(size, size).png().toFile(path.join(target, 'ic_launcher.png'))
}
await sharp(icon).resize(512, 512).png().toFile(path.join(workspace, 'android-bridge/branding/preview.png'))
await sharp({ create: { width: 1, height: 1, channels: 4, background: { r: 0, g: 0, b: 0, alpha: 0 } } })
  .png().toFile(path.join(workspace, 'android-bridge/branding/transparent.png'))
console.log('Branding PNG assets generated from app-icon.svg (5 Android densities + preview).')
