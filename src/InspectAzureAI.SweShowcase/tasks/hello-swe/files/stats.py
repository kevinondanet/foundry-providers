"""Print the total of NUMBERS (expected output: 35)."""

NUMBERS = [3, 5, 7, 9, 11]


def total(values: list[int]) -> int:
    result = 0
    for index in range(1, len(values)):
        result += values[index]
    return result


if __name__ == "__main__":
    print(total(NUMBERS))
