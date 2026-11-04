const loginPanel = document.querySelector("#login-panel");
const dashboard = document.querySelector("#dashboard");
const loginForm = document.querySelector("#login-form");
const loginMessage = document.querySelector("#login-message");
const galleryForm = document.querySelector("#gallery-form");
const galleryList = document.querySelector("#admin-gallery-list");
const editorMessage = document.querySelector("#editor-message");
const logoutButton = document.querySelector("#logout");
const privateGalleryKeyControl = document.querySelector("#private-gallery-key-control");
const privateGalleryKeyHint = document.querySelector("#private-gallery-key-hint");
const privateGalleryShare = document.querySelector("#private-gallery-share");
const privateGalleryShareHint = document.querySelector("#private-gallery-share-hint");
const shareLinkControl = document.querySelector("#share-link-control");
const shareLinkValue = document.querySelector("#share-link-value");
const shareLinkMessage = document.querySelector("#share-link-message");
const generateShareLinkButton = document.querySelector("#generate-share-link");
const revokeShareLinkButton = document.querySelector("#revoke-share-link");
const gallerySearch = document.querySelector("#gallery-search");
const galleryFilter = document.querySelector("#gallery-filter");
const gallerySort = document.querySelector("#gallery-sort");
const collectionResult = document.querySelector("#collection-result");
const clearGalleryFilters = document.querySelector("#clear-gallery-filters");
let galleries = [];
let antiforgeryToken = "";
let editorKeyConfigured = false;
let editorShareLinkConfigured = false;

function cachedPhotoUrl(url, width) {
  if (!url.startsWith("/uploads/") && !url.startsWith("/local-photos/"))
    return url;

  const cachedUrl = new URL(url, window.location.href);
  cachedUrl.searchParams.set("width", String(width));
  return `${cachedUrl.pathname}${cachedUrl.search}`;
}

async function api(url, options = {}) {
  const response = await fetch(url, { cache: "no-store", credentials: "same-origin", ...options });
  if (!response.ok) {
    let message = `Request failed (${response.status}).`;
    try {
      const body = await response.json();
      if (body.error) message = body.error;
    } catch { }
    throw new Error(message);
  }
  if (response.status === 204) return null;
  return response.json();
}

async function loadAntiforgeryToken() {
  const result = await api("/api/auth/antiforgery");
  antiforgeryToken = result.token;
}

async function showDashboard() {
  loginPanel.classList.add("hidden");
  dashboard.classList.remove("hidden");
  logoutButton.classList.remove("hidden");
  try {
    await loadAntiforgeryToken();
    await refreshGalleries();
  } catch (error) {
    dashboard.classList.add("hidden");
    loginPanel.classList.remove("hidden");
    logoutButton.classList.add("hidden");
    loginMessage.textContent = error.message;
  }
}

async function refreshGalleries() {
  galleries = await api("/api/admin/galleries");
  document.querySelector("#admin-gallery-count").textContent = String(galleries.length).padStart(2, "0");
  updateCollectionStats();
  renderGalleryList();
}

function updateCollectionStats() {
  document.querySelector("#admin-stat-galleries").textContent = String(galleries.length);
  document.querySelector("#admin-stat-photos").textContent = String(
    galleries.reduce((total, gallery) => total + gallery.images.length, 0)
  );
  document.querySelector("#admin-stat-public-events").textContent = String(
    galleries.filter(gallery => gallery.isEvent && !gallery.isPrivate).length
  );
  document.querySelector("#admin-stat-private-events").textContent = String(
    galleries.filter(gallery => gallery.isEvent && gallery.isPrivate).length
  );
}

function getVisibleGalleries() {
  const query = gallerySearch.value.trim().toLocaleLowerCase();
  const filter = galleryFilter.value;
  const visible = galleries.filter(gallery => {
    const matchesFilter = filter === "all" ||
      (filter === "portfolio" && !gallery.isEvent) ||
      (filter === "public-event" && gallery.isEvent && !gallery.isPrivate) ||
      (filter === "private-event" && gallery.isEvent && gallery.isPrivate);
    const searchable = [gallery.title, gallery.category, gallery.location, gallery.date]
      .join(" ")
      .toLocaleLowerCase();
    return matchesFilter && (!query || searchable.includes(query));
  });

  visible.sort((first, second) => {
    if (gallerySort.value === "title")
      return first.title.localeCompare(second.title, undefined, { sensitivity: "base" });
    if (gallerySort.value === "photos")
      return second.images.length - first.images.length || first.title.localeCompare(second.title);
    const dateOrder = first.date.localeCompare(second.date);
    return gallerySort.value === "oldest" ? dateOrder : -dateOrder;
  });
  return visible;
}

