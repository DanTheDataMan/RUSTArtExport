# ArtExport

Save in-game photos and paintings from a Rust server as real image files.

Players make great art on signs and canvases, and take photos with the instant camera.
The game gives no way to save them to disk. A screenshot is a cropped, re-lit copy.
ArtExport copies the original image file the server already stores, byte for byte.

It is one `.cs` file. It works on **Carbon** and **Oxide/uMod**. It needs no other plugin.

## What it exports

- A photo you hold (instant camera photo).
- A photo hung in a photo frame, and any paint drawn over the frame.
- Painted signs, canvases, picture frames, banners and boards. A sign with several faces gives one file per face.
- A canvas standing on an easel.
- Carved pumpkins and neon signs.
- Picked-up signs and painted items in your inventory.
- Anything else in the game that stores an image through the game's sign or user-content interfaces (`ISignage`, `IUGCBrowserEntity`).

## Commands

| Command | Who | What it does |
|---|---|---|
| `/artexport` or `/saveart` | players | Export what you hold. If you hold nothing with a picture, export what you look at. |
| `/artexport all` | players | Export every photo and painted item in your inventory and backpack. |
| `/artexport help` | players | Short help. |
| `artexport` (F1 console) | players | Same as `/artexport`. Takes `all` too. |
| `artexport.list [steamid]` | admins | Count exported files per player, or list one player's files. |
| `artexport.purge <steamid\|all>` | admins | Delete one player's exports, or all of them. |
| `artexport.selftest [http]` | server console / RCON only | Built-in test. See below. |

Admins are the game's admins or anyone with `artexport.admin`.

## Permissions

| Permission | Meaning |
|---|---|
| `artexport.use` | Needed to export, unless `RequirePermission` is `false`. |
| `artexport.others` | Export art owned by another player, when `AllowOthersArt` is `false`. |
| `artexport.admin` | Use `artexport.list` and `artexport.purge` from the F1 console. |

Grant with `o.grant group default artexport.use` (Oxide) or `c.grant group default artexport.use` (Carbon).

"Owned by another player" means the entity's `OwnerID` is someone else, usually whoever placed or locked the sign.
Items in your own inventory always count as yours.

## Install

**Carbon:** copy `ArtExport.cs` into `carbon/plugins/`.

**Oxide/uMod:** copy `ArtExport.cs` into `oxide/plugins/`.

The plugin compiles and loads by itself. The config file is created on first load:
`carbon/configs/ArtExport.json` or `oxide/config/ArtExport.json`.
Run `c.reload ArtExport` or `o.reload ArtExport` after you change it.

## How players get their files

A server plugin cannot write to a player's computer. ArtExport offers three ways to deliver the file.

### 1. Saved on the server (always on)

Every export is written to the data folder:

```
carbon/data/ArtExport/<steamid>/<time>-<kind>-<n>.<ext>
oxide/data/ArtExport/<steamid>/<time>-<kind>-<n>.<ext>
```

The time is UTC. The extension comes from the file's own first bytes (`png`, `jpg`).
The oldest files are deleted first when a player goes over `MaxFilesPerPlayer`,
or when all exports together go over `MaxTotalMegabytes`.

The server owner can hand these files out any way they like.

### 2. Download links (off by default)

Turn on `DownloadLinks.Enabled`. The plugin starts a small web server.
After each export the player gets a link in chat and in the F1 console (copy it from F1).

- Each link holds a random 128-bit token. Nothing else in the address is used.
- A token maps to one saved file. Players never give a file path, so nobody can reach other files.
- Links run out after `LinkMinutes` (30) or after `MaxDownloadsPerLink` (5) downloads.
- Links are kept in memory. A plugin reload or server restart ends them all.
- Only `GET` and `HEAD` are answered. Everything else is refused.

Open the port in your firewall, or put a reverse proxy in front.
The listener speaks plain HTTP. For HTTPS, use a proxy and set `PublicBaseUrl`.

Example with Caddy, on the same machine as the game server:

```
art.example.com {
    reverse_proxy 127.0.0.1:28099
}
```

And in the config:

```json
"ListenPrefix (HttpListener prefix, must end with /)": "http://127.0.0.1:28099/artexport/",
"PublicBaseUrl (what players open; empty = built from the prefix and server.ip)": "https://art.example.com/artexport/"
```

With nginx:

```
location /artexport/ {
    proxy_pass http://127.0.0.1:28099/artexport/;
}
```

