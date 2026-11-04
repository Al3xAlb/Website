# Alex Photography

A lightweight photography portfolio with a public gallery and a separate private studio at `/admin`. The ASP.NET Core app serves both experiences and stores galleries as JSON; no database or third-party runtime packages are required.

## Run locally

```sh
ADMIN_EMAIL=you@example.com ADMIN_PASSWORD='choose-a-long-password' dotnet run
```

The app hashes the supplied password with PBKDF2 and writes the credentials to `data/admin.json`; the password itself is never stored. To change or reset the admin account, stop the app and start it with both `ADMIN_EMAIL` and `ADMIN_PASSWORD` set to the new values. This safely replaces the stored hash, so you do not need to delete `data/admin.json`. When neither variable is set, the existing account is left unchanged. Keep `data/admin.json` private and back it up securely.

By default, the server listens on all network interfaces at `http://0.0.0.0:8080`. Open `http://localhost:8080` on the host computer, or use the host computer's LAN IP address (for example, `http://192.168.1.25:8080`) from another device on the same network. On macOS, `ipconfig getifaddr en0` commonly prints the Wi-Fi address. If needed, allow incoming connections to the app in the host's firewall.

To select a different address or port, set `ASPNETCORE_URLS`, for example `ASPNETCORE_URLS=http://0.0.0.0:5000 dotnet run`. The public gallery is at `/` and the studio is at `/admin`. If no credentials have been provisioned, the studio remains locked; set `ADMIN_EMAIL` and `ADMIN_PASSWORD` and restart the app.

This default setup is intended for trusted local networks. Do not expose the HTTP port directly to the public internet: admin credentials and session cookies require HTTPS in transit. For remote access, put the app behind an HTTPS reverse proxy and configure the host/router firewall appropriately.

## Event galleries

Open `/events` for a welcome screen with a choice to browse public event galleries or enter a private access key. A fresh install includes three public sample event galleries to try the page. In the studio, mark a gallery as an event to include it there, then mark it private and set its unique 8-character access key. Each private gallery has its own key; entering one shows only its matching private gallery. From a saved private gallery in the studio, create a guest share link to let visitors unlock it without typing the key. Share links are random, stored only as hashes, and can be revoked or replaced in the studio; replacing a link immediately invalidates the previous one. Link access grants the same 8-hour session as entering a key. Keys are case-sensitive, hashed with PBKDF2, and stored in `data/private-gallery-keys.json`, never as plain text. If upgrading from the former shared event key, edit each private gallery in the studio and set its new key. Private galleries must use photos uploaded to this site so their image files can be access-controlled; their media is kept outside the public static-file directory and only delivered after entering a key or opening a share link for a private gallery that references it.

## Galleries and images

Gallery metadata and image URLs are kept in `data/galleries.json`. The admin studio can create, edit, and delete galleries, and upload JPEG, PNG, or WebP images up to 20 MB each. The browser processes uploads one at a time, scales large images to at most 2,400 pixels on the longest side, and uses WebP when that reduces file size. The server streams the resulting image to `wwwroot/uploads` under a generated file name.

To show photos already on disk, create a folder named `photos` beside the project file and add one subfolder per gallery. Put JPEG, PNG, or WebP files directly inside each subfolder, for example `photos/summer-trip/01.jpg`. The site scans these folders whenever the gallery is loaded, creates a gallery for every non-empty folder, and orders its photos by filename. New files appear after refreshing the page. Every folder-based gallery is public and read-only; a folder name cannot make an album private. To require a key, create or edit the gallery in the studio, mark it as a private event, upload its photos through the studio, and save it. Private photos must be uploaded so the server can check access before delivering each image. Local photo folders are excluded from source control by default.

The app also checks `data/uploads` and `photos` for new or changed images every 30 seconds. It creates 480-, 960-, and 1,600-pixel WebP cache sizes for faster gallery loading and stores them in `data/photo-cache`, outside the public static-file directory. Gallery pages use a cached size when it is ready and fall back to the original photo while it is being generated. Cached files are generated automatically and do not need to be backed up.

Gallery metadata and its image-access index are kept in memory after startup and updated when the studio saves changes, avoiding JSON reads on normal requests. Local photo albums are also cached in memory and refreshed when the `photos` folder changes. API responses are never cached, private uploaded photos use `no-store`, and JavaScript/CSS are revalidated before reuse so frontend changes do not get stuck behind an old bundle. Brotli or gzip compression is enabled for responses, using the fast compression setting.

Gallery data, uploads, and local photo folders are excluded from source control by default.

The starter galleries use remote Unsplash image URLs. Replace them with your own photos in the studio. Update the placeholder contact email (`hello@example.com`) in `wwwroot/index.html`. For production, serve the site over HTTPS and back up the `data` directory and `wwwroot/uploads` together.