function renderGalleryList() {
  galleryList.replaceChildren();
  const visible = getVisibleGalleries();
  collectionResult.textContent = `Showing ${visible.length} of ${galleries.length} ${galleries.length === 1 ? "gallery" : "galleries"}`;
  clearGalleryFilters.classList.toggle(
    "hidden",
    !gallerySearch.value && galleryFilter.value === "all" && gallerySort.value === "newest"
  );
  if (!galleries.length || !visible.length) {
    const empty = document.createElement("p");
    empty.className = "gallery-list-empty";
    empty.textContent = galleries.length
      ? "No galleries match those filters. Try a different search or clear the filters."
      : "Your first story is waiting to be added.";
    galleryList.append(empty);
    return;
  }

  visible.forEach(gallery => {
    const selected = galleryForm.elements.id.value === gallery.id && !galleryForm.classList.contains("hidden");
    const item = document.createElement("button");
    item.type = "button";
    item.className = "admin-gallery-item";
    item.classList.toggle("selected", selected);
    item.dataset.id = gallery.id;
    item.setAttribute("aria-pressed", String(selected));
    const image = document.createElement("img");
    image.src = cachedPhotoUrl(gallery.coverImage, 480);
    image.alt = "";
    image.loading = "lazy";
    const copy = document.createElement("div");
    const title = document.createElement("h3");
    title.textContent = gallery.title;
    const category = document.createElement("p");
    category.textContent = `${gallery.category} · ${gallery.location || "No location"}`;
    const metadata = document.createElement("p");
    const visibility = gallery.isPrivate ? "PRIVATE EVENT" : gallery.isEvent ? "PUBLIC EVENT" : "PORTFOLIO";
    metadata.textContent = `${visibility} · ${gallery.images.length} ${gallery.images.length === 1 ? "photo" : "photos"} · ${gallery.date}`;
    copy.append(title, category, metadata);
    item.append(image, copy);
    item.addEventListener("click", () => editGallery(gallery));
    galleryList.append(item);
  });
}

function editGallery(gallery) {
  galleryForm.classList.remove("hidden");
  galleryForm.elements.id.value = gallery.id;
  galleryForm.elements.title.value = gallery.title;
  galleryForm.elements.category.value = gallery.category;
  galleryForm.elements.location.value = gallery.location;
  galleryForm.elements.description.value = gallery.description || "";
  galleryForm.elements.date.value = gallery.date;
  galleryForm.elements.images.value = gallery.images.join("\n");
  galleryForm.elements.isEvent.checked = gallery.isEvent;
  galleryForm.elements.isPrivate.checked = gallery.isPrivate;
  galleryForm.elements.isPrivate.disabled = !gallery.isEvent;
  galleryForm.elements.accessKey.value = "";
  editorKeyConfigured = gallery.accessKeyConfigured;
  editorShareLinkConfigured = gallery.shareLinkConfigured;
  shareLinkValue.value = "";
  shareLinkControl.classList.add("hidden");
  shareLinkMessage.textContent = "";
  updatePrivateGalleryKeyField();
  document.querySelector("#editor-title").textContent = "Edit gallery";
  document.querySelector("#editor-eyebrow").textContent = "EDIT STORY";
  document.querySelector("#delete-gallery").classList.remove("hidden");
  editorMessage.textContent = "";
  document.querySelector("#gemini-status").textContent = "";
  renderGalleryList();
  galleryForm.scrollIntoView({ behavior: "smooth", block: "nearest" });
}

function startNewGallery() {
  galleryForm.reset();
  galleryForm.classList.remove("hidden");
  galleryForm.elements.id.value = "";
  const today = new Date();
  galleryForm.elements.date.value = new Date(today.getTime() - today.getTimezoneOffset() * 60000).toISOString().slice(0, 10);
  galleryForm.elements.isEvent.checked = false;
  galleryForm.elements.isPrivate.checked = false;
  galleryForm.elements.isPrivate.disabled = true;
  galleryForm.elements.description.value = "";
  galleryForm.elements.accessKey.value = "";
  editorKeyConfigured = false;
  editorShareLinkConfigured = false;
  shareLinkValue.value = "";
  shareLinkControl.classList.add("hidden");
  shareLinkMessage.textContent = "";
  updatePrivateGalleryKeyField();
  document.querySelector("#editor-title").textContent = "A new gallery";
  document.querySelector("#editor-eyebrow").textContent = "NEW STORY";
  document.querySelector("#delete-gallery").classList.add("hidden");
  editorMessage.textContent = "";
  document.querySelector("#gemini-status").textContent = "";
  galleryList.querySelector(".selected")?.classList.remove("selected");
  renderGalleryList();
  galleryForm.scrollIntoView({ behavior: "smooth", block: "nearest" });
}

