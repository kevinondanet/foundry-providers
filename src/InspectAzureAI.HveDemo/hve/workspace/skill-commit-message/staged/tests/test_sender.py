"""Tests for notify.sender.send retry behaviour."""

import unittest
from unittest import mock

from notify.sender import Message, send


class TestSendRetries(unittest.TestCase):
    def test_retries_then_succeeds(self) -> None:
        # Arrange
        transport = mock.Mock()
        transport.deliver.side_effect = [ConnectionError("down"), True]

        # Act
        with mock.patch("notify.sender.time.sleep"):
            accepted = send(Message("ops", "hi"), transport, attempts=3)

        # Assert
        self.assertTrue(accepted)
        self.assertEqual(transport.deliver.call_count, 2)
