"""Inventory module."""
import os
from dataclasses import dataclass


@dataclass
class Item:
    """An item in stock."""

    name: str
    quantity: int = 0

    def restock(self, amount):
        """Adds stock."""
        self.quantity += amount
        return self.quantity

    @property
    def empty(self):
        return self.quantity == 0


def load(path):
    # Reads items from a file.
    with open(path) as handle:
        return [Item(line.strip()) for line in handle]


if __name__ == "__main__":
    print(load(os.environ.get("INVENTORY", "items.txt")))
