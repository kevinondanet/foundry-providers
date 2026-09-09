"""Send notifications through a pluggable transport with retries."""

import logging
import time
from dataclasses import dataclass

LOGGER = logging.getLogger(__name__)


@dataclass(frozen=True)
class Message:
    recipient: str
    body: str


def send(message: Message, transport, *, attempts: int = 3, backoff_seconds: float = 0.5) -> bool:
    """Deliver the message, retrying transient transport errors with linear backoff.

    Returns True when the transport accepted the message within ``attempts`` tries.
    """
    for attempt in range(1, attempts + 1):
        try:
            if transport.deliver(message.recipient, message.body):
                return True
        except ConnectionError as exc:
            LOGGER.warning("delivery attempt %d/%d failed: %s", attempt, attempts, exc)
        if attempt < attempts:
            time.sleep(backoff_seconds * attempt)
    return False