function updatePrivateGalleryKeyField() {
  const isPrivate = galleryForm.elements.isPrivate.checked;
  const keyInput = galleryForm.elements.accessKey;
  privateGalleryKeyControl.classList.toggle("hidden", !isPrivate);
  keyInput.required = isPrivate && !editorKeyConfigured;
  privateGalleryKeyHint.textContent = editorKeyConfigured
    ? "A key is set. Leave blank to keep it, or enter a new unique key to replace it."
    : "Choose a unique 8-character key and share it with this gallery’s guests.";
  const canShare = isPrivate && Boolean(galleryForm.elements.id.value) && editorKeyConfigured;
  privateGalleryShare.classList.toggle("hidden", !canShare);
  revokeShareLinkButton.classList.toggle("hidden", !editorShareLinkConfigured);
  generateShareLinkButton.textContent = editorShareLinkConfigured
    ? "Create & copy new link"
    : "Create & copy link";
  privateGalleryShareHint.textContent = editorShareLinkConfigured
    ? "An active link already exists. Creating a new one revokes the previous link. Guest access lasts 8 hours."
    : "Create a secure link guests can open without entering the key. Guest access lasts 8 hours.";
}

let searchDebounceTimeout = null;
gallerySearch.addEventListener("input", () => {
  clearTimeout(searchDebounceTimeout);
  searchDebounceTimeout = setTimeout(renderGalleryList, 120);
});
galleryFilter.addEventListener("change", renderGalleryList);
gallerySort.addEventListener("change", renderGalleryList);
clearGalleryFilters.addEventListener("click", () => {
  gallerySearch.value = "";
  galleryFilter.value = "all";
  gallerySort.value = "newest";
  renderGalleryList();
  gallerySearch.focus();
});

loginForm.addEventListener("submit", async event => {
  event.preventDefault();
  loginMessage.textContent = "";
  const button = loginForm.querySelector("button");
  button.disabled = true;
  try {
    await api("/api/auth/login", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        email: loginForm.elements.email.value,
        password: loginForm.elements.password.value
      })
    });
    loginForm.reset();
    await showDashboard();
  } catch {
    loginMessage.textContent = "That email and password didn't match. Try again.";
  } finally {
    button.disabled = false;
  }
});

galleryForm.addEventListener("submit", async event => {
  event.preventDefault();
  editorMessage.textContent = "";
  const id = galleryForm.elements.id.value;
  const images = galleryForm.elements.images.value.split("\n").map(value => value.trim()).filter(Boolean);
  const payload = {
    title: galleryForm.elements.title.value,
    category: galleryForm.elements.category.value,
    location: galleryForm.elements.location.value,
    description: galleryForm.elements.description.value,
    date: galleryForm.elements.date.value,
    images,
    isEvent: galleryForm.elements.isEvent.checked,
    isPrivate: galleryForm.elements.isPrivate.checked,
    accessKey: galleryForm.elements.isPrivate.checked ? galleryForm.elements.accessKey.value : null
  };
  const button = galleryForm.querySelector('[type="submit"]');
  button.disabled = true;
  try {
    await api(id ? `/api/admin/galleries/${id}` : "/api/admin/galleries", {
      method: id ? "PUT" : "POST",
      headers: {
        "Content-Type": "application/json",
        RequestVerificationToken: antiforgeryToken
      },
      body: JSON.stringify(payload)
    });
    galleryForm.classList.add("hidden");
    await refreshGalleries();
  } catch (error) {
    editorMessage.textContent = error.message;
  } finally {
    button.disabled = false;
  }
});

