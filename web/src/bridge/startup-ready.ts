/** Tell the local shell to remove its decoration only after the first route is painted. */
export function notifyStartupReady(): void {
  if (window.parent === window) return
  window.requestAnimationFrame(() => {
    window.requestAnimationFrame(() => {
      window.parent.postMessage(
        { source: 'label-system-h5', type: 'app-ready' },
        'http://127.0.0.1:18765',
      )
    })
  })
}
