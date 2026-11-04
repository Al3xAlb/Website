const grid = document.querySelector("#gallery-grid");
const filters = document.querySelector("#filters");
const count = document.querySelector("#gallery-count");
const lightbox = document.querySelector("#lightbox");
const localAlbumLink = document.querySelector("#local-album-link");
const localPhotoNote = document.querySelector("#local-photo-note");
const isEventsPage = document.body.dataset.page === "events";
const eventKeyForm = document.querySelector("#event-key-form");
const eventKeyMessage = document.querySelector("#event-key-message");
const lockPrivateWork = document.querySelector("#lock-private-work");
const eventSplash = document.querySelector("#event-splash");
const privateAccessNote = document.querySelector("#private-access-note");
const shareTokenFromUrl = isEventsPage && window.location.hash.startsWith("#share=")
  ? new URLSearchParams(window.location.hash.slice(1)).get("share")
  : null;
if (shareTokenFromUrl)
  window.history.replaceState(null, "", `${window.location.pathname}${window.location.search}`);
let galleries = [];
let activePhotos = [];
let activeIndex = 0;
let activeTitle = "";
let antiforgeryToken = "";
let eventAccessUnlocked = false;

function makeFilter(label) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "filter-button";
  button.textContent = label;
  button.addEventListener("click", () => {
    filters.querySelector(".active")?.classList.remove("active");
    button.classList.add("active");
    renderGalleries(label);
  });
  return button;
}

function renderFilters() {
  filters.replaceChildren();
  filters.append(makeFilter("All stories"));
  [...new Set(galleries.map(gallery => gallery.category))].forEach(category => {
    filters.append(makeFilter(category));
  });
  filters.querySelector("button")?.classList.add("active");
}

function cachedPhotoUrl(url, width) {
  if (!width || (!url.startsWith("/uploads/") && !url.startsWith("/local-photos/")))
    return url;

  const cachedUrl = new URL(url, window.location.href);
  cachedUrl.searchParams.set("width", String(width));
  return `${cachedUrl.pathname}${cachedUrl.search}`;
}

function setResponsiveImage(image, url, sizes, cacheWidth = null) {
  image.removeAttribute("srcset");
  image.removeAttribute("sizes");

  if (url.startsWith("/uploads/") || url.startsWith("/local-photos/")) {
    image.srcset = `${cachedPhotoUrl(url, 480)} 480w, ${cachedPhotoUrl(url, 960)} 960w, ${cachedPhotoUrl(url, 1600)} 1600w`;
    image.sizes = sizes;
    image.src = cachedPhotoUrl(url, cacheWidth || 960);
    return;
  }

  if (!url.startsWith("https://images.unsplash.com/")) {
    image.src = cachedPhotoUrl(url, cacheWidth);
    return;
  }

  const source = new URL(url);
  const originalWidth = Number(source.searchParams.get("w"));
  if (!originalWidth) {
    image.src = url;
    return;
  }

  const widths = [480, 720, 960, 1280].filter(width => width < originalWidth);
  widths.push(originalWidth);
  if (widths.length < 2) {
    image.src = url;
    return;
  }

  image.srcset = widths.map(width => {
    const candidate = new URL(source);
    candidate.searchParams.set("w", String(width));
    return `${candidate} ${width}w`;
  }).join(", ");
  image.sizes = sizes;
  image.src = url;
}

function featureLocalPhotos() {
  if (isEventsPage) return;
  const localGalleries = galleries.filter(gallery => gallery.category === "Local photos");
  if (!localGalleries.length) return;

  const photoCount = localGalleries.reduce((total, gallery) => total + gallery.images.length, 0);
  const photoLabel = photoCount === 1 ? "photograph" : "photographs";
  localPhotoNote.textContent =
    `${localGalleries.length} ${localGalleries.length === 1 ? "album" : "albums"} from your photos folder · ${photoCount} ${photoLabel}`;
  localPhotoNote.classList.remove("hidden");

  const featuredGallery = localGalleries[0];
  const heroImage = document.querySelector(".hero-image img");
  if (heroImage) {
    heroImage.removeAttribute("srcset");
    heroImage.removeAttribute("sizes");
    heroImage.src = cachedPhotoUrl(featuredGallery.coverImage, 1600);
    heroImage.alt = `${featuredGallery.title}, from your local photo collection`;
    const caption = document.querySelector(".hero-image figcaption");
    caption.children[0].textContent = `01 / ${String(featuredGallery.images.length).padStart(2, "0")}`;
    caption.children[1].textContent = featuredGallery.title;
    caption.children[2].textContent = "FROM YOUR PHOTO FOLDER";

    const aboutImage = document.querySelector(".about-image img");
    const nextPhoto = localGalleries.flatMap(gallery => gallery.images)
      .find(image => image !== featuredGallery.coverImage);
    if (nextPhoto) {
      aboutImage.removeAttribute("srcset");
      aboutImage.removeAttribute("sizes");
      aboutImage.src = cachedPhotoUrl(nextPhoto, 1600);
      aboutImage.alt = "Another photograph from your local collection";
      document.querySelector(".image-stamp").textContent = "A MOMENT FROM YOUR COLLECTION";
    }

    localAlbumLink.classList.remove("hidden");
    localAlbumLink.addEventListener("click", () => openGallery(featuredGallery), { once: true });
  }
}

