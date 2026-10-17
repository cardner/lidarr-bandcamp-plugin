# Docker Download Path (linuxserver / hotio Lidarr)

## Typical layout

linuxserver and hotio Lidarr images usually mount your host download library at **`/downloads`** inside the container:

```yaml
volumes:
  - /path/on/host/downloads:/downloads
```

The Bandcamp download client **Download Path** must be a folder **inside that mount** that the Lidarr process can create and write (same `PUID`/`PGID` as the container).

| Setting | Result |
| --- | --- |
| `/downloads/bandcamp` | Recommended — created under the mounted volume |
| `/downloads` | Works, but mixes Bandcamp archives with other clients |
| `/downloads/downloads` | Often wrong — only exists if you nested a second `downloads` folder on the host |
| A host path like `/home/you/Music` | Wrong inside Docker — that path is not the container mount |

## Why `/downloads/downloads/` fails

Operators sometimes paste a host path such as `/mnt/user/downloads` or double the segment after reading “downloads volume + downloads folder.” Inside the container:

1. Only `/downloads` is mounted.
2. `/downloads/downloads` may be **missing**, **not creatable**, or **not writable**.
3. Older plugin builds logged create/write failures at Debug and could leave Queue with an **empty** failure message.

This plugin now:

- **Test()** — creates the path if needed, proves a write probe, and warns when the path looks doubled (`…/downloads/downloads`).
- **Download / Queue** — refuses grabs when the root is not writable and sets `Message` to `Download Path not writable: '…'` (or a phase-tagged failure string).

## Compose example (this repo)

See `docker/docker-compose.lidarr.yml`:

```yaml
volumes:
  - ./lidarr-downloads:/downloads
```

Configure the download client **Download Path** as `/downloads/bandcamp` (not `/downloads/downloads`).

## Checklist

1. `docker exec <lidarr> ls -la /downloads`
2. Confirm your intended folder exists or can be created under `/downloads`
3. Set Download Path → **Test** (should pass; doubled path shows a warning)
4. Grab a release — Queue should show a clear path error if write still fails
