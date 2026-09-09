"""URL slug generation."""

import re
import unicodedata

_SEPARATORS = re.compile(r"[^a-z0-9]+")


def slugify(text: str, *, max_length: int | None = None) -> str:
    """Convert ``text`` to a lowercase ASCII slug with single hyphens between words."""
    if not isinstance(text, str):
        raise TypeError(f"slugify expects a str, got {type(text).__name__}")
    ascii_text = unicodedata.normalize("NFKD", text).encode("ascii", "ignore").decode("ascii")
    slug = _SEPARATORS.sub("-", ascii_text.lower()).strip("-")
    if max_length is not None and len(slug) > max_length:
        head = slug[:max_length]
        if slug[max_length] != "-" and "-" in head:
            head = head.rsplit("-", 1)[0]
        slug = head.strip("-")
    return slug