let revealObserver = null;
function activateRevealAnimations(elements = null) {
  const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  const revealItems = elements || document.querySelectorAll(".reveal");
  if (!revealItems.length) return;

  if (reducedMotion || typeof IntersectionObserver !== "function") {
    revealItems.forEach(element => element.classList.add("visible"));
    return;
  }

  if (!revealObserver) {
    revealObserver = new IntersectionObserver((entries) => {
      entries.forEach(entry => {
        if (entry.isIntersecting) {
          entry.target.classList.add("visible");
          revealObserver.unobserve(entry.target);
        }
      });
    }, { threshold: 0.15, rootMargin: "0px 0px -40px 0px" });
  }

  revealItems.forEach((element, index) => {
    element.style.transitionDelay = `${(index % 8) * 85}ms`;
    revealObserver.observe(element);
  });
}

function renderGalleries(category = "All stories") {
  const visible = category === "All stories"
    ? galleries
    : galleries.filter(gallery => gallery.category === category);
  count.textContent = String(visible.length).padStart(2, "0");
  grid.replaceChildren();

  if (!visible.length) {
    const empty = document.createElement("p");
    empty.className = "empty-note";
    empty.textContent = "No stories here just yet.";
    grid.append(empty);
    return;
  }

  visible.forEach((gallery, index) => {
    const card = document.createElement("article");
    card.className = "gallery-card reveal";
    card.style.transitionDelay = `${index * 80}ms`;
    card.tabIndex = 0;
    card.setAttribute("role", "button");
    card.setAttribute("aria-label", `View ${gallery.title} gallery`);

    const figure = document.createElement("figure");
    const image = document.createElement("img");
    setResponsiveImage(image, gallery.coverImage, "(max-width: 800px) 86vw, 48vw", 960);
    image.alt = gallery.location
      ? `${gallery.title} — ${gallery.location}`
      : `${gallery.title} photo album`;
    image.loading = "lazy";
    image.decoding = "async";
    const overlay = document.createElement("div");
    overlay.className = "card-overlay";
    const photoCount = document.createElement("span");
    photoCount.textContent = `${String(gallery.images.length).padStart(2, "0")} PHOTOGRAPHS`;
    const arrow = document.createElement("span");
    arrow.setAttribute("aria-hidden", "true");
    arrow.textContent = "↗";
    overlay.append(photoCount, arrow);
    figure.append(image, overlay);

    const caption = document.createElement("div");
    caption.className = "card-caption";
    const title = document.createElement("h3");
    title.textContent = gallery.title;
    const categoryLabel = document.createElement("span");
    categoryLabel.textContent = gallery.isPrivate ? `${gallery.category} · PRIVATE` : gallery.category;
    caption.append(title, categoryLabel);
    const location = document.createElement("p");
    location.className = "card-location";
    const place = gallery.location || (gallery.category === "Local photos" ? "From your photos folder" : "Somewhere lovely");
    location.textContent = `${place}  ·  ${new Date(`${gallery.date}T12:00:00`).getFullYear()}`;
    card.append(figure, caption, location);

    if (gallery.description) {
      const description = document.createElement("p");
      description.className = "card-description";
      description.textContent = gallery.description;
      card.append(description);
    }

    const open = () => openGallery(gallery);
    card.addEventListener("click", open);
    card.addEventListener("keydown", event => {
      if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        open();
      }
    });
    grid.append(card);
    activateRevealAnimations([card]);
  });
}

function openGallery(gallery) {
  activePhotos = gallery.images;
  activeIndex = 0;
  activeTitle = gallery.title;
  showPhoto();
  lightbox.showModal();
}

