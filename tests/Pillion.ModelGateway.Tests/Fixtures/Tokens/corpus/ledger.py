"""A small double-entry ledger with CSV import and monthly summaries."""

from __future__ import annotations

import csv
import datetime as dt
from collections import defaultdict
from dataclasses import dataclass, field
from decimal import Decimal, InvalidOperation
from pathlib import Path
from typing import Iterable, Iterator


class LedgerError(Exception):
    """Raised when a transaction does not balance or an account is unknown."""


@dataclass(frozen=True)
class Posting:
    account: str
    amount: Decimal
    memo: str = ""


@dataclass
class Transaction:
    date: dt.date
    description: str
    postings: list[Posting] = field(default_factory=list)

    def total(self) -> Decimal:
        return sum((p.amount for p in self.postings), Decimal("0"))

    def is_balanced(self) -> bool:
        return self.total() == Decimal("0")


class Ledger:
    def __init__(self, accounts: Iterable[str]) -> None:
        self._accounts = set(accounts)
        self._transactions: list[Transaction] = []

    def __len__(self) -> int:
        return len(self._transactions)

    def __iter__(self) -> Iterator[Transaction]:
        return iter(sorted(self._transactions, key=lambda t: (t.date, t.description)))

    def add(self, transaction: Transaction) -> None:
        unknown = {p.account for p in transaction.postings} - self._accounts
        if unknown:
            raise LedgerError(f"unknown accounts: {', '.join(sorted(unknown))}")
        if not transaction.is_balanced():
            raise LedgerError(
                f"transaction on {transaction.date:%Y-%m-%d} is off by {transaction.total()}"
            )
        self._transactions.append(transaction)

    def balance(self, account: str, as_of: dt.date | None = None) -> Decimal:
        if account not in self._accounts:
            raise LedgerError(f"unknown account: {account}")
        total = Decimal("0")
        for transaction in self._transactions:
            if as_of is not None and transaction.date > as_of:
                continue
            for posting in transaction.postings:
                if posting.account == account:
                    total += posting.amount
        return total

    def monthly_summary(self, account: str) -> dict[str, Decimal]:
        months: dict[str, Decimal] = defaultdict(lambda: Decimal("0"))
        for transaction in self:
            key = transaction.date.strftime("%Y-%m")
            for posting in transaction.postings:
                if posting.account == account:
                    months[key] += posting.amount
        return dict(months)

    @classmethod
    def from_csv(cls, path: Path, accounts: Iterable[str]) -> "Ledger":
        """Reads rows of date, description, account, amount; rows with the same date and
        description form one transaction."""
        ledger = cls(accounts)
        grouped: dict[tuple[dt.date, str], list[Posting]] = defaultdict(list)
        with path.open(newline="", encoding="utf-8") as handle:
            for line_number, row in enumerate(csv.DictReader(handle), start=2):
                try:
                    date = dt.date.fromisoformat(row["date"].strip())
                    amount = Decimal(row["amount"].replace(",", "").strip())
                except (KeyError, ValueError, InvalidOperation) as error:
                    raise LedgerError(f"{path.name}:{line_number}: {error}") from error
                grouped[(date, row["description"].strip())].append(
                    Posting(row["account"].strip(), amount, row.get("memo", "").strip())
                )
        for (date, description), postings in grouped.items():
            ledger.add(Transaction(date, description, postings))
        return ledger


def format_amount(amount: Decimal, currency: str = "GBP") -> str:
    sign = "-" if amount < 0 else ""
    whole, _, fraction = f"{abs(amount):.2f}".partition(".")
    groups = []
    while whole:
        groups.insert(0, whole[-3:])
        whole = whole[:-3]
    return f"{sign}{currency} {','.join(groups)}.{fraction}"


if __name__ == "__main__":
    demo = Ledger(["assets:bank", "expenses:rent", "income:salary"])
    demo.add(
        Transaction(
            dt.date(2026, 9, 1),
            "September salary",
            [Posting("assets:bank", Decimal("3200.00")), Posting("income:salary", Decimal("-3200.00"))],
        )
    )
    demo.add(
        Transaction(
            dt.date(2026, 9, 3),
            "Rent",
            [Posting("expenses:rent", Decimal("1150.00")), Posting("assets:bank", Decimal("-1150.00"))],
        )
    )
    for month, total in demo.monthly_summary("assets:bank").items():
        print(month, format_amount(total))
