"""Tests for textkit.slug.slugify."""

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

    def test_collapses_separator_runs_and_trims_edges(self) -> None:
        # Arrange
        text = "  Hello,   World!!  "

        # Act
        result = slugify(text)

        # Assert
        self.assertEqual(result, "hello-world")

    def test_strips_accents_to_ascii(self) -> None:
        # Arrange
        text = "Crème Brûlée"

        # Act
        result = slugify(text)

        # Assert
        self.assertEqual(result, "creme-brulee")

    def test_max_length_cuts_on_word_boundary(self) -> None:
        # Arrange
        text = "the quick brown fox"

        # Act
        result = slugify(text, max_length=9)

        # Assert
        self.assertEqual(result, "the-quick")

    def test_rejects_non_string_input_with_type_error(self) -> None:
        # Arrange
        value = 42

        # Act / Assert
        with self.assertRaises(TypeError):
            slugify(value)  # type: ignore[arg-type]