function preloadPhoto(url) {
  if (!url) return;
  const img = new Image();
  setResponsiveImage(img, url, "(max-width: 560px) calc(100vw - 108px), (max-width: 800px) calc(100vw - 180px), 88vw", 1600);
}

function showPhoto() {
  const image = lightbox.querySelector(".lightbox-image");
  setResponsiveImage(image, activePhotos[activeIndex], "(max-width: 560px) calc(100vw - 108px), (max-width: 800px) calc(100vw - 180px), 88vw", 1600);
  image.alt = `${activeTitle}, photograph ${activeIndex + 1}`;
  lightbox.querySelector(".lightbox-caption").textContent =
    `${activeTitle.toUpperCase()}  ·  ${String(activeIndex + 1).padStart(2, "0")} / ${String(activePhotos.length).padStart(2, "0")}`;

  if (activePhotos.length > 1) {
    const next = (activeIndex + 1) % activePhotos.length;
    const prev = (activeIndex - 1 + activePhotos.length) % activePhotos.length;
    preloadPhoto(activePhotos[next]);
    if (prev !== next) preloadPhoto(activePhotos[prev]);
  }
}

function movePhoto(direction) {
  if (!activePhotos.length) return;
  activeIndex = (activeIndex + direction + activePhotos.length) % activePhotos.length;
  showPhoto();
}

let swipeStart = null;
const lightboxImage = lightbox.querySelector(".lightbox-image");
lightboxImage.addEventListener("touchstart", event => {
  swipeStart = event.touches.length === 1
    ? { x: event.touches[0].clientX, y: event.touches[0].clientY }
    : null;
}, { passive: true });
lightboxImage.addEventListener("touchend", event => {
  if (!swipeStart) return;
  const start = swipeStart;
  swipeStart = null;
  if (event.changedTouches.length !== 1) return;
  const deltaX = event.changedTouches[0].clientX - start.x;
  const deltaY = event.changedTouches[0].clientY - start.y;
  if (Math.abs(deltaX) > 48 && Math.abs(deltaX) > Math.abs(deltaY) * 1.3)
    movePhoto(deltaX < 0 ? 1 : -1);
}, { passive: true });
lightboxImage.addEventListener("touchcancel", () => { swipeStart = null; }, { passive: true });

activateRevealAnimations();

if (!window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
  const heroImage = document.querySelector(".hero-image");
  const heroCopy = document.querySelector(".hero-copy");
  if (heroImage && heroCopy) {
    let ticking = false;
    let clientX = 0, clientY = 0;
    document.addEventListener("pointermove", event => {
      if (event.pointerType === "touch") return;
      clientX = event.clientX;
      clientY = event.clientY;
      if (!ticking) {
        requestAnimationFrame(() => {
          const { innerWidth, innerHeight } = window;
          const x = (clientX / innerWidth - .5) * 16;
          const y = (clientY / innerHeight - .5) * 16;
          heroImage.style.transform = `translate(${x * .25}px, ${y * .15}px)`;
          heroCopy.style.transform = `translate(${x * .1}px, ${y * .08}px)`;
          ticking = false;
        });
        ticking = true;
      }
    }, { passive: true });
    document.addEventListener("pointerleave", () => {
      heroImage.style.transform = "";
      heroCopy.style.transform = "";
    }, { passive: true });
  }
}

lightbox.querySelector(".lightbox-close").addEventListener("click", () => lightbox.close());
lightbox.querySelector(".lightbox-prev").addEventListener("click", () => movePhoto(-1));
lightbox.querySelector(".lightbox-next").addEventListener("click", () => movePhoto(1));
lightbox.addEventListener("click", event => {
  if (event.target === lightbox) lightbox.close();
});
document.addEventListener("keydown", event => {
  if (!lightbox.open) return;
  if (event.key === "ArrowLeft") movePhoto(-1);
  if (event.key === "ArrowRight") movePhoto(1);
});
document.querySelector("#year").textContent = String(new Date().getFullYear());

const contactSplash = document.querySelector("#contact-splash");
const contactForm = document.querySelector("#contact-form");
const welcomeSplash = document.querySelector("#welcome-splash");

function dismissWelcomeSplash() {
  welcomeSplash.close();
  document.cookie = "portfolio-welcome=seen; Path=/; SameSite=Lax";
}

document.querySelectorAll("[data-open-contact]").forEach(link => {
  link.addEventListener("click", event => {
    event.preventDefault();
    if (welcomeSplash?.open) dismissWelcomeSplash();
    contactSplash.showModal();
    document.querySelector("#contact-name").focus();
  });
});