The path in `PublicBaseUrl` must reach the path in `ListenPrefix`.
If `PublicBaseUrl` is empty, links use `server.ip` (or 127.0.0.1) with the prefix's port and path.

On Windows, a prefix with `*` or `+` may need a URL reservation (`netsh http add urlacl`) or an administrator account.
Use `http://127.0.0.1:<port>/...` with a proxy to avoid that.

### 3. Discord webhook (off by default)

Set `Discord.Enabled` to `true` and paste a webhook URL. Each export is posted to that channel as an attachment,
with the player's name. Mentions in names are switched off.
Images over `MaxUploadBytes` (8 MB) are not posted. They are still saved on the server.

## Config

| Key | Default | Meaning |
|---|---|---|
| `RequirePermission` | `true` | Players need `artexport.use`. |
| `AllowOthersArt` | `true` | Anyone may export art owned by someone else. Set `false` to need `artexport.others`. |
| `CooldownSeconds` | `5` | Wait between exports, per player. |
| `LookRangeMeters` | `6` | How far away the thing you look at can be. |
| `MaxImageBytes` | `16777216` | Images larger than this are skipped (16 MB). |
| `MaxFilesPerPlayer` | `200` | Oldest files deleted past this. `0` = no limit. |
| `MaxTotalMegabytes` | `1024` | Oldest files of anyone deleted past this. `0` = no limit. |
| `DataSubfolder` | `ArtExport` | Folder name inside the data folder. |
| `DownloadLinks.Enabled` | `false` | Start the built-in web server. |
| `DownloadLinks.ListenPrefix` | `http://*:28099/artexport/` | Where the web server listens. Must end with `/`. |
| `DownloadLinks.PublicBaseUrl` | empty | The address players open, for a proxy. |
| `DownloadLinks.LinkMinutes` | `30` | How long a link works. |
| `DownloadLinks.MaxDownloadsPerLink` | `5` | Downloads before a link stops. `0` = no limit. |
| `Discord.Enabled` | `false` | Post each export to Discord. |
| `Discord.WebhookUrl` | empty | The channel's webhook URL. |
| `Discord.MaxUploadBytes` | `8388608` | Larger images are not posted. |
| `Discord.Message` | see config | Text with the post. Placeholders: `{player}`, `{steamid}`, `{kind}`, `{file}`. |

Player messages are in `lang/en/ArtExport.json` and can be translated.

## Image quality

ArtExport gives you the file the game itself sends to clients. It is never decoded or re-encoded.

- **Photos** are the JPEG the camera made when the photo was taken, at the size the photographer's client chose.
- **Signs, canvases and frames** are the PNG the painter's client uploaded, at the sign's texture size
  (up to 1024 x 1024 for most signs). PNG is lossless.

A screenshot of the same art is worse in several ways. It is redrawn with the game's lighting, fog and angle.
It is resampled to however many screen pixels the sign covers. It is usually saved as a compressed JPEG.
The export has none of that: it is the painting exactly as it was made.

## Limits and privacy

- Exported files stay on the server until they are pruned or purged. They include the player's Steam ID in the folder name.
- Download links are not secret from anyone the player shares them with. Anyone with a live link can download that one file.
- The web server is plain HTTP. Use a proxy with HTTPS on a public server.
- Art made by other players can be exported when `AllowOthersArt` is on. They can already be screenshotted,
  but some servers prefer to turn this off.
- The plugin logs each export (player, Steam ID, file names) to the server console.
- Discord posts go to whatever channel the webhook belongs to. Check that you are allowed to post the art there.

## For other plugins

- `object CanArtExport(BasePlayer player, BaseEntity source)`: return anything but `null` to block an export.
  `source` is `null` for held items and `/artexport all`.
- `void OnArtExported(BasePlayer player, List<string> paths)`: called after files are saved.

## Self test

`artexport.selftest` runs from the server console or RCON. It needs no player.

1. Spawns a wooden sign high above the map and stores a generated PNG as its texture.
2. Exports it through the normal code and checks the saved file is identical, byte for byte.
3. Does the same for a photo item holding JPEG data.
4. Checks the size limit and the per-player cap.
5. If download links are on (or with `artexport.selftest http`, which starts the listener for the test only),
   fetches the file over HTTP: a good token must return 200 and the same bytes; an unknown token, an expired token
   and a path-traversal attempt must fail.
6. Removes everything it made.

The HTTP results arrive a moment later in the server log. The last line is `SELFTEST PASSED` or `SELFTEST FAILED`.

## Licence

MIT. See `LICENSE`.
