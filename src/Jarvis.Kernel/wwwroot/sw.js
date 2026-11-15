/* Minimal service worker: exists only so the mobile PWA is installable.
   Deliberately NO fetch handler — a fetch handler (even a no-op) routes every request on
   the origin through the SW thread, which made the desktop console laggy. */
self.addEventListener("install", function () { self.skipWaiting(); });
self.addEventListener("activate", function (e) { e.waitUntil(self.clients.claim()); });
