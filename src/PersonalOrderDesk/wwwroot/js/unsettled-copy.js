(() => {
  const button = document.getElementById('copy-unsettled');
  const input = document.getElementById('unsettled-text');
  const status = document.getElementById('copy-unsettled-status');
  if (!button || !input || !status) return;
  button.disabled = !input.value;
  button.addEventListener('click', async () => {
    let copied = false;
    try { await navigator.clipboard.writeText(input.value); copied = true; } catch { }
    if (!copied) {
      document.getElementById('unsettled-preview').open = true;
      input.focus(); input.select(); input.setSelectionRange(0, input.value.length);
      try { copied = document.execCommand('copy'); } catch { }
    }
    status.className = copied ? 'small mb-3 text-success' : 'small mb-3 text-danger';
    status.textContent = copied ? `已复制 ${input.dataset.count} 个编号的未结款清单。` : '清单已选中，请长按复制或按 Ctrl+C。';
  });
})();
