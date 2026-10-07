const storageKey = "slipmat-companion-onboarding-v1";

function setupCopyButtons() {
  for (const button of document.querySelectorAll("[data-copy]")) {
    button.addEventListener("click", async (event) => {
      event.preventDefault();
      const value = button.getAttribute("data-copy");
      if (!value) return;
      await navigator.clipboard.writeText(value);
      button.textContent = "Copied";
    });
  }
}

function setupChecklist() {
  const checks = Array.from(document.querySelectorAll("[data-step]"));
  if (checks.length === 0) return;

  try {
    const saved = JSON.parse(localStorage.getItem(storageKey) ?? "{}");
    for (const check of checks) {
      check.checked = Boolean(saved[check.dataset.step ?? ""]);
    }
  } catch {
    localStorage.removeItem(storageKey);
  }

  for (const check of checks) {
    check.addEventListener("change", () => {
      const state = Object.fromEntries(
        checks.map((item) => [item.dataset.step, item.checked]),
      );
      localStorage.setItem(storageKey, JSON.stringify(state));
    });
  }
}

setupCopyButtons();
setupChecklist();
