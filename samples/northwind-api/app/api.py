"""Minimal HTTP surface (framework-free so the sample has no dependencies)."""
import json

from .invoices import Invoice, LineItem
from decimal import Decimal


def handle_create(body: str) -> str:
    data = json.loads(body)
    inv = Invoice(number=data["number"])
    for raw in data.get("items", []):
        inv.items.append(LineItem(raw["description"], int(raw["quantity"]), Decimal(str(raw["unit_price"]))))
    # TODO: validate that quantity is positive before accepting the invoice
    return json.dumps({"number": inv.number, "total": str(inv.total())})