document.querySelector("#new-gallery").addEventListener("click", startNewGallery);
document.querySelector("#cancel-edit").addEventListener("click", () => {
  galleryForm.classList.add("hidden");
  renderGalleryList();
});
galleryForm.elements.isEvent.addEventListener("change", () => {
  galleryForm.elements.isPrivate.disabled = !galleryForm.elements.isEvent.checked;
  if (!galleryForm.elements.isEvent.checked)
    galleryForm.elements.isPrivate.checked = false;
  updatePrivateGalleryKeyField();
});
galleryForm.elements.isPrivate.addEventListener("change", updatePrivateGalleryKeyField);
generateShareLinkButton.addEventListener("click", async () => {
  const id = galleryForm.elements.id.value;
  if (!id) return;

  generateShareLinkButton.disabled = true;
  shareLinkMessage.textContent = "";
  try {
    const result = await api(`/api/admin/galleries/${id}/share-link`, {
      method: "POST",
      headers: { RequestVerificationToken: antiforgeryToken }
    });
    shareLinkValue.value = result.url;
    shareLinkControl.classList.remove("hidden");
    editorShareLinkConfigured = true;
    updatePrivateGalleryKeyField();
    try {
      await navigator.clipboard.writeText(result.url);
      shareLinkMessage.textContent = "New guest link copied. Any previous link has been revoked.";
    } catch {
      shareLinkValue.focus();
      shareLinkValue.select();
      shareLinkMessage.textContent = "New guest link created. Copy it from the field above.";
    }
    await refreshGalleries();
  } catch (error) {
    shareLinkMessage.textContent = error.message;
  } finally {
    generateShareLinkButton.disabled = false;
  }
});

revokeShareLinkButton.addEventListener("click", async () => {
  const id = galleryForm.elements.id.value;
  if (!id) return;

  revokeShareLinkButton.disabled = true;
  shareLinkMessage.textContent = "";
  try {
    await api(`/api/admin/galleries/${id}/share-link`, {
      method: "DELETE",
      headers: { RequestVerificationToken: antiforgeryToken }
    });
    editorShareLinkConfigured = false;
    shareLinkValue.value = "";
    shareLinkControl.classList.add("hidden");
    updatePrivateGalleryKeyField();
    shareLinkMessage.textContent = "The guest link has been revoked.";
    await refreshGalleries();
  } catch (error) {
    shareLinkMessage.textContent = error.message;
  } finally {
    revokeShareLinkButton.disabled = false;
  }
});
document.querySelector("#delete-gallery").addEventListener("click", async () => {
  const id = galleryForm.elements.id.value;
  if (!id || !window.confirm("Delete this gallery? This cannot be undone.")) return;
  try {
    await api(`/api/admin/galleries/${id}`, {
      method: "DELETE",
      headers: { RequestVerificationToken: antiforgeryToken }
    });
    galleryForm.classList.add("hidden");
    await refreshGalleries();
  } catch (error) {
    editorMessage.textContent = error.message;
  }
});

document.querySelector("#image-upload").addEventListener("change", async event => {
  const input = event.currentTarget;
  const files = [...input.files];
  editorMessage.textContent = "";
  let uploaded = 0;
  for (const [index, file] of files.entries()) {
    if (file.size > 20 * 1024 * 1024) {
      editorMessage.textContent = `${file.name} is larger than 20 MB.`;
      break;
    }

    const formData = new FormData();
    try {
      editorMessage.textContent = `Preparing photograph ${index + 1} of ${files.length}…`;
      let optimized;
      try {
        optimized = await optimizeImage(file);
      } catch {
        optimized = file;
      }
      formData.append("file", optimized);
      const result = await api("/api/uploads", {
        method: "POST",
        headers: { RequestVerificationToken: antiforgeryToken },
        body: formData
      });
      const images = galleryForm.elements.images;
      images.value = `${images.value.trim()}${images.value.trim() ? "\n" : ""}${result.url}`;
      uploaded++;
    } catch (error) {
      editorMessage.textContent = error.message;
      break;
    }
  }
  if (uploaded > 0) {
    editorMessage.textContent =
      `Added ${uploaded} ${uploaded === 1 ? "photograph" : "photographs"}. Save gallery to publish ${uploaded === 1 ? "it" : "them"}.`;
    if (!galleryForm.elements.title.value.trim()) {
      geminiSuggestBtn?.click();
    }
  }
  input.value = "";
});

