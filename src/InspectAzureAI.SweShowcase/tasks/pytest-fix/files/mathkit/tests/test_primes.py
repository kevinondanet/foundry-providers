import pytest

from mathkit import is_prime


@pytest.mark.parametrize("n", [2, 3, 5, 7, 11, 13, 97])
def test_primes_are_detected(n):
    assert is_prime(n)


@pytest.mark.parametrize("n", [0, 1, 4, 9, 25, 49, 100])
def test_composites_and_small_numbers_are_rejected(n):
    assert not is_prime(n)
