"""A nightly stock report."""

from inventory import Inventory


def low_stock_report(inventory, skus, threshold=5):
    """Lists the SKUs whose quantity is under the threshold."""
    return [sku for sku in skus if inventory.quantity(sku) < threshold]


def sample():
    stock = Inventory()
    stock.add("widget", 2)
    return low_stock_report(stock, ["widget"])
