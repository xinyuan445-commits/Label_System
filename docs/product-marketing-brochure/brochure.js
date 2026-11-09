(() => {
  'use strict';
  const root = document.documentElement;
  const storageKey = 'u8-product-marketing-language';
  const languageButtons = [...document.querySelectorAll('.language button')];
  const dialog = document.getElementById('image-dialog');
  const enlarged = document.getElementById('enlarged-image');
  const caption = document.getElementById('image-caption');
  const original = document.getElementById('original-image');
  let activeImage = null;

  function refreshCaption() {
    if (!activeImage) return;
    const text = activeImage.getAttribute(`data-caption-${root.dataset.language}`);
    caption.textContent = text;
    enlarged.alt = text;
  }

  function setLanguage(language) {
    const lang = language === 'en' ? 'en' : 'zh';
    root.dataset.language = lang;
    root.lang = lang === 'en' ? 'en' : 'zh-CN';
    document.title = lang === 'en'
      ? 'Bring U8 into Your Warehouse | Scanning & Label Printing'
      : '让 U8 管理延伸到仓库现场｜仓库扫码与条码打印';
    document.querySelector('meta[name="description"]').content = lang === 'en'
      ? 'Warehouse scanning and barcode label printing for manufacturers using Yonyou U8. Connect material labels, on-site work, and business documents.'
      : '面向用友 U8 制造企业的仓库扫码与条码打印方案。连接物料标签、现场作业与业务单据。';
    languageButtons.forEach(button => button.setAttribute('aria-pressed', String(button.dataset.language === lang)));
    document.querySelectorAll('[data-alt-zh]').forEach(el => el.alt = el.getAttribute(`data-alt-${lang}`));
    document.querySelectorAll('[data-aria-zh]').forEach(el => el.setAttribute('aria-label', el.getAttribute(`data-aria-${lang}`)));
    refreshCaption();
    try { localStorage.setItem(storageKey, lang); } catch { /* File previews may disable storage. */ }
  }
  languageButtons.forEach(button => button.addEventListener('click', () => setLanguage(button.dataset.language)));
  let savedLanguage = 'zh';
  try { savedLanguage = localStorage.getItem(storageKey) || 'zh'; } catch { /* Chinese remains the default. */ }
  setLanguage(savedLanguage);

  // Print every capability, then restore the reader's disclosure state.
  let printDetails = null;
  let printImageLinks = null;
  addEventListener('beforeprint', () => {
    if (!printDetails) printDetails = [...document.querySelectorAll('details')].map(el => [el, el.open]);
    printDetails.forEach(([el]) => el.open = true);
    if (!printImageLinks) printImageLinks = [...document.querySelectorAll('a.zoom')].map(el => [el, el.getAttribute('href')]);
    printImageLinks.forEach(([el]) => el.removeAttribute('href'));
  });
  addEventListener('afterprint', () => {
    if (printDetails) printDetails.forEach(([el, open]) => el.open = open);
    printDetails = null;
    if (printImageLinks) printImageLinks.forEach(([el, href]) => el.setAttribute('href', href));
    printImageLinks = null;
  });

  document.querySelectorAll('a.zoom').forEach(link => {
    link.addEventListener('click', event => {
      if (event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
      if (typeof dialog.showModal !== 'function') return;
      event.preventDefault();
      activeImage = link;
      enlarged.src = link.href;
      original.href = link.href;
      refreshCaption();
      dialog.showModal();
      document.body.style.overflow = 'hidden';
    });
  });
  document.getElementById('close-image').addEventListener('click', () => dialog.close());
  dialog.addEventListener('click', event => {
    const r = dialog.getBoundingClientRect();
    if (event.target === dialog && (event.clientX < r.left || event.clientX > r.right || event.clientY < r.top || event.clientY > r.bottom)) dialog.close();
  });
  dialog.addEventListener('close', () => {
    document.body.style.overflow = '';
    if (activeImage) activeImage.focus({preventScroll: true});
  });

  // Appendix links reveal the reference before native anchor navigation.
  function revealAnchor(hash) {
    let id;
    try { id = decodeURIComponent(hash.replace(/^#/, '')); } catch { return null; }
    const target = document.getElementById(id);
    if (!target) return null;
    if (id === 'capabilities') document.getElementById('capability-appendix').open = true;
    for (let node = target; node; node = node.parentElement) {
      if (node.tagName === 'DETAILS') node.open = true;
    }
    return target;
  }
  document.querySelectorAll('a[href^="#"]').forEach(link => {
    link.addEventListener('click', event => {
      if (event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
      revealAnchor(link.hash);
    });
  });
  function revealLocation() {
    const target = revealAnchor(location.hash);
    if (target && target.closest('#capabilities')) {
      requestAnimationFrame(() => target.scrollIntoView({block: 'start'}));
    }
  }
  addEventListener('hashchange', revealLocation);
  revealLocation();

  const progress = document.querySelector('.read-progress');
  function updateProgress() {
    const total = document.documentElement.scrollHeight - innerHeight;
    progress.style.width = `${total > 0 ? Math.min(100, Math.max(0, scrollY / total * 100)) : 0}%`;
  }
  addEventListener('scroll', updateProgress, {passive: true});
  addEventListener('resize', updateProgress);
  addEventListener('load', updateProgress);
  document.querySelectorAll('details').forEach(el => el.addEventListener('toggle', updateProgress));
  updateProgress();
})();
