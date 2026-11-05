# Stream cards

Images the bot shows on the stream (spec 015). They are copied into the Docker image as `/app/cards/`.

| File | Shown | Image | Source | Licence |
|---|---|---|---|---|
| `loading.jpg` | while a stream loads (start, and after a jump) | IRIB test card (FuBK version) | [Wikimedia Commons](https://commons.wikimedia.org/wiki/File:IRIB_Testcard_(FuBK_Version).jpg) | see the source page; attribution as given there |
| `paused.*` | while paused | _to be added_ | | |

Only add images whose licence allows redistribution, and record their author and licence here.

Override at runtime with `STREAM_LOADING_IMAGE` / `STREAM_PAUSED_IMAGE` (a file path in the container), or set them
to `none` to turn a card off.
