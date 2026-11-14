(() => {
  const form = document.querySelector("#dueMode")?.closest("form");
  if (form) {
    const mode = form.querySelector("#dueMode");
    const specified = form.querySelector('[name="Input.SpecifiedDueDate"]') || form.querySelector("#SpecifiedDueDate");
    const dateGroup = form.querySelector("#specifiedDateGroup");
    const type = form.querySelector("#projectType");
    const category = form.querySelector('[name="Input.ScheduleCategory"]') || form.querySelector("#ScheduleCategory");
    const received = form.querySelector('[name="Input.ReceivedDate"]') || form.querySelector("#ReceivedDate");
    const preview = form.querySelector("#dueDatePreview");
    const updatePreview = () => {
      if (dateGroup) dateGroup.hidden = mode?.value !== "Manual";
      if (!preview) return;
      let categoryValue = category?.value;
      if (!categoryValue) categoryValue = type?.value === "PhysicalProduction" ? "Physical" : type?.value === "MixedOrUnknown" ? "Pending" : "Digital";
      if (mode?.value === "HistoricalUnknown") { preview.textContent = "历史截止日期未知；保存后仍为空，除非你明确切换模式。"; return; }
      if (mode?.value === "Manual") { preview.textContent = specified?.value ? `手动指定：${specified.value}` : "手动模式需要填写截止日期。"; return; }
      if (categoryValue === "Physical") { preview.textContent = "实物项目请填写约定交付日：选择手动指定并填写日期；系统不会自动猜测工期。"; return; }
      if (categoryValue !== "Digital" || !received?.value) { preview.textContent = "当前分类或接单日期不足以自动计算截止日期，保存后显示待补时限。"; return; }
      const date = new Date(`${received.value}T12:00:00`);
      date.setDate(date.getDate() + Number(preview.dataset.defaultDays || 1));
      preview.textContent = `自动截止日期预览：${date.getFullYear()}-${String(date.getMonth()+1).padStart(2,"0")}-${String(date.getDate()).padStart(2,"0")}（保存结果以服务返回为准）`;
    };
    [mode, type, category, received].filter(Boolean).forEach(el => el.addEventListener("change", updatePreview));
    specified?.addEventListener("input", updatePreview);
    specified?.addEventListener("change", () => {
      if (!specified.value && mode?.value === "Manual") mode.value = "Auto";
      updatePreview();
    });
    updatePreview();
    const checkButton = form.querySelector("#checkPath");
    checkButton?.addEventListener("click", async () => {
      const target = form.querySelector("#pathCheckMessage");
      const path = form.querySelector("#projectPath")?.value || "";
      const url = new URL(window.location.href);
      url.searchParams.set("handler", "CheckPath");
      url.searchParams.set("path", path);
      if (target) target.textContent = "正在检查路径…";
      try {
        const response = await fetch(url, { headers: { "Accept": "application/json" } });
        const result = await response.json();
        if (target) target.textContent = result.message || (result.isDirectory ? "目录可访问。" : result.isSpecified ? "路径不是可访问的目录。" : "未填写路径。");
      } catch { if (target) target.textContent = "路径检查暂不可用；这不会阻止保存工作项。"; }
    });
    form.querySelector("#copyPath")?.addEventListener("click", async () => {
      const target = form.querySelector("#pathCheckMessage");
      const path = form.querySelector("#projectPath")?.value || "";
      if (!path) { if (target) target.textContent = "尚未填写路径。"; return; }
      try { await navigator.clipboard.writeText(path); if (target) target.textContent = "路径已复制。"; }
      catch { if (target) target.textContent = "浏览器未允许复制；请手动选择并复制路径。"; }
    });
  }

  const target = document.querySelector("#statusTarget");
  const confirmGroup = document.querySelector("#cancelConfirmGroup");
  const confirmCancel = document.querySelector("#confirmCancel");
  const cancelFulfillmentGroup = document.querySelector("#cancelFulfillmentGroup");
  const continueCancelFulfillment = document.querySelector("#continueCancelFulfillment");
  const statusForm = target?.closest("form");
  const updateCancel = () => {
    if (!confirmGroup || !target) return;
    confirmGroup.hidden = target.value !== "Cancelled";
    if (cancelFulfillmentGroup) cancelFulfillmentGroup.hidden = target.value !== "Cancelled";
    if (target.value !== "Cancelled" && confirmCancel) confirmCancel.checked = false;
    if (target.value !== "Cancelled" && continueCancelFulfillment) continueCancelFulfillment.checked = false;
  };
  target?.addEventListener("change", updateCancel);
  updateCancel();
  statusForm?.addEventListener("submit", e => {
    if (target?.value === "Cancelled" && !confirmCancel?.checked) { e.preventDefault(); confirmCancel?.focus(); }
  });

  const restoreTarget = document.querySelector("#restoreTarget");
  const restoreChoice = document.querySelector("#restoreChoice");
  const syncRestore = () => { if (restoreChoice && restoreTarget) restoreChoice.value = restoreTarget.value; };
  restoreTarget?.addEventListener("change", syncRestore);
  restoreTarget?.form?.addEventListener("submit", syncRestore);

  const pathStatus = document.querySelector("#pathStatus[data-check-url]");
  if (pathStatus?.dataset.path) {
    const url = new URL(pathStatus.dataset.checkUrl, window.location.origin);
    url.searchParams.set("path", pathStatus.dataset.path);
    fetch(url, { headers: { "Accept": "application/json" } }).then(r => r.json()).then(result => {
      pathStatus.textContent = result.message || (result.isDirectory ? "目录可访问。" : "目录暂不可访问。保存工作项不受影响。");
    }).catch(() => { pathStatus.textContent = "无法检查目录；保存工作项不受影响。"; });
  }
  document.querySelector("#copyDetailPath")?.addEventListener("click", async e => {
    const button = e.currentTarget;
    const status = document.querySelector("#pathStatus");
    const path = button.dataset.path || "";
    if (!path) { if (status) status.textContent = "尚未填写路径。"; return; }
    try { await navigator.clipboard.writeText(path); if (status) status.textContent = "路径已复制。"; }
    catch { if (status) status.textContent = "浏览器未允许复制；请手动选择并复制路径。"; }
  });
})();
