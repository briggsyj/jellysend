"""Scrub credentials from URLs and free-form text before logging.

Jellyfin stream URLs carry the API key / access token twice (``ApiKey=`` from
the server and ``api_key=`` added by us), and FFmpeg/ffprobe echo the input URL
in their error output, so every log path that can contain a URL goes through
here.
"""

from __future__ import annotations

import re
from urllib.parse import parse_qsl, urlencode, urlparse, urlunparse

# Query parameters that carry credentials (matched case-insensitively).
SECRET_QUERY_KEYS = frozenset(
    {"api_key", "apikey", "token", "access_token", "password", "x-emby-token"}
)

_SECRET_PARAM_RE = re.compile(
    r"([?&;](?:" + "|".join(re.escape(k) for k in sorted(SECRET_QUERY_KEYS)) + r")=)"
    r"[^&\s\"'<>]+",
    re.IGNORECASE,
)
_USERINFO_RE = re.compile(r"(\b[a-z][a-z0-9+.-]*://)[^/\s@]+@", re.IGNORECASE)


def redact_url(url: str) -> str:
    """Drop user:pass and secret query params from a URL."""
    p = urlparse(url)
    netloc = (p.hostname or "") + (f":{p.port}" if p.port else "")
    query = [
        (k, v)
        for k, v in parse_qsl(p.query, keep_blank_values=True)
        if k.lower() not in SECRET_QUERY_KEYS
    ]
    return urlunparse(p._replace(netloc=netloc, query=urlencode(query)))


def redact_text(text: str) -> str:
    """Mask secrets in any text that may embed URLs (e.g. FFmpeg stderr)."""
    text = _SECRET_PARAM_RE.sub(r"\1***", text)
    return _USERINFO_RE.sub(r"\1***@", text)
