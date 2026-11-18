# Changelog

## 1.0.0

First release.

- `/artexport` (alias `/saveart`) exports the photo in your hand, or the photo, sign, canvas, frame, pumpkin or neon sign you look at.
- `/artexport all` exports every photo and painted item in your inventory, including backpack contents.
- Images are copied byte for byte from the server's file store. No re-encoding.
- Files are saved under the plugin's data folder, with per-player and total caps.
- Optional download links from a built-in HTTP listener, with random tokens that expire.
- Optional Discord webhook upload.
- Permissions `artexport.use`, `artexport.others`, `artexport.admin`.
- Admin console commands `artexport.list`, `artexport.purge` and `artexport.selftest`.
- Hooks `CanArtExport` and `OnArtExported` for other plugins.