document.querySelector(".contact-splash-dismiss").addEventListener("click", () => contactSplash.close());
contactSplash.addEventListener("click", event => {
  if (event.target === contactSplash) contactSplash.close();
});
contactForm.addEventListener("submit", event => {
  event.preventDefault();
  const name = contactForm.elements.name.value.trim();
  const email = contactForm.elements.email.value.trim();
  const message = contactForm.elements.message.value.trim();
  const subject = encodeURIComponent(`Photography enquiry from ${name}`);
  const body = encodeURIComponent(`${message}\n\n---\nFrom: ${name}\nEmail: ${email}`);
  window.location.href = `mailto:hello@example.com?subject=${subject}&body=${body}`;
});

async function loadPageGalleries(privateEventsOnly = false) {
  const endpoint = isEventsPage
    ? privateEventsOnly ? "/api/events/private" : "/api/events"
    : "/api/galleries";
  const response = await fetch(endpoint, { cache: "no-store" });
  if (!response.ok) throw new Error("The gallery could not be loaded.");
  if (!response.headers.get("content-type")?.includes("application/json"))
    throw new Error("The gallery service needs to be restarted before it can load this page.");
  const result = await response.json();
  if (!Array.isArray(result)) throw new Error("The gallery service returned an invalid response.");
  galleries = result.filter(gallery => !gallery.isPrivate || (isEventsPage && privateEventsOnly));
  featureLocalPhotos();
  renderFilters();
  renderGalleries();
}

