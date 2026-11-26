// Remembers which text boxes you typed in, so the tab isn't unloaded while one
// still has text. Nothing is read out of the page: it only answers yes or no.
if (!globalThis.ramLimiterHasTyped) {
  const edited = new Set();

  const isSearch = (el) =>
    ["combobox", "searchbox"].includes(el.getAttribute("role")) ||
    /^(q|query|search|search_query)$/i.test(el.getAttribute("name") ?? "") ||
    Boolean(el.closest("[role=search]"));

  const isDraft = (el) =>
    el?.isContentEditable ||
    ((el?.tagName === "TEXTAREA" ||
      (el?.tagName === "INPUT" && ["text", "email", "url", "tel"].includes(el.type))) &&
      !isSearch(el));

  addEventListener("input", (event) => {
    const el = event.composedPath()[0];
    if (event.isTrusted && isDraft(el)) edited.add(el);
  }, true);

  // A submitted form is no longer a draft.
  addEventListener("submit", (event) => {
    for (const el of edited) if (event.target.contains(el)) edited.delete(el);
  }, true);

  globalThis.ramLimiterHasTyped = () =>
    [...edited].some((el) => el.isConnected && (el.value ?? el.textContent).trim() !== "");
}
