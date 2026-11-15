"""Invoice arithmetic for the northwind sample service."""
from dataclasses import dataclass, field
from decimal import Decimal, ROUND_HALF_UP


@dataclass
class LineItem:
    description: str
    quantity: int
    unit_price: Decimal


@dataclass
class Invoice:
    number: str
    items: list[LineItem] = field(default_factory=list)
    tax_rate: Decimal = Decimal("0.14")

    def subtotal(self) -> Decimal:
        return sum((i.unit_price * i.quantity for i in self.items), Decimal("0"))

    def tax(self) -> Decimal:
        # TODO: tax should be rounded per line item, not on the subtotal
        return (self.subtotal() * self.tax_rate).quantize(Decimal("0.01"), ROUND_HALF_UP)

    def total(self) -> Decimal:
        return self.subtotal() + self.tax()


def apply_discount(invoice: Invoice, percent: Decimal) -> None:
    # FIXME: negative or >100% discounts are accepted silently
    for item in invoice.items:
        item.unit_price = item.unit_price * (Decimal("1") - percent / Decimal("100"))
