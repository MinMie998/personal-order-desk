(() => {
  let opener;
  const commonMethods = ['微信', '支付宝', '银行卡', '现金'];
  const rememberMethod = dialog => {
    const select = dialog.querySelector('.payment-method');
    if (!select || select.value) return;
    try { const saved = localStorage.getItem('pod.commonPaymentMethod'); if (commonMethods.includes(saved)) select.value = saved; } catch { }
  };
  const open = (dialog, button) => {
    if (!dialog) return;
    opener = button;
    const type = button?.dataset.paymentType;
    if (type) {
      const refund = type === 'Refund';
      const typeInput = dialog.querySelector('[name="PaymentInput.Type"]');
      if (typeInput.value !== type) {
        dialog.querySelector('form').reset();
        dialog.querySelector('[data-entry-focus]').value = '';
        ['PaymentInput.PaymentMethod', 'PaymentInput.CustomPaymentMethod', 'PaymentInput.ExternalReference', 'PaymentInput.Notes'].forEach(name => {
          const field = dialog.querySelector(`[name="${name}"]`);
          if (field) field.value = '';
        });
        const custom = dialog.querySelector('.custom-payment-method');
        if (custom) custom.hidden = true;
      }
      typeInput.value = type;
      dialog.querySelector('#payment-editor-title').textContent = refund ? '记退款' : '记收款';
      dialog.querySelector('#payment-amount-label').textContent = refund ? '退款金额（元）' : '收款金额（元）';
      dialog.querySelector('#payment-save').textContent = refund ? '保存退款' : '保存收款';
    }
    rememberMethod(dialog);
    dialog.showModal();
    const amount = dialog.querySelector('[data-entry-focus]');
    if (amount) { amount.focus(); amount.select(); }
  };
  document.querySelectorAll('[data-dialog-target]').forEach(button => button.addEventListener('click', () => open(document.getElementById(button.dataset.dialogTarget), button)));
  document.querySelectorAll('.entry-dialog').forEach(dialog => {
    const paymentForm = dialog.querySelector('form');
    if (dialog.querySelector('.payment-method')) paymentForm?.addEventListener('submit', () => {
      const value = dialog.querySelector('.payment-method').value;
      try { if (commonMethods.includes(value)) localStorage.setItem('pod.commonPaymentMethod', value); else localStorage.removeItem('pod.commonPaymentMethod'); } catch { }
    });
    dialog.querySelectorAll('[data-close-dialog]').forEach(button => button.addEventListener('click', () => dialog.close()));
    dialog.addEventListener('close', () => opener?.focus());
    if (dialog.dataset.reopen === 'true') open(dialog);
  });
})();
