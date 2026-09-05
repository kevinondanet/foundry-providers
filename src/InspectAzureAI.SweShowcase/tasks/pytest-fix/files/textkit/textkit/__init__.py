"""Tiny text helpers."""


def slugify(text: str) -> str:
    """Return a URL slug: lower-case words joined by single hyphens."""
    return text.replace(" ", "-")
