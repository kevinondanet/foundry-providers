"""Acceptance tests for textkit.slug.slugify (unittest, also collected by pytest)."""

import unittest

from textkit.slug import slugify


class TestSlugify(unittest.TestCase):
    def test_lowercases_and_hyphenates_words(self) -> None:
        # Arrange
        text = "Hello World"

        # Act
        result = slugify(text)

        # Assert
        self.assertEqual(result, "hello-world")

    def test_collapses_runs_of_separators_and_trims(self) -> None:
        self.assertEqual(slugify("  Hello,   World!!  "), "hello-world")

    def test_strips_accents(self) -> None:
        self.assertEqual(slugify("Crème Brûlée"), "creme-brulee")

    def test_keeps_digits(self) -> None:
        self.assertEqual(slugify("Release 2.0 notes"), "release-2-0-notes")

    def test_max_length_cuts_on_word_boundary(self) -> None:
        self.assertEqual(slugify("the quick brown fox", max_length=9), "the-quick")

    def test_empty_input_returns_empty_string(self) -> None:
        self.assertEqual(slugify(""), "")

    def test_rejects_non_string(self) -> None:
        with self.assertRaises(TypeError):
            slugify(None)  # type: ignore[arg-type]


if __name__ == "__main__":
    unittest.main()
