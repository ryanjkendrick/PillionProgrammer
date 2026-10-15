"""Stock levels for the warehouse."""


class Inventory:
    """Counts items on the shelves."""

    def __init__(self):
        self._stock = {}

    def add(self, sku, quantity):
        self._stock[sku] = self._stock.get(sku, 0) + quantity

    def quantity(self, sku):
        return self._stock.get(sku, 0)
