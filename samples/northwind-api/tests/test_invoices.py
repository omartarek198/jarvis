from decimal import Decimal

from app.invoices import Invoice, LineItem, apply_discount


def test_total_includes_tax():
    inv = Invoice("INV-1", [LineItem("Widget", 2, Decimal("10.00"))])
    assert inv.total() == Decimal("22.80")


def test_discount_reduces_total():
    inv = Invoice("INV-2", [LineItem("Widget", 1, Decimal("100.00"))])
    apply_discount(inv, Decimal("10"))
    assert inv.subtotal() == Decimal("90.00")
