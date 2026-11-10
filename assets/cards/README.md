# Stream cards

Images the bot shows on the stream (spec 015). They are copied into the Docker image as `/app/cards/`.

| File | Shown | Image | Source / licence |
|---|---|---|---|
| `loading.png` | while a stream loads (start, and after a jump) | FuBK-style colour test card, 769×578 | Supplied by the maintainer (2026-11-05) as a Creative Commons test card from Wikimedia Commons, e.g. [IRIB Testcard (FuBK Version)](https://commons.wikimedia.org/wiki/File:IRIB_Testcard_(FuBK_Version).jpg); keep the attribution required by its Commons licence |
| `paused.png` | while paused | Pause symbol: two light bars on a dark background, 1280×720 | Generated for jellysend with FFmpeg (`drawbox`); dedicated to the public domain ([CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/)) |

`paused.png` was made with:

```bash
ffmpeg -f lavfi -i "color=c=0x1a1a1a:s=1280x720:d=1" -vf "drawbox=x=525:y=210:w=90:h=300:color=0xf2f2f2:t=fill,drawbox=x=665:y=210:w=90:h=300:color=0xf2f2f2:t=fill" -frames:v 1 paused.png
```

Only add images whose licence allows redistribution, and record their source and licence here.

Override at runtime with `STREAM_LOADING_IMAGE` / `STREAM_PAUSED_IMAGE` (a file path in the container), or set them
to `none` to turn a card off.
