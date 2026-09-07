from textkit import slugify


def test_lowercases_and_joins_words():
    assert slugify("Hello World") == "hello-world"


def test_collapses_surrounding_and_repeated_whitespace():
    assert slugify("  Multiple   spaces here ") == "multiple-spaces-here"


def test_single_word_is_unchanged():
    assert slugify("inspect") == "inspect"
