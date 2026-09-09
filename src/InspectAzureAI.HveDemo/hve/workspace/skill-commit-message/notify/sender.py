"""Send notifications through a pluggable transport."""

from dataclasses import dataclass


@dataclass(frozen=True)
class Message:
    recipient: str
    body: str


def send(message: Message, transport) -> bool:
    """Deliver the message; returns True when the transport accepted it."""
    return bool(transport.deliver(message.recipient, message.body))
