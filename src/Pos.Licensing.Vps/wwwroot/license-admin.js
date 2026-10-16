(() => {
  "use strict";

  let csrfToken = "";
  const loginPanel = document.querySelector("#login");
  const dashboard = document.querySelector("#dashboard");
  const loginError = document.querySelector("#login-error");
  const globalMessage = document.querySelector("#global-message");

  function showLogin() {
    csrfToken = "";
    dashboard.hidden = true;
    loginPanel.hidden = false;
  }

  function showDashboard(username, token) {
    csrfToken = token;
    document.querySelector("#admin-name").textContent = username;
    loginPanel.hidden = true;
    dashboard.hidden = false;
  }

  function setMessage(text, kind = "") {
    globalMessage.textContent = text;
    globalMessage.className = `message ${kind}`;
  }

  async function request(path, options = {}) {
    const method = (options.method || "GET").toUpperCase();
    const headers = new Headers(options.headers || {});
    if (options.body !== undefined) headers.set("Content-Type", "application/json");
    if (method !== "GET" && method !== "HEAD") headers.set("X-CSRF-Token", csrfToken);
    const response = await fetch(path, {
      ...options,
      method,
      headers,
      credentials: "same-origin",
      cache: "no-store",
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
    });
    if (response.status === 401 && path !== "/admin/api/session") {
      showLogin();
      throw new Error("La sesión terminó. Vuelve a ingresar.");
    }
    if (!response.ok) {
      let error = `El servicio respondió HTTP ${response.status}.`;
      try {
        const payload = await response.json();
        if (payload.error) error = payload.error;
        if (payload.detail) error += ` · ${payload.detail}`;
      } catch (_) { }
      if (error === "credentials_invalid") error = "Usuario, contraseña o código de autenticación incorrectos.";
      if (error === "login_rate_limited") error = "Demasiados intentos. Espera 15 minutos antes de probar otra vez.";
      throw new Error(error);
    }
    if (response.status === 204) return null;
    return response.json();
  }

  function toUtcEndOfDay(value) {
    if (!value) throw new Error("Indica una fecha de vencimiento.");
    return `${value}T23:59:59Z`;
  }

  function formatDate(value) {
    if (!value) return "—";
    const date = new Date(value);
    return Number.isNaN(date.valueOf()) ? value : date.toLocaleString("es-VE");
  }

  function downloadBase64(fileName, encoded) {
    const binary = atob(encoded);
    const bytes = new Uint8Array(binary.length);
    for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
    const url = URL.createObjectURL(new Blob([bytes], { type: "application/octet-stream" }));
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    document.body.append(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 30_000);
  }

  function button(label, action, style = "secondary") {
    const control = document.createElement("button");
    control.type = "button";
    control.className = style;
    control.textContent = label;
    control.addEventListener("click", action);
    return control;
  }

  function addCell(row, text) {
    const cell = document.createElement("td");
    cell.textContent = text ?? "—";
    row.append(cell);
    return cell;
  }

  async function downloadLicense(item) {
    const result = await request(`/admin/api/licenses/${encodeURIComponent(item.licenseId)}/document`);
    downloadBase64(`${item.licenseId}.poslic`, result.documentBase64);
    setMessage(`Archivo .poslic generado para ${item.customerName}.`, "success");
  }

  async function registerPayment(item) {
    const amountText = prompt(`Monto recibido para ${item.customerName}:`);
    if (amountText === null) return;
    const amount = Number(amountText.replace(",", "."));
    if (!Number.isFinite(amount) || amount <= 0) throw new Error("El monto debe ser mayor que cero.");
    const currency = prompt("Moneda (USD, VES, USDT, etc.):", "USD");
    if (currency === null) return;
    const reference = prompt("Referencia o número de comprobante:");
    if (reference === null || !reference.trim()) return;
    const date = prompt("Nueva fecha de vencimiento (AAAA-MM-DD):");
    if (date === null) return;
    const result = await request(`/admin/api/licenses/${encodeURIComponent(item.licenseId)}/payments`, {
      method: "POST",
      body: { amount, currency, reference, paidThroughUtc: toUtcEndOfDay(date) },
    });
    setMessage(`Pago registrado. El POS renovará automáticamente al conectarse. Vence ${formatDate(result.payment.paidThroughUtc)}.`, "success");
    await loadAll();
  }

  async function updateTerms(item) {
    const plan = prompt("Plan:", item.plan || "mensual");
    if (plan === null) return;
    const terminalText = prompt("Cantidad máxima de terminales:", String(item.maxTerminals));
    if (terminalText === null) return;
    const maxTerminals = Number(terminalText);
    if (!Number.isInteger(maxTerminals) || maxTerminals < 1 || maxTerminals > 1000)
      throw new Error("El límite de terminales debe estar entre 1 y 1000.");
    const entitlementsText = prompt("Módulos autorizados separados por coma (vacío = ninguno):", (item.entitlements || []).join(","));
    if (entitlementsText === null) return;
    const entitlements = [...new Set(entitlementsText.split(",").map(value => value.trim()).filter(Boolean))];
    await request(`/admin/api/licenses/${encodeURIComponent(item.licenseId)}/terms`, {
      method: "PUT",
      body: { plan: plan.trim(), maxTerminals, entitlements },
    });
    setMessage("Plan, módulos y terminales actualizados. Se aplicarán al sincronizar el POS.", "success");
    await loadAll();
  }

  async function changeStatus(item) {
    if (item.active) {
      const reason = prompt(`Motivo de suspensión para ${item.customerName}:`);
      if (reason === null || !reason.trim()) return;
      if (prompt("Escribe SUSPENDER para confirmar:") !== "SUSPENDER") return;
      await request(`/admin/api/licenses/${encodeURIComponent(item.licenseId)}/suspend`, {
        method: "POST", body: { reason: reason.trim() },
      });
      setMessage("Suspensión firmada y registrada. Se aplicará cuando el POS vuelva a conectarse.", "success");
    } else {
      if (new Date(item.paidThroughUtc) <= new Date())
        throw new Error("Registra primero el pago y una fecha vigente antes de reactivar.");
      if (prompt("Escribe REACTIVAR para confirmar:") !== "REACTIVAR") return;
      await request(`/admin/api/licenses/${encodeURIComponent(item.licenseId)}/enable`, { method: "POST" });
      setMessage("Suscripción reactivada. El POS recibirá la licencia al conectarse.", "success");
    }
    await loadAll();
  }

  async function loadLicenses() {
    const body = document.querySelector("#licenses-body");
    body.replaceChildren();
    const records = await request("/admin/api/licenses");
    if (!records.length) {
      const row = document.createElement("tr");
      const cell = addCell(row, "Todavía no hay suscripciones.");
      cell.colSpan = 7;
      body.append(row);
      return;
    }
    for (const item of records) {
      const row = document.createElement("tr");
      addCell(row, `${item.customerName} · ${item.customerRif}\n${item.licenseId}`);
      const status = addCell(row, "");
      const badge = document.createElement("span");
      const expiresAt = new Date(item.paidThroughUtc);
      const licenseState = !item.active ? "SUSPENDIDA" : expiresAt <= new Date() ? "VENCIDA / GRACIA" : "ACTIVA";
      badge.className = !item.active ? "badge off" : licenseState.startsWith("VENCIDA") ? "badge warn" : "badge";
      badge.textContent = licenseState;
      status.append(badge);
      addCell(row, item.plan);
      addCell(row, formatDate(item.paidThroughUtc));
      addCell(row, String(item.maxTerminals));
      addCell(row, formatDate(item.lastSeenAtUtc) === "—" ? "Nunca" : formatDate(item.lastSeenAtUtc));
      const actions = addCell(row, "");
      actions.className = "actions";
      const group = document.createElement("div");
      group.className = "row-actions";
      group.append(button(".poslic", () => downloadLicense(item).catch(showError)));
      group.append(button("Pago", () => registerPayment(item).catch(showError)));
      group.append(button("Términos", () => updateTerms(item).catch(showError)));
      group.append(button(item.active ? "Suspender" : "Reactivar", () => changeStatus(item).catch(showError), item.active ? "secondary" : "primary"));
      actions.append(group);
      row.children[0].classList.add("license-cell");
      body.append(row);
    }
  }

  async function loadAudit() {
    const body = document.querySelector("#audit-body");
    body.replaceChildren();
    const events = await request("/admin/api/audit");
    if (!events.length) {
      const row = document.createElement("tr");
      const cell = addCell(row, "Todavía no hay eventos.");
      cell.colSpan = 4;
      body.append(row);
      return;
    }
    for (const event of events) {
      const row = document.createElement("tr");
      addCell(row, formatDate(event.occurredAtUtc));
      addCell(row, event.action);
      addCell(row, event.licenseId);
      addCell(row, JSON.stringify(event.details));
      body.append(row);
    }
  }

  async function loadAll() {
    await Promise.all([loadLicenses(), loadAudit()]);
  }

  function showError(error) {
    setMessage(error instanceof Error ? error.message : "No se pudo completar la operación.", "error");
  }

  document.querySelector("#login-form").addEventListener("submit", async event => {
    event.preventDefault();
    loginError.textContent = "";
    const form = new FormData(event.currentTarget);
    const buttonControl = event.currentTarget.querySelector("button");
    buttonControl.disabled = true;
    try {
      const response = await request("/admin/api/session", {
        method: "POST",
        body: { username: form.get("username"), password: form.get("password"), totpCode: form.get("totpCode") },
      });
      event.currentTarget.reset();
      showDashboard(form.get("username"), response.csrfToken);
      await loadAll();
    } catch (error) {
      loginError.textContent = error.message || "No se pudo iniciar sesión.";
    } finally { buttonControl.disabled = false; }
  });

  document.querySelector("#logout").addEventListener("click", async () => {
    try { await request("/admin/api/session", { method: "DELETE" }); }
    catch (_) { }
    showLogin();
  });

  document.querySelector("#refresh").addEventListener("click", () => loadAll().catch(showError));

  document.querySelector("#create-form").addEventListener("submit", async event => {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    const buttonControl = event.currentTarget.querySelector("button");
    buttonControl.disabled = true;
    try {
      const entitlements = [...new Set(String(form.get("entitlements")).split(",").map(value => value.trim()).filter(Boolean))];
      const created = await request("/admin/api/licenses", {
        method: "POST",
        body: {
          installationId: form.get("installationId"), customerRif: form.get("customerRif"),
          customerName: form.get("customerName"), plan: form.get("plan"),
          maxTerminals: Number(form.get("maxTerminals")), entitlements,
          paidThroughUtc: toUtcEndOfDay(form.get("paidThroughDate")),
          initialPaymentAmount: Number(form.get("initialPaymentAmount")),
          initialPaymentCurrency: form.get("initialPaymentCurrency"),
          initialPaymentReference: form.get("initialPaymentReference"),
        },
      });
      downloadBase64(`${created.licenseId}.poslic`, created.documentBase64);
      setMessage(`Suscripción ${created.licenseId} creada. Importa este archivo .poslic una vez en el POS.`, "success");
      event.currentTarget.reset();
      await loadAll();
    } catch (error) { showError(error); }
    finally { buttonControl.disabled = false; }
  });

  document.querySelector("#recovery-form").addEventListener("submit", async event => {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    const file = form.get("requestFile");
    const buttonControl = event.currentTarget.querySelector("button");
    if (!file || file.size <= 0 || file.size > 16 * 1024) {
      showError(new Error("El archivo .posreq está vacío o supera 16 KiB."));
      return;
    }
    buttonControl.disabled = true;
    try {
      const recoveryRequest = JSON.parse(await file.text());
      if (prompt("Escribe AUTORIZAR para firmar un desbloqueo temporal:") !== "AUTORIZAR") return;
      const result = await request("/admin/api/recoveries", {
        method: "POST",
        body: { request: recoveryRequest, durationHours: Number(form.get("durationHours")), reason: form.get("reason") },
      });
      downloadBase64(`${result.requestId}.posunlock`, result.documentBase64);
      setMessage(`Desbloqueo temporal firmado hasta ${formatDate(result.expiresAtUtc)}. Cópialo a esa PC e impórtalo; no puede prolongarse con el mismo archivo.`, "success");
      event.currentTarget.reset();
      await loadAudit();
    } catch (error) { showError(error); }
    finally { buttonControl.disabled = false; }
  });

  async function resumeSession() {
    try {
      const session = await request("/admin/api/session");
      showDashboard(session.username, session.csrfToken);
      await loadAll();
    } catch (_) { showLogin(); }
  }

  resumeSession().catch(showError);
})();