if (isEventsPage) {
  const splashActions = document.querySelector("#splash-actions");
  const splashAccessNote = document.querySelector("#splash-access-note");
  const showPrivateEntry = document.querySelector("#show-private-entry");
  const backToSplash = document.querySelector("#back-to-splash");
  const enterUnlockedEvents = document.querySelector("#enter-unlocked-events");

  function showEventSplash(showKeyEntry = false) {
    splashActions.classList.toggle("hidden", showKeyEntry);
    eventKeyForm.classList.toggle("hidden", !showKeyEntry);
    showPrivateEntry.classList.toggle("hidden", showKeyEntry);
    backToSplash.classList.toggle("hidden", !showKeyEntry);
    enterUnlockedEvents.classList.toggle("hidden", !eventAccessUnlocked);
    eventKeyMessage.textContent = "";
    if (!eventSplash.open) eventSplash.showModal();
    if (showKeyEntry) document.querySelector("#event-key").focus();
  }

  function enterEvents() {
    if (eventSplash.open) eventSplash.close();
    document.querySelector("#events-work").scrollIntoView({ behavior: "smooth" });
  }

  async function enterPublicEvents() {
    try {
      await loadPageGalleries(false);
      enterEvents();
    } catch (loadError) {
      grid.replaceChildren();
      const message = document.createElement("p");
      message.className = "empty-note";
      message.textContent = loadError instanceof Error
        ? loadError.message
        : "Public event galleries are having a quiet moment. Please try again.";
      grid.append(message);
    }
  }

  document.querySelector("#browse-public-events").addEventListener("click", enterPublicEvents);
  document.querySelector("#dismiss-event-splash").addEventListener("click", enterPublicEvents);
  document.querySelectorAll("[data-open-event-access]").forEach(button => {
    button.addEventListener("click", () => showEventSplash());
  });
  showPrivateEntry.addEventListener("click", () => showEventSplash(true));
  backToSplash.addEventListener("click", () => showEventSplash());
  enterUnlockedEvents.addEventListener("click", async () => {
    try {
      await refreshEventGalleries(true);
      enterEvents();
    } catch (loadError) {
      splashAccessNote.textContent = loadError instanceof Error
        ? loadError.message
        : "Could not load your private gallery. Please try again.";
    }
  });

  eventSplash.addEventListener("click", event => {
    if (event.target === eventSplash) enterPublicEvents();
  });
  eventSplash.addEventListener("cancel", event => {
    event.preventDefault();
    enterPublicEvents();
  });

  eventKeyForm.addEventListener("submit", async event => {
    event.preventDefault();
    eventKeyMessage.textContent = "";
    const button = eventKeyForm.querySelector("button");
    button.disabled = true;
    try {
      const response = await fetch("/api/events/unlock", {
        method: "POST",
        credentials: "same-origin",
        headers: {
          "Content-Type": "application/json",
          RequestVerificationToken: antiforgeryToken
        },
        body: JSON.stringify({ key: eventKeyForm.elements.key.value })
      });
      if (response.status === 401) throw new Error("That key didn’t match. Please try again.");
      if (response.status === 429) throw new Error("Too many tries. Please wait a minute and try again.");
      if (response.status === 503) throw new Error("Private gallery access hasn’t been set up yet.");
      if (response.status === 400) {
        const result = await response.json();
        throw new Error(result.error || "Couldn’t unlock the private gallery. Please refresh and try again.");
      }
      if (!response.ok) throw new Error("Couldn’t unlock the private gallery. Please try again.");
      eventKeyForm.reset();
      eventAccessUnlocked = true;
      await refreshEventGalleries(true);
      splashAccessNote.textContent = "This private gallery is unlocked. Enter another key to switch galleries.";
      enterEvents();
    } catch (error) {
      eventKeyMessage.textContent = error.message;
    } finally {
      button.disabled = false;
    }
  });

  lockPrivateWork.addEventListener("click", async () => {
    try {
      const response = await fetch("/api/events/lock", { method: "POST", credentials: "same-origin" });
      if (!response.ok) throw new Error("Couldn’t lock the private galleries.");
      eventAccessUnlocked = false;
      galleries = galleries.filter(gallery => !gallery.isPrivate);
      renderFilters();
      renderGalleries();
      await refreshEventGalleries(false);
    } catch (error) {
      privateAccessNote.textContent = error.message;
    }
  });

  async function refreshEventGalleries(showUnlockedMessage) {
    await loadPageGalleries(showUnlockedMessage);
    const hasPrivateGalleries = galleries.some(gallery => gallery.isPrivate);
    lockPrivateWork.classList.toggle("hidden", !hasPrivateGalleries);
    privateAccessNote.textContent = showUnlockedMessage
      ? "Only this private gallery is visible."
      : "Public event galleries are open to everyone.";
  }

  fetch("/api/events/session", { cache: "no-store" })
    .then(async response => {
      if (!response.ok) throw new Error("Private gallery access could not be checked.");
      return response.json();
    })
    .then(async session => {
      document.body.classList.add("events-ready");
      antiforgeryToken = session.token;
      eventAccessUnlocked = Array.isArray(session.unlockedGalleryIds) &&
        session.unlockedGalleryIds.length > 0;
      if (!session.configured) {
        splashAccessNote.textContent = "Private gallery keys haven’t been set up yet. Public events are open to everyone.";
      }
      if (eventAccessUnlocked) {
        splashAccessNote.textContent = "Your private gallery is unlocked. Public galleries remain visible until you enter it.";
        enterUnlockedEvents.classList.remove("hidden");
      }
      if (shareTokenFromUrl) {
        try {
          const response = await fetch("/api/events/unlock", {
            method: "POST",
            credentials: "same-origin",
            headers: {
              "Content-Type": "application/json",
              RequestVerificationToken: antiforgeryToken
            },
            body: JSON.stringify({ shareToken: shareTokenFromUrl })
          });
          if (response.status === 401)
            throw new Error("This guest link is invalid or has been revoked.");
          if (response.status === 429)
            throw new Error("Too many tries. Please wait a minute and try again.");
          if (!response.ok) {
            const result = await response.json().catch(() => ({}));
            throw new Error(result.error || "Couldn’t open this guest link. Please try again.");
          }
          eventAccessUnlocked = true;
          await refreshEventGalleries(true);
          enterEvents();
        } catch (error) {
          eventSplash.showModal();
          showEventSplash(true);
          eventKeyMessage.textContent = error.message;
        }
      } else {
        eventSplash.showModal();
      }
    })
    .catch(() => {
      document.body.classList.add("events-ready");
      splashAccessNote.textContent = "Could not check private access. You can still browse public events.";
      eventSplash.showModal();
    });
} else {
  if (welcomeSplash) {
    document.querySelector("#enter-portfolio").addEventListener("click", dismissWelcomeSplash);
    document.querySelector("#dismiss-welcome-splash").addEventListener("click", dismissWelcomeSplash);
    welcomeSplash.addEventListener("cancel", event => {
      event.preventDefault();
      dismissWelcomeSplash();
    });
    const hasSeenWelcome = document.cookie.split(";").some(cookie => cookie.trim() === "portfolio-welcome=seen");
    if (!window.location.hash && !hasSeenWelcome) welcomeSplash.showModal();
  }

  loadPageGalleries().catch(() => {
    grid.replaceChildren();
    const error = document.createElement("p");
    error.className = "empty-note";
    error.textContent = "The gallery is having a quiet moment. Please refresh to try again.";
    grid.append(error);
  });
}
