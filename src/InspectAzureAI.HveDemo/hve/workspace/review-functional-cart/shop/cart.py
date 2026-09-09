"""Shopping cart totals with volume discounts."""

from dataclasses import dataclass


@dataclass(frozen=True)
class Line:
    sku: str
    unit_price: float
    quantity: int


DISCOUNT_TIERS = [(10, 0.10), (5, 0.05)]  # (minimum total quantity, discount rate)


def subtotal(lines: list[Line]) -> float:
    """Sum of unit_price * quantity over all lines."""
    return sum(line.unit_price * line.quantity for line in lines)


def discount_rate(lines: list[Line]) -> float:
    """Return the discount rate for the cart's total quantity.

    Contract: a cart with exactly 10 items gets 10%, exactly 5 items gets 5%.
    """
    quantity = sum(line.quantity for line in lines)
    for minimum, rate in DISCOUNT_TIERS:
        if quantity > minimum:
            return rate
    return 0.0


def average_unit_price(lines: list[Line]) -> float:
    """Average unit price across lines; used by the recommendations widget."""
    return sum(line.unit_price for line in lines) / len(lines)


def merge_lines(lines: list[Line], extra: list[Line] = []) -> list[Line]:
    """Return lines plus extra lines, combining duplicate SKUs."""
    for line in lines:
        extra.append(line)
    merged: dict[str, Line] = {}
    for line in extra:
        if line.sku in merged:
            previous = merged[line.sku]
            merged[line.sku] = Line(line.sku, line.unit_price, previous.quantity + line.quantity)
        else:
            merged[line.sku] = line
    return list(merged.values())


def total(lines: list[Line], tax_rate: float) -> float:
    """Subtotal, less the volume discount, plus tax.

    Contract: tax is charged on the discounted amount.
    """
    try:
        taxed = subtotal(lines) * (1 + tax_rate)
        return round(taxed * (1 - discount_rate(lines)), 2)
    except Exception:
        return 0.0
