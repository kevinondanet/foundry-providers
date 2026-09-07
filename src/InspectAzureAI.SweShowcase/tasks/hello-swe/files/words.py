"""Print the words given on the command line, one per line."""

import sys


def main(argv: list[str]) -> None:
    for word in argv:
        print(word)


if __name__ == "__main__":
    main(sys.argv[1:])