async function optimizeImage(file) {
  if (typeof createImageBitmap !== "function") return file;
  const bitmap = await createImageBitmap(file);
  const scale = Math.min(1, 2400 / Math.max(bitmap.width, bitmap.height));
  const canvas = document.createElement("canvas");
  canvas.width = Math.round(bitmap.width * scale);
  canvas.height = Math.round(bitmap.height * scale);
  const context = canvas.getContext("2d");
  if (!context) {
    bitmap.close();
    throw new Error("This browser could not prepare the image.");
  }

  context.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
  bitmap.close();
  try {
    const blob = await new Promise((resolve, reject) => {
      canvas.toBlob(result => result ? resolve(result) : reject(new Error("This image could not be optimized.")), "image/webp", 0.84);
    });
    if (blob.type !== "image/webp" || blob.size >= file.size) return file;
    return new File([blob], `${file.name.replace(/\.[^.]+$/, "")}.webp`, { type: "image/webp" });
  } finally {
    canvas.width = 0;
    canvas.height = 0;
  }
}

logoutButton.addEventListener("click", async () => {
  try {
    await api("/api/auth/logout", { method: "POST" });
    dashboard.classList.add("hidden");
    loginPanel.classList.remove("hidden");
    logoutButton.classList.add("hidden");
  } catch (error) {
    loginMessage.textContent = error.message;
  }
});

document.querySelector("#year").textContent = String(new Date().getFullYear());
api("/api/auth/session")
  .then(session => session.authenticated ? showDashboard() : undefined)
  .catch(error => {
    loginMessage.textContent = error.message;
  });

// --- Gemini AI Auto Naming & Description ---
const geminiSuggestBtn = document.querySelector("#gemini-suggest-btn");
const geminiStatus = document.querySelector("#gemini-status");

geminiSuggestBtn?.addEventListener("click", async () => {
  const images = galleryForm.elements.images.value
    .split("\n")
    .map(value => value.trim())
    .filter(Boolean);

  if (!images.length) {
    geminiStatus.textContent = "Upload or enter photographs first to analyze with Gemini.";
    geminiStatus.className = "ai-status error";
    return;
  }

  geminiSuggestBtn.disabled = true;
  geminiStatus.textContent = "Gemini is analyzing your photographs…";
  geminiStatus.className = "ai-status active";

  try {
    const hint = galleryForm.elements.title.value.trim() || galleryForm.elements.category.value.trim();
    const result = await api("/api/admin/gemini/suggest", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        RequestVerificationToken: antiforgeryToken
      },
      body: JSON.stringify({ images, hint })
    });

    if (result.title) galleryForm.elements.title.value = result.title;
    if (result.category) galleryForm.elements.category.value = result.category;
    if (result.location) galleryForm.elements.location.value = result.location;
    if (result.date && (!galleryForm.elements.date.value || galleryForm.elements.date.value === new Date().toISOString().slice(0, 10))) {
      galleryForm.elements.date.value = result.date;
    }
    if (result.description) galleryForm.elements.description.value = result.description;

    geminiStatus.textContent = "✨ Title, category, location, and story generated by Gemini!";
    geminiStatus.className = "ai-status success";
  } catch (error) {
    geminiStatus.textContent = error.message;
    geminiStatus.className = "ai-status error";
    if (error.message.includes("Settings") || error.message.includes("key")) {
      openSettingsDialog();
    }
  } finally {
    geminiSuggestBtn.disabled = false;
  }
});

// --- Google Drive Event Import ---
const gdriveModal = document.querySelector("#gdrive-modal");
const openGdriveModalBtn = document.querySelector("#open-gdrive-modal");
const editorOpenGdriveBtn = document.querySelector("#editor-open-gdrive");
const closeGdriveModalBtn = document.querySelector("#close-gdrive-modal");
const cancelGdriveModalBtn = document.querySelector("#cancel-gdrive-modal");
const gdriveForm = document.querySelector("#gdrive-form");
const gdriveMessage = document.querySelector("#gdrive-message");
const gdriveSubmitBtn = document.querySelector("#gdrive-submit-btn");

function openGdriveModal() {
  gdriveMessage.textContent = "";
  gdriveMessage.className = "form-message";
  gdriveModal.showModal();
  document.querySelector("#gdrive-url").focus();
}

function closeGdriveModal() {
  gdriveModal.close();
  gdriveForm.reset();
}

openGdriveModalBtn?.addEventListener("click", openGdriveModal);
editorOpenGdriveBtn?.addEventListener("click", openGdriveModal);
closeGdriveModalBtn?.addEventListener("click", closeGdriveModal);
cancelGdriveModalBtn?.addEventListener("click", closeGdriveModal);

