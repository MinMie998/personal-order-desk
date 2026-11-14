// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.
document.querySelectorAll('.payment-method').forEach(select => {
  const custom = select.parentElement.querySelector('.custom-payment-method');
  const update = () => { if (custom) custom.hidden = select.value !== '其他'; };
  select.addEventListener('change', update);
  update();
});

const receiptToast = document.querySelector(".receipt-toast");
if (receiptToast) setTimeout(() => receiptToast.remove(), 2400);