gdriveForm?.addEventListener("submit", async event => {
  event.preventDefault();
  gdriveMessage.textContent = "Connecting to Google Drive and downloading photographs…";
  gdriveMessage.className = "form-message active";
  gdriveSubmitBtn.disabled = true;

  const url = document.querySelector("#gdrive-url").value.trim();
  const isEvent = document.querySelector("#gdrive-is-event").checked;
  const autoDescribe = document.querySelector("#gdrive-auto-gemini").checked;

  try {
    const result = await api("/api/admin/gdrive/import", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        RequestVerificationToken: antiforgeryToken
      },
      body: JSON.stringify({ url, isEvent, autoDescribe })
    });

    closeGdriveModal();

    if (galleryForm.classList.contains("hidden")) {
      startNewGallery();
    }

    if (result.title) galleryForm.elements.title.value = result.title;
    if (result.category) galleryForm.elements.category.value = result.category;
    if (result.location) galleryForm.elements.location.value = result.location;
    if (result.date) galleryForm.elements.date.value = result.date;
    if (result.description) galleryForm.elements.description.value = result.description;

    galleryForm.elements.isEvent.checked = result.isEvent;
    galleryForm.elements.isPrivate.disabled = !result.isEvent;
    updatePrivateGalleryKeyField();

    const existingImages = galleryForm.elements.images.value.split("\n").filter(Boolean);
    const combined = [...existingImages, ...result.images];
    galleryForm.elements.images.value = combined.join("\n");

    editorMessage.textContent = `Imported ${result.images.length} photographs from Google Drive. Save gallery to publish.`;
    editorMessage.className = "form-message success";
    galleryForm.scrollIntoView({ behavior: "smooth" });
  } catch (error) {
    gdriveMessage.textContent = error.message;
    gdriveMessage.className = "form-message error";
  } finally {
    gdriveSubmitBtn.disabled = false;
  }
});

// --- Studio Settings ---
const settingsDialog = document.querySelector("#settings-dialog");
const openSettingsBtn = document.querySelector("#open-settings");
const closeSettingsBtn = document.querySelector("#close-settings");
const cancelSettingsBtn = document.querySelector("#cancel-settings");
const settingsForm = document.querySelector("#settings-form");
const settingsMessage = document.querySelector("#settings-message");
const geminiKeyHint = document.querySelector("#gemini-key-hint");
const gdriveKeyHint = document.querySelector("#gdrive-key-hint");

async function openSettingsDialog() {
  settingsMessage.textContent = "";
  try {
    const info = await api("/api/admin/settings");
    if (info.hasGeminiKey) {
      geminiKeyHint.textContent = `Configured: ${info.geminiKeyMasked}. Enter a new key to update, or leave blank to keep.`;
    } else {
      geminiKeyHint.textContent = "No key configured. Get a key at ai.google.dev (free tier available).";
    }
    if (info.hasGoogleDriveKey) {
      gdriveKeyHint.textContent = `Configured: ${info.googleDriveKeyMasked}. Enter a new key to update, or leave blank.`;
    } else {
      gdriveKeyHint.textContent = "Shared view-only links work without a key. An API key is needed for restricted folders.";
    }
  } catch { }
  settingsDialog.showModal();
}

function closeSettingsDialog() {
  settingsDialog.close();
  settingsForm.reset();
}

openSettingsBtn?.addEventListener("click", openSettingsDialog);
closeSettingsBtn?.addEventListener("click", closeSettingsDialog);
cancelSettingsBtn?.addEventListener("click", closeSettingsDialog);

settingsForm?.addEventListener("submit", async event => {
  event.preventDefault();
  settingsMessage.textContent = "";
  const geminiKey = document.querySelector("#settings-gemini-key").value.trim();
  const gdriveKey = document.querySelector("#settings-gdrive-key").value.trim();

  try {
    await api("/api/admin/settings", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        RequestVerificationToken: antiforgeryToken
      },
      body: JSON.stringify({
        geminiApiKey: geminiKey || null,
        googleDriveApiKey: gdriveKey || null
      })
    });
    settingsMessage.textContent = "Settings saved successfully.";
    settingsMessage.className = "form-message success";
    setTimeout(closeSettingsDialog, 1000);
  } catch (error) {
    settingsMessage.textContent = error.message;
    settingsMessage.className = "form-message error";
  }
});
